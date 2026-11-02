using System.Reflection;
using MiningFleet.Contracts;
using SelfUpdateKit;

namespace MiningFleet.Agent;

/// <summary>
/// Agent wiring for the shared SelfUpdateKit: official repositories, agent zip names,
/// probe executables, and the node-identity files that must survive. Update rules
/// (mandatory checksum, staged probe with rollback, immediate swap) come from the
/// library, not from here.
/// </summary>
public static class FleetAgentUpdate
{
    /// <summary>
    /// Options for one variant. The default overload prefers <see cref="FleetVariant.Light"/>
    /// whenever this PC has the compatible .NET + ASP.NET Core runtime installed, falling back
    /// to <see cref="FleetVariant.Full"/> otherwise. Identity files stay excluded in every variant.
    /// </summary>
    public static ReleaseSourceOptions Options(FleetVariant variant)
    {
        var options = new ReleaseSourceOptions
        {
            Repository = ReleaseAssets.GitHubRepositories[0],
            ExpectedMagic = [(byte)'P', (byte)'K'],
            ApplyPayloadImmediately = true,
            UserAgent = AgentIdentity.Name,
        };
        options.FallbackRepositories.AddRange(ReleaseAssets.GitHubRepositories.Skip(1));
        options.ExecutableAssetNames.AddRange(ReleaseAssets.AgentZipNamesFor(variant));
        options.ProbeExecutableNames.AddRange(AgentIdentity.ExeFileNames);
        options.ExcludedFileNames.AddRange(AgentUpdateService.ProtectedFileNames);
        return options;
    }

    public static ReleaseSourceOptions Options() =>
        Options(PreferredVariant());

    public static FleetVariant PreferredVariant() =>
        ReleaseAssets.PreferredVariant(agent: true);

    /// <summary>
    /// Which package this installation came from. The installer and updater record it in
    /// <see cref="ReleaseAssets.VariantMarkerFileName"/> next to the executable.
    /// </summary>
    public static FleetVariant InstalledVariant() =>
        ReleaseAssets.InstalledVariant(Environment.ProcessPath);

    public static ReleaseVersion InstalledVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null
            ? new ReleaseVersion(0, 0, 0)
            : new ReleaseVersion(version.Major, version.Minor, version.Build);
    }

    public static string InstallDirectory => AppContext.BaseDirectory;

    public static string InstalledExePath() =>
        UpdateFiles.FirstExisting(InstallDirectory, AgentIdentity.ExeFileNames, AgentIdentity.ExeFileName);

    /// <summary>
    /// A release tag for the kit, or null for the newest release. "latest" is the
    /// console's spelling, not a tag; passing it through would fail tag validation.
    /// </summary>
    public static string? NormalizeTag(string? version) =>
        string.IsNullOrWhiteSpace(version) || version.Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? null
            : version;

    public static Task<bool> ProbeExecutableAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(UpdateFiles.IsPortableExecutable(path));

    /// <summary>
    /// Executes <c>--probe</c> on builds that support it (&gt;= 1.17.40) so a Light binary is
    /// proven to load on this machine's shared .NET + ASP.NET Core runtime before swapping.
    /// </summary>
    public static Task<bool> ProbeReleaseExecutableAsync(
        string path,
        ReleaseVersion releaseVersion,
        CancellationToken cancellationToken)
    {
        if (releaseVersion < new ReleaseVersion(1, 17, 40) && releaseVersion != new ReleaseVersion(0, 0, 0))
            return ProbeExecutableAsync(path, cancellationToken);

        return UpdateFiles.RunExecutableProbeAsync(path, "--probe", TimeSpan.FromSeconds(10), cancellationToken);
    }
}
