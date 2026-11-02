using System.Reflection;
using MiningFleet.Contracts;
using SelfUpdateKit;

namespace MiningFleet.Console;

/// <summary>A release newer than what is running, with the asset that fits this machine.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string AssetName, string DownloadUrl, long SizeBytes, string? Notes);

/// <summary>
/// Self-update for the operator console: asks GitHub for the latest release, downloads the
/// asset for this platform, and swaps the files in place. Checksum verification, payload
/// validation and rollback come from the shared kit.
///
/// Replacing a running executable works because Windows allows renaming the image of a live
/// process even though it cannot be deleted: the old file is moved aside and cleaned up on
/// the next start.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Files displaced by a previous update, removed on the next run.</summary>
    public const string BackupSuffix = ".old";

    private readonly UpdateConfig _config;

    public UpdateService(UpdateConfig config)
    {
        _config = config;
    }

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public static string InstallDirectory => AppContext.BaseDirectory;

    /// <summary>Returns the newer release, or null when this build is already current.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.Repository))
            throw new InvalidOperationException("update.repository is not set in fleet.json (expected \"owner/name\").");

        var preferred = FleetConsoleUpdate.PreferredVariant();
        var chosenVariant = preferred;
        ReleaseDescriptor release;
        try
        {
            using var source = new GitHubReleaseSource(FleetConsoleUpdate.Options(_config, preferred));
            release = await source.ResolveAsync(null, ct);
        }
        catch (InvalidDataException) when (preferred == FleetVariant.Light)
        {
            chosenVariant = FleetVariant.Full;
            try
            {
                using var fallback = new GitHubReleaseSource(FleetConsoleUpdate.Options(_config, FleetVariant.Full));
                release = await fallback.ResolveAsync(null, ct);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        var installedVersion = FleetConsoleUpdate.InstalledVersion();
        var variantSwitchWanted = release.Version == installedVersion
                                  && FleetConsoleUpdate.InstalledVariant() != chosenVariant;
        if (release.Version < installedVersion || (release.Version == installedVersion && !variantSwitchWanted))
            return null;

        return new UpdateInfo(
            new Version(release.Version.Major, release.Version.Minor, release.Version.Patch),
            release.Tag,
            AssetFileName(release.ExecutableUrl),
            release.ExecutableUrl.AbsoluteUri,
            release.SizeBytes,
            release.Notes);
    }

    /// <summary>
    /// Downloads and installs an update. <paramref name="onProgress"/> reports bytes received
    /// and the total when the server declares one.
    /// </summary>
    public async Task<string> ApplyAsync(UpdateInfo update, Action<long, long?> onProgress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        var preferred = update.AssetName.EndsWith("-light.zip", StringComparison.OrdinalIgnoreCase)
            ? FleetVariant.Light
            : FleetConsoleUpdate.PreferredVariant();
        var chosenVariant = preferred;

        SelfUpdateReport report;
        try
        {
            report = await ApplyVariantAsync(chosenVariant, update.Tag, onProgress, ct);
        }
        catch (InvalidDataException) when (chosenVariant == FleetVariant.Light)
        {
            chosenVariant = FleetVariant.Full;
            report = await ApplyVariantAsync(FleetVariant.Full, update.Tag, onProgress, ct);
        }

        var variantLabel = chosenVariant == FleetVariant.Light ? "light" : "full";
        return $"Updated to {report.Tag} ({variantLabel}). Restart mining-fleet to run the new version.";
    }

    private async Task<SelfUpdateReport> ApplyVariantAsync(
        FleetVariant variant,
        string tag,
        Action<long, long?> onProgress,
        CancellationToken ct)
    {
        var options = FleetConsoleUpdate.Options(_config, variant);
        using var source = new GitHubReleaseSource(options);
        var session = new VariantSwapSession(
            InstallDirectory,
            variant,
            FleetConsoleUpdate.ProbeExecutableAsync);
        var service = new SelfUpdateService(
            FleetConsoleUpdate.InstalledExePath(),
            FleetConsoleUpdate.InstalledVersion(),
            source,
            options,
            probe: session.ProbeAsync);
        return await service.UpdateAsync(new SelfUpdateRequest(Tag: tag), ct, progress =>
        {
            if (progress.Phase == SelfUpdatePhase.Downloading)
                onProgress(progress.ReceivedBytes ?? 0, progress.TotalBytes);
        });
    }

    private static string AssetFileName(Uri address) =>
        Path.GetFileName(Uri.UnescapeDataString(address.LocalPath));

    /// <summary>Removes files displaced by an earlier update. Safe to call on every start.</summary>
    public static void CleanUpPreviousUpdate()
    {
        try
        {
            UpdateFiles.CleanUpRetiredFiles(InstallDirectory);
            var replacer = new ExecutableReplacer();
            foreach (var name in FleetConsoleUpdate.ExeFileNames)
                replacer.RemoveRetiredCopies(Path.Combine(InstallDirectory, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers are harmless; never let cleanup stop the console from starting.
        }
    }

    /// <summary>Preferred console zip, e.g. mining-fleet-win-x64.zip. See <see cref="AssetNames"/>.</summary>
    public static string AssetName => AssetNames[0];

    /// <summary>
    /// Console zip names, new then legacy. Matched in full rather than by fragment: a release
    /// also carries the agent zip, and a substring match would unpack it over the console.
    /// </summary>
    public static IReadOnlyList<string> AssetNames => MiningFleet.Contracts.ReleaseAssets.ConsoleZipNames;

    public static string? PickAsset(IEnumerable<string?> available) =>
        MiningFleet.Contracts.ReleaseAssets.Pick(AssetNames, available);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

}
