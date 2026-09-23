using System.Runtime.InteropServices;

namespace MiningFleet.Contracts;

/// <summary>
/// Names this product ships under. The product and GitHub repo are mining-fleet; live
/// agents and the Windows service still answer to xmrig-fleet. Lookups try the new name
/// first and fall back to the old one so one release can upgrade both an already-renamed
/// console and a node that has not moved yet.
///
/// Matched in full rather than by fragment: each zip's name is a prefix of the other
/// side's, and a substring match would unpack the agent over the console (or the reverse).
/// </summary>
/// <summary>
/// Which kind of build is installed. Full is self-contained; light is
/// framework-dependent and needs the .NET runtime on the machine.
/// </summary>
public enum FleetVariant
{
    Full,
    Light,
}

public static class ReleaseAssets
{
    public const string Product = "mining-fleet";
    public const string LegacyProduct = "xmrig-fleet";
    public const string GitHubOwner = "XYphrodite";

    /// <summary>
    /// Marker file next to the binary recording the installed variant, so a
    /// self-update keeps installing the same kind of build. Written by the
    /// install scripts; an installation from before variants has no marker
    /// and defaults to <see cref="FleetVariant.Full"/>.
    /// </summary>
    public const string VariantMarkerFileName = ".mining-fleet-variant";

    /// <summary>
    /// Major of the .NET runtime the light package targets. Bump together with
    /// the apps' target framework. These are console apps, so a machine with
    /// either Microsoft.NETCore.App or Microsoft.WindowsDesktop.App qualifies.
    /// </summary>
    public const string DotNetMajor = "10";

    /// <summary>Official GitHub repos, new then the name GitHub still redirects.</summary>
    public static IReadOnlyList<string> GitHubRepositories { get; } =
        [$"{GitHubOwner}/{Product}", $"{GitHubOwner}/{LegacyProduct}"];

    /// <summary>
    /// Repos to ask for a release. A custom fork is left alone. The two official names
    /// fall back to each other so a fleet.json written before the rename still updates.
    /// </summary>
    public static IReadOnlyList<string> RepositoriesToTry(string? configured)
    {
        var wanted = string.IsNullOrWhiteSpace(configured) ? null : configured.Trim().Trim('/');
        if (wanted is null) return GitHubRepositories;

        var official = false;
        foreach (var repo in GitHubRepositories)
        {
            if (repo.Equals(wanted, StringComparison.OrdinalIgnoreCase)) { official = true; break; }
        }

        if (!official) return [wanted];

        var result = new List<string> { wanted };
        foreach (var repo in GitHubRepositories)
        {
            if (!repo.Equals(wanted, StringComparison.OrdinalIgnoreCase)) result.Add(repo);
        }

        return result;
    }

    public static IReadOnlyList<string> ConsoleZipNames => Names(agent: false);

    public static IReadOnlyList<string> AgentZipNames => Names(agent: true);

    /// <summary>Console zip names for one variant, new then legacy.</summary>
    public static IReadOnlyList<string> ConsoleZipNamesFor(FleetVariant variant) => Names(agent: false, variant);

    /// <summary>Agent zip names for one variant, new then legacy.</summary>
    public static IReadOnlyList<string> AgentZipNamesFor(FleetVariant variant) => Names(agent: true, variant);

    /// <summary>
    /// One <c>dotnet --list-runtimes</c> line that satisfies the light package:
    /// <c>Microsoft.NETCore.App &lt;major&gt;.*</c>, or
    /// <c>Microsoft.WindowsDesktop.App &lt;major&gt;.*</c> (the desktop bundle
    /// includes the base runtime). The major must equal the app target-framework
    /// major. Same rule as the install scripts' Test-DotNetRuntimeLine.
    /// </summary>
    public static bool IsCompatibleRuntimeLine(string? line, string? major = null)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        major ??= DotNetMajor;
        var text = line.Trim();
        foreach (var runtime in (string[])[ "Microsoft.NETCore.App ", "Microsoft.WindowsDesktop.App "])
        {
            if (!text.StartsWith(runtime, StringComparison.Ordinal)) continue;
            var version = text[runtime.Length..].Trim();
            if (version.StartsWith(major + ".", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// Which package the installation came from. The installer records it next to the
    /// executable; an installation from before variants defaults to the full package, so
    /// an update never silently changes what kind of build is installed.
    /// </summary>
    public static FleetVariant InstalledVariant(string? processPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(processPath) && Path.IsPathFullyQualified(processPath))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(processPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    var marker = Path.Combine(directory, VariantMarkerFileName);
                    if (File.Exists(marker) &&
                        string.Equals(File.ReadAllText(marker).Trim(), "light", StringComparison.OrdinalIgnoreCase))
                        return FleetVariant.Light;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return FleetVariant.Full;
    }

    public static string? Pick(IReadOnlyList<string> wanted, IEnumerable<string?> available)
    {
        foreach (var name in wanted)
        {
            foreach (var candidate in available)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> Names(bool agent, FleetVariant variant = FleetVariant.Full)
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            var other => other.ToString().ToLowerInvariant(),
        };
        var role = agent ? "-agent" : "";
        var light = variant == FleetVariant.Light ? "-light" : "";
        return [$"{Product}{role}-{os}-{arch}{light}.zip", $"{LegacyProduct}{role}-{os}-{arch}{light}.zip"];
    }
}
