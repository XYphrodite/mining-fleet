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
    /// Options for one variant. The default overload keeps the installed variant (see
    /// <see cref="InstalledVariant"/>), so a full install never drifts onto light zips.
    /// Identity files stay excluded in every variant.
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
        Options(InstalledVariant());

    /// <summary>
    /// Which package this installation came from. The installer records it in
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
}
