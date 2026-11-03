using MiningFleet.Contracts;
using SelfUpdateKit;

namespace MiningFleet.Console;

/// <summary>
/// Console wiring for the shared SelfUpdateKit: configured repository with official
/// fallbacks, console zip names, and probe executables. Update rules (mandatory
/// checksum, staged probe with rollback, immediate swap) come from the library,
/// not from here.
/// </summary>
public static class FleetConsoleUpdate
{
    public static IReadOnlyList<string> ExeFileNames { get; } =
        [ExeName(ReleaseAssets.Product), ExeName(ReleaseAssets.LegacyProduct)];

    /// <summary>
    /// Operator state beside the console executable that must never be overwritten by an update:
    /// fleet configuration/token and local income ledgers.
    /// </summary>
    public static IReadOnlyList<string> ProtectedFileNames { get; } =
        ["fleet.json", "gpu-income.json", LifetimeIncomeLedger.FileName];

    /// <summary>
    /// Options for one variant. The default overload prefers <see cref="FleetVariant.Light"/>
    /// whenever this PC has the compatible .NET runtime installed, falling back to
    /// <see cref="FleetVariant.Full"/> otherwise.
    /// </summary>
    public static ReleaseSourceOptions Options(UpdateConfig config, FleetVariant variant)
    {
        ArgumentNullException.ThrowIfNull(config);
        var tried = ReleaseAssets.RepositoriesToTry(config.Repository);
        var options = new ReleaseSourceOptions
        {
            Repository = tried[0],
            ExpectedMagic = [(byte)'P', (byte)'K'],
            ApplyPayloadImmediately = true,
            UserAgent = $"mining-fleet/{UpdateService.CurrentVersion}",
            AuthorizationToken = string.IsNullOrWhiteSpace(config.Token) ? null : config.Token,
        };
        options.FallbackRepositories.AddRange(tried.Skip(1));
        options.ExecutableAssetNames.AddRange(ReleaseAssets.ConsoleZipNamesFor(variant));
        options.ProbeExecutableNames.AddRange(ExeFileNames);
        options.ExcludedFileNames.AddRange(ProtectedFileNames);
        return options;
    }

    public static ReleaseSourceOptions Options(UpdateConfig config) =>
        Options(config, PreferredVariant());

    public static FleetVariant PreferredVariant() =>
        ReleaseAssets.PreferredVariant(agent: false);

    /// <summary>
    /// Which package this installation came from. The installer and updater record it in
    /// <see cref="ReleaseAssets.VariantMarkerFileName"/> next to the executable.
    /// </summary>
    public static FleetVariant InstalledVariant() =>
        ReleaseAssets.InstalledVariant(Environment.ProcessPath);

    public static ReleaseVersion InstalledVersion()
    {
        var version = UpdateService.CurrentVersion;
        return new ReleaseVersion(version.Major, version.Minor, version.Build);
    }

    public static string InstalledExePath() =>
        UpdateFiles.FirstExisting(UpdateService.InstallDirectory, ExeFileNames, ExeFileNames[0]);

    public static Task<bool> ProbeExecutableAsync(string path, CancellationToken cancellationToken) =>
        UpdateFiles.RunExecutableProbeAsync(path, "version", TimeSpan.FromSeconds(10), cancellationToken);

    private static string ExeName(string product) =>
        OperatingSystem.IsWindows() ? product + ".exe" : product;
}
