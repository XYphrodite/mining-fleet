using System.Diagnostics;
using System.Security.Principal;

namespace MiningFleet.Agent;

/// <summary>
/// Registers miner directories and executable names in Windows Defender exclusions before
/// unpacking an archive, preventing false-positive quarantine (such as Wacatac on SRBMiner
/// or xmrig) when the agent extracts or starts the binary.
/// </summary>
internal static class WindowsDefenderExclusions
{
    public static async Task TryAddAsync(string directoryPath, string executableName, ILogger log, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
                return;

            var fullPath = Path.GetFullPath(directoryPath).Replace("'", "''");
            var safeExe = executableName.Replace("'", "''");
            var script = $"Add-MpPreference -ExclusionPath '{fullPath}' -ErrorAction SilentlyContinue; " +
                         $"Add-MpPreference -ExclusionProcess '{safeExe}' -ErrorAction SilentlyContinue";

            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var proc = Process.Start(psi);
            if (proc is null)
                return;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not register Windows Defender exclusion for {Path}", directoryPath);
        }
    }
}
