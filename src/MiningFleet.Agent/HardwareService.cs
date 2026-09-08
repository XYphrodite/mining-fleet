using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>
/// Reads CPU/GPU/RAM sensors through LibreHardwareMonitor. Most temperature and power
/// sensors need an elevated process on Windows, so everything here degrades to null
/// rather than throwing when a sensor is unavailable.
/// </summary>
public sealed class HardwareService : IDisposable
{
    private readonly ILogger<HardwareService> _log;
    private readonly MinerConfigStore _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private bool _opened;

    public HardwareService(MinerConfigStore config, ILogger<HardwareService> log)
    {
        _config = config;
        _log = log;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsControllerEnabled = false,
        };
    }

    public async Task<HardwareDto> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            EnsureOpen();
            _computer.Accept(_visitor);
            return Build();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hardware read failed");
            return new HardwareDto
            {
                CpuName = "unavailable",
                LogicalCores = Environment.ProcessorCount,
                EstimatedPowerWatts = _config.Current.PowerFallbackWatts,
                PowerIsMeasured = false,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureOpen()
    {
        if (_opened) return;
        _computer.Open();
        _opened = true;
    }

    private HardwareDto Build()
    {
        var sensors = new List<SensorDto>();
        var gpus = new List<GpuDto>();

        string cpuName = "unknown";
        double? cpuTemp = null, cpuLoad = null, cpuPower = null;
        double memUsed = 0, memAvailable = 0;
        string? motherboard = null;
        var physicalCores = 0;

        foreach (var hardware in _computer.Hardware)
        {
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    cpuName = hardware.Name;
                    cpuTemp = PickTemperature(hardware);
                    cpuLoad = Find(hardware, SensorType.Load, "CPU Total");
                    cpuPower = Find(hardware, SensorType.Power, "CPU Package") ?? Find(hardware, SensorType.Power, "Package");
                    physicalCores = CountPhysicalCores(hardware);
                    break;

                case HardwareType.Memory:
                    memUsed = Find(hardware, SensorType.Data, "Memory Used") ?? 0;
                    memAvailable = Find(hardware, SensorType.Data, "Memory Available") ?? 0;
                    break;

                case HardwareType.Motherboard:
                    motherboard = hardware.Name;
                    break;

                case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                    gpus.Add(new GpuDto(
                        hardware.Name,
                        PickTemperature(hardware),
                        Find(hardware, SensorType.Load, "GPU Core"),
                        Find(hardware, SensorType.Power, "GPU Package") ?? Find(hardware, SensorType.Power, "GPU Power"),
                        Find(hardware, SensorType.SmallData, "GPU Memory Used"),
                        Find(hardware, SensorType.SmallData, "GPU Memory Total")));
                    break;
            }

            CollectSensors(hardware, sensors);
        }

        // A zero CPU package reading is not a measurement: on Windows the MSR-based power
        // and temperature sensors go silent whenever another tool holds the ring0 driver,
        // and xmrig with its MSR mod enabled is exactly such a tool. Treating that zero as
        // real would understate the electricity cost of the machine that draws the most.
        var cpuPowerMeasured = cpuPower is > 0;
        var gpuPower = gpus.Sum(g => g.PowerWatts ?? 0);
        var measured = cpuPowerMeasured;

        // Sensors only cover the silicon. Add a flat allowance for board, RAM, drives,
        // fans and PSU loss so the electricity cost is not wildly optimistic.
        double? power = cpuPowerMeasured
            ? cpuPower!.Value + gpuPower + IdleOverheadWatts
            : _config.Current.PowerFallbackWatts
              ?? (gpuPower > 0 ? gpuPower + IdleOverheadWatts : null);

        return new HardwareDto
        {
            CpuName = cpuName,
            PhysicalCores = physicalCores > 0 ? physicalCores : Environment.ProcessorCount,
            LogicalCores = Environment.ProcessorCount,
            CpuTemperatureC = cpuTemp,
            CpuLoadPercent = cpuLoad,
            CpuPowerWatts = cpuPowerMeasured ? cpuPower : null,
            MemoryTotalGb = Math.Round(memUsed + memAvailable, 1),
            MemoryUsedGb = Math.Round(memUsed, 1),
            MotherBoard = motherboard,
            Gpus = gpus,
            Sensors = sensors,
            EstimatedPowerWatts = power is null ? null : Math.Round(power.Value, 1),
            PowerIsMeasured = measured,
            SensorNotice = DescribeMissingCpuSensors(cpuTemp, cpuPower),
        };
    }

    /// <summary>
    /// CPU temperature and package power come from model-specific registers, which need kernel
    /// access. This version of LibreHardwareMonitor gets there through the PawnIO driver, which
    /// is installed separately; without it the library still starts and reports whatever needs
    /// no kernel access. That is why a machine can show GPU numbers - those come from the
    /// vendor user-mode API - and nothing at all for the CPU.
    ///
    /// Naming the missing piece beats leaving an operator to guess why one column is empty.
    /// </summary>
    private static string? DescribeMissingCpuSensors(double? cpuTemp, double? cpuPower)
    {
        if (cpuTemp is not null && cpuPower is > 0) return null;
        if (!OperatingSystem.IsWindows()) return null;

        if (FindPawnIoVersion() is null)
        {
            return "PawnIO is not installed, so CPU temperature and package power stay empty: "
                 + "LibreHardwareMonitor reads those through it. Install it from pawnio.eu. "
                 + "Until then this node falls back to powerFallbackWatts.";
        }

        return IsElevated()
            ? "PawnIO is installed but the CPU still reports no temperature or package power."
            : "The agent is not elevated, so CPU temperature and power sensors stay blank.";
    }

    /// <summary>
    /// Reads the exact registry value LibreHardwareMonitor itself probes for PawnIO
    /// (Uninstall\PawnIO\DisplayVersion), so this diagnostic cannot disagree with what the
    /// library actually does. Returns the installed version, or null when absent.
    /// </summary>
    private static string? FindPawnIoVersion()
    {
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var path in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO",
                 })
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                if (key?.GetValue("DisplayVersion") is string version && version.Length > 0)
                    return version;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // An unreadable hive is not evidence either way; try the next path.
            }
        }

        return null;
    }

    /// <summary>Board, RAM, drives, fans and PSU losses that no CPU/GPU sensor accounts for.</summary>
    private const double IdleOverheadWatts = 35;

    private static void CollectSensors(IHardware hardware, List<SensorDto> into)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is not { } value) continue;
            if (sensor.SensorType is not (SensorType.Temperature or SensorType.Power or SensorType.Load or SensorType.Fan or SensorType.Clock))
                continue;

            into.Add(new SensorDto(
                hardware.Name,
                sensor.Name,
                sensor.SensorType.ToString(),
                Math.Round(value, 1),
                UnitFor(sensor.SensorType)));
        }

        foreach (var sub in hardware.SubHardware) CollectSensors(sub, into);
    }

    private static string UnitFor(SensorType type) => type switch
    {
        SensorType.Temperature => "C",
        SensorType.Power => "W",
        SensorType.Load => "%",
        SensorType.Fan => "RPM",
        SensorType.Clock => "MHz",
        _ => "",
    };

    /// <summary>
    /// LibreHardwareMonitor names per-thread load sensors "CPU Core #1 Thread #1", so counting
    /// sensors would return the thread count on an SMT part. Count distinct core numbers instead.
    /// </summary>
    private static int CountPhysicalCores(IHardware hardware)
    {
        var cores = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Load) continue;
            if (!sensor.Name.StartsWith("CPU Core #", StringComparison.Ordinal)) continue;

            var label = sensor.Name["CPU Core #".Length..];
            var threadAt = label.IndexOf(" Thread #", StringComparison.Ordinal);
            cores.Add(threadAt >= 0 ? label[..threadAt] : label);
        }
        return cores.Count;
    }

    private static double? Find(IHardware hardware, SensorType type, string name) =>
        hardware.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>Prefers a package/hotspot reading, otherwise the hottest sensor on the device.</summary>
    private static double? PickTemperature(IHardware hardware)
    {
        var temps = hardware.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue).ToList();
        if (temps.Count == 0) return null;

        var preferred = temps.FirstOrDefault(s =>
            s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
            s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ||
            s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase));

        return Math.Round((preferred ?? temps.MaxBy(s => s.Value!.Value)!).Value!.Value, 1);
    }

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return Environment.UserName == "root";
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_opened) _computer.Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        _gate.Dispose();
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}
