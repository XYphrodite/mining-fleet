using System.Diagnostics;
using System.Globalization;

namespace MiningFleet.Contracts;

/// <summary>Shared file checks, execution probes and Full-to-Light cleanup for self-update.</summary>
public static class UpdateFiles
{
    private const string RetiredInfix = ".old-";

    /// <summary>
    /// Checks for a valid PE header (on Windows) or non-empty binary (on Unix).
    /// </summary>
    public static bool IsPortableExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (!OperatingSystem.IsWindows()) return stream.Length > 4;
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>First candidate present in the directory, or the preferred name.</summary>
    public static string FirstExisting(string directory, IEnumerable<string> candidates, string fallback)
    {
        foreach (var name in candidates)
        {
            if (!string.IsNullOrWhiteSpace(name) && File.Exists(Path.Combine(directory, name)))
                return Path.Combine(directory, name);
        }

        return Path.Combine(directory, fallback);
    }

    /// <summary>
    /// Runs the executable with a fast, side-effect-free flag (<c>version</c> on the console,
    /// <c>--probe</c> on the agent) to prove the machine's .NET runtime can actually launch it.
    /// </summary>
    public static async Task<bool> RunExecutableProbeAsync(
        string path,
        string argument,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (!IsPortableExecutable(path)) return false;

        try
        {
            var psi = new ProcessStartInfo(path)
            {
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process is null) return false;

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    public static void WriteVariantMarker(string installDirectory, FleetVariant variant)
    {
        try
        {
            var marker = Path.Combine(installDirectory, ReleaseAssets.VariantMarkerFileName);
            File.WriteAllText(marker, variant == FleetVariant.Light ? "light" : "full");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Deletes both <c>*.old</c> and timestamped <c>*.old-*</c> files left behind by earlier
    /// updates or a Full-to-Light transition once the outgoing process has exited.
    /// </summary>
    public static int CleanUpRetiredFiles(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            return 0;

        var removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (!IsRetiredFileName(name)) continue;

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still mapped by an exiting process; the next startup collects it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return removed;
    }

    public static bool IsRetiredFileName(string fileName) =>
        fileName.EndsWith(".old", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains(RetiredInfix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Retires self-contained runtime artifacts (<c>hostfxr.dll</c>, <c>coreclr.dll</c>, etc.)
    /// in <paramref name="installDirectory"/> that do not exist in a light payload, returning
    /// the rename pairs so they can be restored if the post-install probe fails.
    /// Never touches config, token, log or ledger files.
    /// </summary>
    public static IReadOnlyList<(string RetiredPath, string OriginalPath)> RetireObsoleteRuntimeFiles(
        string installDirectory,
        IReadOnlySet<string> payloadRelativeFiles,
        IEnumerable<string>? protectedFileNames = null)
    {
        var fullInstallDir = Path.GetFullPath(installDirectory);
        var protectedSet = new HashSet<string>(protectedFileNames ?? [], StringComparer.OrdinalIgnoreCase)
        {
            "appsettings.json",
            "appsettings.Production.json",
            "xmrig-api.token",
            "miner.json",
            "lifetime.json",
            "game-mining.json",
            "fleet.json",
            "gpu-income.json",
            "lifetime-income.json",
            ReleaseAssets.VariantMarkerFileName,
        };

        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var retired = new List<(string RetiredPath, string OriginalPath)>();

        foreach (var file in Directory.EnumerateFiles(fullInstallDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(fullInstallDir, file);
            // Skip any staging folders created by SelfUpdateService inside installDirectory.
            if (rel.StartsWith(".", StringComparison.Ordinal) && rel.Contains(Path.DirectorySeparatorChar))
                continue;

            var name = Path.GetFileName(file);
            if (IsRetiredFileName(name) || protectedSet.Contains(name))
                continue;
            if (payloadRelativeFiles.Contains(rel))
                continue;
            if (!IsRetirableRuntimeArtifact(name))
                continue;

            var dst = file + RetiredInfix + stamp;
            try
            {
                File.Move(file, dst);
                retired.Add((dst, file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return retired;
    }

    public static void RestoreRetiredFiles(IReadOnlyList<(string RetiredPath, string OriginalPath)> retired)
    {
        for (var i = retired.Count - 1; i >= 0; i--)
        {
            var (retiredPath, originalPath) = retired[i];
            try
            {
                if (!File.Exists(originalPath) && File.Exists(retiredPath))
                    File.Move(retiredPath, originalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsRetirableRuntimeArtifact(string fileName) =>
        fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".mib", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".nib", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".staticwebassets.endpoints.json", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, "createdump.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, "createdump", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Coordinates the staged and post-swap probes during <c>SelfUpdateService.UpdateAsync</c>:
/// captures the payload's file list while staged, retires leftover self-contained runtime DLLs
/// (<c>hostfxr.dll</c>, <c>coreclr.dll</c>, etc.) before probing the installed Light binary,
/// records <c>.mining-fleet-variant</c> on success, and restores everything on failure.
/// </summary>
public sealed class VariantSwapSession
{
    private readonly string _installDirectory;
    private readonly FleetVariant _targetVariant;
    private readonly IReadOnlyList<string> _protectedFiles;
    private readonly Func<string, CancellationToken, Task<bool>> _probe;
    private HashSet<string>? _payloadFiles;

    public VariantSwapSession(
        string installDirectory,
        FleetVariant targetVariant,
        Func<string, CancellationToken, Task<bool>> probe,
        IEnumerable<string>? protectedFiles = null)
    {
        _installDirectory = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _targetVariant = targetVariant;
        _probe = probe;
        _protectedFiles = (protectedFiles ?? []).ToArray();
    }

    public async Task<bool> ProbeAsync(string executablePath, CancellationToken ct)
    {
        var fullExe = Path.GetFullPath(executablePath);
        var exeDir = (Path.GetDirectoryName(fullExe) ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // Phase 1: staged payload directory before anything in installDirectory is touched.
        if (!string.Equals(exeDir, _installDirectory, comparison))
        {
            if (Directory.Exists(exeDir))
            {
                _payloadFiles = Directory
                    .EnumerateFiles(exeDir, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(exeDir, f))
                    .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            }

            var stagedHealthy = await _probe(fullExe, ct).ConfigureAwait(false);

            // Guard against older binaries whose probe command writes a default config file into
            // the staging directory: strip any protected file that was not part of the archive
            // before SelfUpdateService swaps staging into installDirectory.
            if (Directory.Exists(exeDir) && _protectedFiles.Count > 0 && _payloadFiles is not null)
            {
                var protectedSet = new HashSet<string>(_protectedFiles, StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(exeDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(exeDir, file);
                    if (_payloadFiles.Contains(rel)) continue;
                    if (!protectedSet.Contains(Path.GetFileName(file))) continue;
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }

            return stagedHealthy;
        }

        // Phase 2: installed binary in installDirectory right after PayloadSwap.Swap.
        IReadOnlyList<(string RetiredPath, string OriginalPath)> retiredExtras = [];
        if (_targetVariant == FleetVariant.Light && _payloadFiles is { Count: > 0 })
        {
            retiredExtras = UpdateFiles.RetireObsoleteRuntimeFiles(_installDirectory, _payloadFiles, _protectedFiles);
        }

        bool healthy;
        try
        {
            healthy = await _probe(fullExe, ct).ConfigureAwait(false);
        }
        catch
        {
            if (retiredExtras.Count > 0) UpdateFiles.RestoreRetiredFiles(retiredExtras);
            throw;
        }

        if (!healthy)
        {
            if (retiredExtras.Count > 0) UpdateFiles.RestoreRetiredFiles(retiredExtras);
            return false;
        }

        UpdateFiles.WriteVariantMarker(_installDirectory, _targetVariant);
        return true;
    }
}
