using System.Diagnostics;
using System.Security.Principal;

namespace MiningFleet.Agent;

/// <summary>
/// Registers miner directories and full executable paths in Windows Defender exclusions so
/// unpacking or starting a managed miner is not quarantined, without granting a global
/// bare-filename exclusion (such as <c>xmrig.exe</c> anywhere on disk) that rogue miners
/// dropped into Temp/AppData could piggyback on.
/// </summary>
public static class WindowsDefenderExclusions
{
    public static Task TryAddPathAsync(string directoryPath, ILogger? log, CancellationToken ct) =>
        RunPowerShellAsync(BuildAddPathScript(directoryPath), directoryPath, log, ct);

    public static Task TryAddProcessAsync(string executablePath, ILogger? log, CancellationToken ct) =>
        RunPowerShellAsync(BuildAddProcessScript(executablePath), executablePath, log, ct);

    public static Task TryRemoveUnderPathAsync(string targetPath, ILogger? log, CancellationToken ct) =>
        RunPowerShellAsync(BuildRemoveUnderPathScript(targetPath), targetPath, log, ct);

    public static string BuildAddPathScript(string directoryPath)
    {
        var fullPath = EscapeSingleQuoted(Path.GetFullPath(directoryPath));
        return $"Add-MpPreference -ExclusionPath '{fullPath}' -ErrorAction SilentlyContinue";
    }

    public static string BuildAddProcessScript(string executablePath)
    {
        var fullExe = Path.GetFullPath(executablePath);
        var safeFullExe = EscapeSingleQuoted(fullExe);
        var bareName = Path.GetFileName(fullExe);
        if (string.IsNullOrEmpty(bareName))
            return $"Add-MpPreference -ExclusionProcess '{safeFullExe}' -ErrorAction SilentlyContinue";

        var safeBare = EscapeSingleQuoted(bareName);
        return $"Remove-MpPreference -ExclusionProcess '{safeBare}' -ErrorAction SilentlyContinue; " +
               $"Add-MpPreference -ExclusionProcess '{safeFullExe}' -ErrorAction SilentlyContinue";
    }

    public static string BuildRemoveUnderPathScript(string targetPath)
    {
        var root = Path.GetFullPath(targetPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = root + Path.DirectorySeparatorChar;
        var safeRoot = EscapeSingleQuoted(root);
        var safePrefix = EscapeSingleQuoted(prefix);

        return $"$r = '{safeRoot}'; $pfx = '{safePrefix}'; " +
               "$mp = Get-MpPreference -ErrorAction SilentlyContinue; " +
               "if ($mp) { " +
               "foreach ($ep in @($mp.ExclusionPath)) { if ($ep -and ($ep.Equals($r, 'OrdinalIgnoreCase') -or $ep.StartsWith($pfx, 'OrdinalIgnoreCase'))) { Remove-MpPreference -ExclusionPath $ep -ErrorAction SilentlyContinue } }; " +
               "foreach ($pr in @($mp.ExclusionProcess)) { if ($pr -and ($pr.Equals($r, 'OrdinalIgnoreCase') -or $pr.StartsWith($pfx, 'OrdinalIgnoreCase'))) { Remove-MpPreference -ExclusionProcess $pr -ErrorAction SilentlyContinue } } " +
               "}";
    }

    private static string EscapeSingleQuoted(string value) => value.Replace("'", "''");

    private static async Task RunPowerShellAsync(string script, string contextPath, ILogger? log, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
                return;

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
            log?.LogDebug(ex, "Could not update Windows Defender exclusion for {Path}", contextPath);
        }
    }
}
