using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XmrigFleet.Agent;

/// <summary>
/// Keeps whole physical cores out of the miner's hands, for whoever is sitting at the machine.
///
/// This exists because the two obvious levers both failed on real hardware, and the measurements
/// are worth keeping beside the code that replaced them.
///
/// <para><b>Lowering the miner's priority is a trap on a hybrid CPU.</b> Dropped to BelowNormal on
/// an i7-12700KF, Windows 11 read the miner as background work and parked it on the four E-cores:
/// 7,126 H/s became 1,058, an 85% loss, while the eight P-cores sat idle. It is not a dial, it is
/// a switch.</para>
///
/// <para><b>A job-object cap costs more than it frees.</b> Measured on the same fleet: rung 50 kept
/// 27.6% of the hashrate and rung 25 kept 14.7%, because a hard cap freezes the miner's threads
/// and RandomX loses its scratchpad to whatever runs during the freeze.</para>
///
/// <para><b>Affinity does neither.</b> Nothing is frozen and nothing is demoted; the miner simply
/// runs on fewer CPUs. Freeing two P-cores on that node cost <b>7%</b> - 7,126 to 6,642 H/s - and
/// gave the person at the machine two whole cores, which is what actually stopped a game from
/// stuttering. It applies instantly, needs no restart, and leaves the RandomX dataset and the huge
/// pages exactly where they are.</para>
///
/// Cores rather than logical CPUs, because half a core is not a core: the sibling hyperthread of a
/// saturated RandomX thread shares its execution units and delivers a fraction of one. Handing back
/// "two CPUs" that turn out to be two halves of two busy cores would look like a fix and change
/// nothing.
/// </summary>
public static class CpuReservation
{
    /// <summary>
    /// Which logical CPUs the miner may use when <paramref name="reserved"/> physical cores are
    /// held back, given the machine's cores as affinity masks.
    ///
    /// Pure and taking the topology as an argument, so the arithmetic can be tested against a
    /// hybrid CPU's awkward shape without owning one.
    /// </summary>
    /// <param name="cores">One mask per physical core, in the order the OS reports them.</param>
    /// <param name="reserved">Physical cores to hand back. Zero or less reserves nothing.</param>
    /// <returns>The miner's mask, or null when nothing should be restricted.</returns>
    public static ulong? MaskFor(IReadOnlyList<ulong> cores, int reserved)
    {
        if (reserved <= 0 || cores.Count == 0) return null;

        // Never reserve the whole machine. A node set to hand back more cores than it has is a
        // typo or a config copied from a bigger rig, and the right answer to it is a slower miner
        // rather than a stopped one - a miner with an empty affinity mask does not run at all, and
        // would look exactly like a miner that crashed.
        var take = Math.Min(reserved, cores.Count - 1);

        ulong mask = 0;
        for (var i = take; i < cores.Count; i++) mask |= cores[i];

        // The first cores are handed back rather than the last because on Intel's hybrid parts the
        // performance cores come first, and a person wants a fast core, not an efficient one.
        return mask == 0 ? null : mask;
    }

    /// <summary>
    /// The machine's physical cores, each as a mask of the logical CPUs belonging to it.
    ///
    /// Read from the OS rather than worked out from core and thread counts, because that sum is
    /// wrong on exactly the CPUs this matters most on: an i7-12700KF reports 12 cores and 20
    /// logical processors, and "two logical per core" would be wrong for all four E-cores.
    ///
    /// Returns an empty list on a platform or a machine that will not answer, which the caller
    /// reads as "reserve nothing" - a node that cannot see its own topology must not guess at it.
    /// </summary>
    public static IReadOnlyList<ulong> PhysicalCores()
    {
        if (!OperatingSystem.IsWindows()) return [];

        try
        {
            uint length = 0;
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length)
                && Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                return [];

            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                    return [];

                var cores = new List<ulong>();
                var offset = 0;
                while (offset < length)
                {
                    var record = buffer + offset;
                    var size = (int)Marshal.ReadInt32(record, sizeof(int));
                    if (size <= 0) break;

                    // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship, Size, then the union.
                    // For a core that is PROCESSOR_RELATIONSHIP { Flags, EfficiencyClass, byte[20]
                    // Reserved, GroupCount, GROUP_AFFINITY[] }, and GROUP_AFFINITY starts with the
                    // KAFFINITY mask. Only group 0 is read: a machine with more than 64 logical
                    // CPUs has processor groups, and this whole feature is about desktops.
                    var groupCount = Marshal.ReadInt16(record, GroupCountOffset);
                    if (groupCount > 0)
                    {
                        var mask = (ulong)Marshal.ReadInt64(record, GroupAffinityOffset);
                        if (mask != 0) cores.Add(mask);
                    }

                    offset += size;
                }

                return cores;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>
    /// The logical CPUs a RandomX thread should sit on, one per physical core, in the order the OS
    /// reports them. The length of this list is the node's full mining capacity.
    ///
    /// Derived rather than read back from the miner because the miner's own list is what gets
    /// rewritten: once it has been cut to eight threads, it no longer says what twelve would be,
    /// and an agent restarting into a reduced node would take the reduction for the ceiling and
    /// never let it back up.
    ///
    /// It reproduces what xmrig picks unaided. On the fleet's i7-12700KF xmrig chose
    /// <c>0,2,4,6,8,10,12,14,16,17,18,19</c> — the first thread of each of eight P-cores, then the
    /// four single-threaded E-cores — and this returns exactly that.
    /// </summary>
    public static IReadOnlyList<int> MiningCpus(IReadOnlyList<ulong> cores) =>
        cores.Select(mask => System.Numerics.BitOperations.TrailingZeroCount(mask)).ToArray();

    /// <summary>
    /// How many RandomX threads this node runs at full speed: one per physical core, but no more
    /// than the L3 cache can hold at 2 MB of scratchpad each — xmrig's own rule, and the reason a
    /// cache-starved machine is slower than its core count suggests.
    ///
    /// Checked against all three nodes in this fleet: 12 cores and 25 MB of L3 give 12, matching
    /// what xmrig chose; 14 cores and 35 MB give 14; 6 cores and 12 MB give 6.
    /// </summary>
    /// <param name="cores">Physical cores available for mining.</param>
    /// <param name="l3Bytes">L3 cache size in bytes, or 0 when the machine will not say.</param>
    public static int FullThreadCount(int cores, long l3Bytes)
    {
        if (cores <= 0) return 0;
        if (l3Bytes <= 0) return cores;

        var cacheAllows = (int)(l3Bytes / (2L * 1024 * 1024));
        return Math.Max(1, Math.Min(cores, cacheAllows));
    }

    /// <summary>
    /// The thread count a percentage ceiling permits.
    ///
    /// Rounded down, because this is a ceiling and rounding up walks through it: 67% of twelve
    /// threads is 8.04, and calling that nine would run the node at 75% of full speed under a
    /// setting that says 67.
    ///
    /// Floored at one thread rather than zero, so a low percentage gives a slow miner and never a
    /// stopped one. Stopping is what the throttle's level 0 is for, and a ceiling that silently
    /// stopped a rig would be indistinguishable from a crash.
    /// </summary>
    public static int ThreadsFor(int fullThreads, int? maxPercent)
    {
        if (fullThreads <= 0) return 0;
        if (maxPercent is not { } pct || pct >= 100) return fullThreads;

        return Math.Clamp(fullThreads * pct / 100, 1, fullThreads);
    }

    /// <summary>
    /// Total L3 cache in bytes, or 0 when the machine will not say — which callers read as "cache
    /// is not the binding constraint", the right answer whenever it is not.
    ///
    /// Read through the same call as the topology rather than through WMI: a WMI query is a round
    /// trip to a service that one node in this fleet answers "RPC server unavailable" from, and
    /// that service has already taken an agent down once.
    /// </summary>
    public static long L3CacheBytes()
    {
        if (!OperatingSystem.IsWindows()) return 0;

        try
        {
            uint length = 0;
            if (!GetLogicalProcessorInformationEx(RelationCache, IntPtr.Zero, ref length)
                && Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                return 0;

            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationCache, buffer, ref length)) return 0;

                long total = 0;
                var offset = 0;
                while (offset < length)
                {
                    var record = buffer + offset;
                    var size = (int)Marshal.ReadInt32(record, sizeof(int));
                    if (size <= 0) break;

                    // CACHE_RELATIONSHIP begins after Relationship and Size: Level is the first
                    // byte and CacheSize a DWORD four bytes later, past Associativity and LineSize.
                    if (Marshal.ReadByte(record, CacheLevelOffset) == 3)
                        total += (uint)Marshal.ReadInt32(record, CacheSizeOffset);

                    offset += size;
                }

                return total;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or OutOfMemoryException)
        {
            return 0;
        }
    }

    private const uint RelationProcessorCore = 0;
    private const uint RelationCache = 2;

    // Relationship (4) + Size (4), then CACHE_RELATIONSHIP: Level, Associativity, LineSize (2).
    private const int CacheLevelOffset = 8;
    private const int CacheSizeOffset = 12;
    private const int ErrorInsufficientBuffer = 122;

    // Relationship (4) + Size (4) + Flags (1) + EfficiencyClass (1) + Reserved (20) = 30.
    private const int GroupCountOffset = 30;
    private const int GroupAffinityOffset = 32;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        uint relationshipType, IntPtr buffer, ref uint returnedLength);
}

/// <summary>
/// Applies the reservation to whichever miner is running, and keeps applying it.
///
/// A background service rather than something <see cref="MinerService"/> does at start-up, because
/// process affinity does not survive the thing it is set on: a miner restarted by the throttle, by
/// an operator, or by the agent's own autostart after a reboot comes back with the machine's full
/// mask. The setting has to be re-asserted, not set once - and a node whose reservation quietly
/// stopped applying is a node whose owner finds the stutter back with nothing to point at.
/// </summary>
public sealed class CpuReservationService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    private readonly MinerConfigStore _config;
    private readonly ILogger<CpuReservationService> _log;
    private readonly IReadOnlyList<ulong> _cores = CpuReservation.PhysicalCores();

    private int _appliedPid;

    public CpuReservationService(MinerConfigStore config, ILogger<CpuReservationService> log)
    {
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Windows only, like the job object the throttle uses. Linux has the same idea under a
        // different name (sched_setaffinity), and a node there would need its own reader for the
        // topology as well - so the honest thing is to stand aside rather than half-apply it.
        if (!OperatingSystem.IsWindows())
        {
            _log.LogInformation("CPU reservation: not implemented on this platform; no cores will be reserved.");
            return;
        }

        if (_cores.Count == 0)
            _log.LogInformation("CPU reservation: this machine did not report its core topology, so no cores can be reserved.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { Apply(); }
            catch (Exception ex)
            {
                // The same rule the throttle and the GPU pause follow: this service exists to make
                // a machine pleasant to use and must never be why a node stops answering.
                _log.LogDebug(ex, "CPU reservation tick failed");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void Apply()
    {
        var reserved = _config.Current.ReservedCores ?? 0;
        var wanted = CpuReservation.MaskFor(_cores, reserved);

        foreach (var miner in Process.GetProcessesByName("xmrig"))
        {
            using (miner)
            {
                if (miner.HasExited) continue;

                try
                {
                    var current = (ulong)miner.ProcessorAffinity.ToInt64();
                    var target = wanted ?? FullMask();
                    if (current == target) continue;

                    miner.ProcessorAffinity = (IntPtr)(long)target;

                    // Logged at every change because this costs hashrate on purpose, and an
                    // operator comparing two nodes' rates deserves to find the reason on the node.
                    if (wanted is null)
                        _log.LogInformation("CPU reservation: lifted, xmrig (pid {Pid}) may use every core again.", miner.Id);
                    else
                        _log.LogInformation(
                            "CPU reservation: {Reserved} core(s) held back from xmrig (pid {Pid}); its mask is now 0x{Mask:X}.",
                            Math.Min(reserved, _cores.Count - 1), miner.Id, target);

                    _appliedPid = miner.Id;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // A miner started by another user needs privileges the agent may not have.
                    // Reported once per process rather than every five seconds.
                    if (_appliedPid != miner.Id)
                    {
                        _appliedPid = miner.Id;
                        _log.LogWarning(ex, "CPU reservation: could not set the affinity of xmrig (pid {Pid})", miner.Id);
                    }
                }
            }
        }
    }

    /// <summary>Every logical CPU this process can see, for lifting a reservation.</summary>
    private static ulong FullMask()
    {
        var count = Environment.ProcessorCount;
        return count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
    }
}
