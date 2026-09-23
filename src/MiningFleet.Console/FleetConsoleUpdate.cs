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

    public static ReleaseSourceOptions Options(UpdateConfig config)
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
        options.ExecutableAssetNames.AddRange(ReleaseAssets.ConsoleZipNames);
        options.ProbeExecutableNames.AddRange(ExeFileNames);
        return options;
    }

    public static ReleaseVersion InstalledVersion()
    {
        var version = UpdateService.CurrentVersion;
        return new ReleaseVersion(version.Major, version.Minor, version.Build);
    }

    public static string InstalledExePath() =>
        UpdateFiles.FirstExisting(UpdateService.InstallDirectory, ExeFileNames, ExeFileNames[0]);

    public static Task<bool> ProbeExecutableAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(UpdateFiles.IsPortableExecutable(path));

    private static string ExeName(string product) =>
        OperatingSystem.IsWindows() ? product + ".exe" : product;
}
