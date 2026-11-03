using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MiningFleet.Agent;

/// <summary>
/// Restores factory NVIDIA GPU clocks, clock offsets, and default power limit when a GPU miner
/// that applied overclocking flags (<c>--gpu-cclock0</c>, <c>--gpu-coffset0</c>, <c>--gpu-plimit0</c>,
/// <c>--cclk</c>, <c>--coff</c>, etc.) is stopped — for example when <see cref="GpuPauseService"/>
/// hands the card back to a game.
///
/// Killing SRBMiner or lolMiner leaves locked clocks, core/memory offsets, and custom power limits
/// active in the NVIDIA driver, which would hold the card at 40 W idle or push gaming Boost clocks
/// past stability.
/// </summary>
internal static class NvidiaOcReset
{
    private static readonly string[] OcFlagPrefixes =
    [
        "--gpu-cclock",
        "--gpu-mclock",
        "--gpu-coffset",
        "--gpu-moffset",
        "--gpu-plimit",
        "--gpu-fan",
        "--cclk",
        "--mclk",
        "--coff",
        "--moff",
        "--pl",
    ];

    public static bool HasOcFlags(IReadOnlyList<string>? extraArgs)
    {
        if (extraArgs is null || extraArgs.Count == 0) return false;
        foreach (var arg in extraArgs)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            var trimmed = arg.Trim();
            foreach (var prefix in OcFlagPrefixes)
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    public static async Task TryResetAsync(ILogger log, CancellationToken ct)
    {
        if (TryResetViaNvml(log)) return;
        await TryResetViaNvidiaSmiAsync(log, ct);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlClockOffsetV1
    {
        public uint Version;
        public int Type;
        public int PState;
        public int ClockOffsetMHz;
        public int MinClockOffsetMHz;
        public int MaxClockOffsetMHz;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetCountDelegate(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetHandleDelegate(uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlSetClockOffsetsDelegate(IntPtr device, ref NvmlClockOffsetV1 info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlResetLockedClocksDelegate(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetDefaultPowerLimitDelegate(IntPtr device, out uint defaultLimitMw);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlSetPowerLimitDelegate(IntPtr device, uint limitMw);

    private static bool TryResetViaNvml(ILogger log)
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["nvml.dll"]
            : ["libnvidia-ml.so.1", "libnvidia-ml.so"];

        IntPtr lib = IntPtr.Zero;
        foreach (var name in candidates)
        {
            if (NativeLibrary.TryLoad(name, out lib)) break;
        }
        if (lib == IntPtr.Zero) return false;

        try
        {
            if (!TryExport<NvmlInitDelegate>(lib, "nvmlInit_v2", out var init) || init!() != 0)
                return false;
            if (!TryExport<NvmlGetCountDelegate>(lib, "nvmlDeviceGetCount_v2", out var getCount) || getCount!(out var count) != 0)
                return false;
            if (!TryExport<NvmlGetHandleDelegate>(lib, "nvmlDeviceGetHandleByIndex_v2", out var getHandle))
                return false;

            TryExport<NvmlSetClockOffsetsDelegate>(lib, "nvmlDeviceSetClockOffsets", out var setOffsets);
            TryExport<NvmlResetLockedClocksDelegate>(lib, "nvmlDeviceResetGpuLockedClocks", out var resetClocks);
            TryExport<NvmlGetDefaultPowerLimitDelegate>(lib, "nvmlDeviceGetPowerManagementDefaultLimit", out var getDefaultPl);
            TryExport<NvmlSetPowerLimitDelegate>(lib, "nvmlDeviceSetPowerManagementLimit", out var setPl);

            var structVersion = (uint)Marshal.SizeOf<NvmlClockOffsetV1>() | (1u << 24);
            for (uint i = 0; i < count; i++)
            {
                if (getHandle!(i, out var dev) != 0 || dev == IntPtr.Zero) continue;

                if (setOffsets is not null)
                {
                    // Reset Graphics (0) and Memory (2) offsets across P0 (0) and P2 (2).
                    foreach (var clockType in new[] { 0, 2 })
                    {
                        foreach (var pstate in new[] { 0, 2 })
                        {
                            var info = new NvmlClockOffsetV1
                            {
                                Version = structVersion,
                                Type = clockType,
                                PState = pstate,
                                ClockOffsetMHz = 0,
                            };
                            setOffsets(dev, ref info);
                        }
                    }
                }

                resetClocks?.Invoke(dev);

                if (getDefaultPl is not null && setPl is not null && getDefaultPl(dev, out var defMw) == 0 && defMw > 0)
                    setPl(dev, defMw);
            }

            log.LogInformation("Reset NVIDIA GPU clocks, offsets, and power limits for {Count} device(s).", count);
            return true;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "NVML OC reset failed; falling back to nvidia-smi");
            return false;
        }
    }

    private static bool TryExport<T>(IntPtr handle, string name, out T? fn) where T : Delegate
    {
        if (NativeLibrary.TryGetExport(handle, name, out var addr))
        {
            fn = Marshal.GetDelegateForFunctionPointer<T>(addr);
            return true;
        }
        fn = null;
        return false;
    }

    private static async Task TryResetViaNvidiaSmiAsync(ILogger log, CancellationToken ct)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "-rgc",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            proc.Start();
            await proc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "nvidia-smi -rgc fallback skipped");
        }
    }
}
