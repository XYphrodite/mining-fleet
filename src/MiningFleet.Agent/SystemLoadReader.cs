using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MiningFleet.Agent;

/// <summary>A reading of how busy the machine is, with the miner's own share separated out.</summary>
/// <param name="TotalCpuPercent">Everything, miner included.</param>
/// <param name="MinerCpuPercent">The miner alone.</param>
/// <param name="OtherCpuPercent">What is left: the load the throttle is meant to get out of the way of.</param>
/// <param name="MemoryUsedPercent">Physical memory in use.</param>
/// <param name="Usable">
/// False when this sample cannot be trusted and the caller must hold its current decision.
/// See <see cref="SystemLoadReader.Read"/> for the two ways that happens.
/// </param>
public readonly record struct SystemLoad(
    double TotalCpuPercent,
    double MinerCpuPercent,
    double OtherCpuPercent,
    double MemoryUsedPercent,
    bool Usable);

/// <summary>
/// Cheap, once-a-second load sampling for the throttle.
///
/// Deliberately not <see cref="HardwareService"/>: that walks every LibreHardwareMonitor sensor
/// on the machine and takes seconds, which is fine for a screen refresh and useless for a control
/// loop. Two kernel calls and one process handle are enough for what the ladder is read against.
///
/// The miner's own CPU time is subtracted for a reason that is easy to miss: if the throttle read
/// total load, capping the miner would lower that load, which would let the miner back up, which
/// would raise it again. The machine would spend its life oscillating and never settle. What the
/// ladder must react to is the load the miner does not cause.
/// </summary>
public sealed class SystemLoadReader
{
    private const string MinerProcessName = "xmrig";

    private long _lastIdle, _lastKernel, _lastUser;
    private long _lastMinerTicks;
    private bool _primed;

    /// <summary>
    /// Load since the previous call.
    ///
    /// Two situations produce an unusable sample, and both matter more than they look:
    ///
    /// 1. The miner restarted between samples, so its accumulated CPU time went backwards. Taking
    ///    its share as zero would count the new miner's own spin-up as somebody else's work, and
    ///    a caller reading the ladder would stop the miner an operator had just started by hand.
    /// 2. A miner is running but its CPU time cannot be read - an agent without the rights to
    ///    query another user's process. Its share then reads as zero forever, the miner's own load
    ///    counts as everybody else's, and the throttle stops it, sees a quiet machine, starts it,
    ///    and stops it again. An unusable sample is far better than that loop.
    ///
    /// The first call after construction or <see cref="Reset"/> is unusable for a duller reason:
    /// a CPU percentage is the difference between two samples and there is only one.
    /// </summary>
    public SystemLoad Read()
    {
        var memory = ReadMemoryPercent();

        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return Unusable(memory);

        var idleTicks = ToTicks(idle);
        var kernelTicks = ToTicks(kernel);
        var userTicks = ToTicks(user);
        var (minerTicks, minerKnown) = ReadMinerTicks();

        if (!_primed)
        {
            Remember(idleTicks, kernelTicks, userTicks, minerTicks);
            _primed = true;
            return Unusable(memory);
        }

        // GetSystemTimes counts kernel time with idle time inside it, and every figure is summed
        // across all cores - the same units Process.TotalProcessorTime uses, which is what makes
        // the subtraction below valid.
        var totalDelta = (kernelTicks - _lastKernel) + (userTicks - _lastUser);
        var idleDelta = idleTicks - _lastIdle;
        var minerDelta = minerTicks - _lastMinerTicks;

        Remember(idleTicks, kernelTicks, userTicks, minerTicks);

        if (totalDelta <= 0) return Unusable(memory);
        if (minerDelta < 0 || !minerKnown) return Unusable(memory);

        var total = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
        var miner = Math.Clamp(100.0 * minerDelta / totalDelta, 0, 100);

        return new SystemLoad(
            Math.Round(total, 1),
            Math.Round(miner, 1),
            Math.Round(Math.Max(0, total - miner), 1),
            memory,
            Usable: true);
    }

    private void Remember(long idle, long kernel, long user, long miner) =>
        (_lastIdle, _lastKernel, _lastUser, _lastMinerTicks) = (idle, kernel, user, miner);

    private static SystemLoad Unusable(double memory) => new(0, 0, 0, memory, Usable: false);

    /// <summary>Forgets the previous sample, so the next reading starts a fresh difference.</summary>
    public void Reset() => _primed = false;

    /// <summary>
    /// The miner's combined CPU time, and whether it is really known.
    ///
    /// "Known" is not the same as "non-zero": no miner at all is a perfectly good answer of zero,
    /// while a miner whose time could not be read is no answer and must be said so. Conflating
    /// the two is what would make the throttle mistake the miner's own load for somebody else's.
    /// </summary>
    private static (long Ticks, bool Known) ReadMinerTicks()
    {
        Process[] found;
        try { found = Process.GetProcessesByName(MinerProcessName); }
        catch (InvalidOperationException) { return (0, false); }

        long ticks = 0;
        var read = 0;

        foreach (var process in found)
        {
            // Several miner processes are unusual but possible - one adopted, one started - and
            // the throttle cares about their combined share, not about which is which.
            try
            {
                ticks += process.TotalProcessorTime.Ticks;
                read++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Exited between the listing and the read, or refused a handle.
            }
            finally { process.Dispose(); }
        }

        return (ticks, read == found.Length);
    }

    private static double ReadMemoryPercent()
    {
        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status)) return status.dwMemoryLoad;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not Windows, or a stripped API set. Memory simply goes unreported.
        }
        return 0;
    }

    private static long ToTicks(FileTime time) => ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public int dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys;
        public ulong ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
