using System.Diagnostics;

namespace MiningFleet.Agent;

/// <summary>
/// Names the agent ships under. The process and the Windows service are mining-fleet-agent;
/// xmrig-fleet-agent remains a copy of the same executable and the name an older service
/// still answers to, so one update can move a live node without stopping the miner.
/// </summary>
public static class AgentIdentity
{
    public const string Name = "mining-fleet-agent";
    public const string LegacyName = "xmrig-fleet-agent";

    public static string ExeFileName => FileName(Name);
    public static string LegacyExeFileName => FileName(LegacyName);
    public static IReadOnlyList<string> ExeFileNames { get; } = [FileName(Name), FileName(LegacyName)];

    public const string ServiceName = Name;
    public const string LegacyServiceName = LegacyName;
    public static IReadOnlyList<string> ServiceNames { get; } = [ServiceName, LegacyServiceName];

    /// <summary>The exe a payload must contain. New name first, then the shim.</summary>
    public static string? FindPayloadExe(string directory)
    {
        foreach (var name in ExeFileNames)
        {
            if (File.Exists(Path.Combine(directory, name))) return name;
        }

        return null;
    }

    /// <summary>
    /// The SCM name this process was started under. Must match <c>UseWindowsService</c> or
    /// the host cannot connect to the service controller.
    /// </summary>
    public static string RunningServiceName()
    {
        if (!OperatingSystem.IsWindows()) return ServiceName;

        var pid = Environment.ProcessId;
        foreach (var name in ServiceNames)
        {
            if (ServicePid(name) == pid) return name;
        }

        return ServiceName;
    }

    private static string FileName(string name) =>
        OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static int? ServicePid(string service)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("sc.exe", $"queryex \"{service}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2000)) return null;

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("PID", StringComparison.OrdinalIgnoreCase)) continue;
                var colon = line.IndexOf(':');
                if (colon >= 0 && int.TryParse(line[(colon + 1)..].Trim(), out var pid) && pid > 0)
                    return pid;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }

        return null;
    }
}
