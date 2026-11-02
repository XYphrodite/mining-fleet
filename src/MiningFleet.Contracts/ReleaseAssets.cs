using System.Diagnostics;
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
    /// One <c>dotnet --list-runtimes</c> line that satisfies the base .NET runtime:
    /// <c>Microsoft.NETCore.App &lt;major&gt;.*</c>, or
    /// <c>Microsoft.WindowsDesktop.App &lt;major&gt;.*</c> (the desktop bundle
    /// includes the base runtime). Rejects 32-bit x86 runtimes when running in a 64-bit process.
    /// </summary>
    public static bool IsCompatibleRuntimeLine(string? line, string? major = null) =>
        MatchesFrameworkLine(line, major ?? DotNetMajor, "Microsoft.NETCore.App ", "Microsoft.WindowsDesktop.App ");

    /// <summary>
    /// One <c>dotnet --list-runtimes</c> line that satisfies the ASP.NET Core shared framework
    /// needed by the light agent build (<c>Microsoft.NET.Sdk.Web</c>).
    /// </summary>
    public static bool IsCompatibleAspNetRuntimeLine(string? line, string? major = null) =>
        MatchesFrameworkLine(line, major ?? DotNetMajor, "Microsoft.AspNetCore.App ");

    /// <summary>
    /// Whether the given <c>dotnet --list-runtimes</c> output satisfies the light build:
    /// the console needs <c>Microsoft.NETCore.App</c> (or <c>WindowsDesktop.App</c>), whereas
    /// the web-SDK agent additionally needs <c>Microsoft.AspNetCore.App</c>.
    /// </summary>
    public static bool CanRunLight(IEnumerable<string?> runtimeLines, bool agent, string? major = null)
    {
        major ??= DotNetMajor;
        var hasBase = false;
        var hasAspNet = !agent;

        foreach (var line in runtimeLines)
        {
            if (!hasBase && IsCompatibleRuntimeLine(line, major)) hasBase = true;
            if (!hasAspNet && IsCompatibleAspNetRuntimeLine(line, major)) hasAspNet = true;
            if (hasBase && hasAspNet) return true;
        }

        return false;
    }

    /// <summary>
    /// Selects the variant an update should install on this machine: whenever the PC can run
    /// the light build, light wins (even when upgrading an older full installation), unless
    /// explicitly forced via <c>MINING_FLEET_VARIANT=full</c>. Falls back to full when the
    /// required shared runtime is absent.
    /// </summary>
    public static FleetVariant PreferredVariant(
        bool agent,
        IEnumerable<string?>? runtimeLines = null,
        string? variantOverride = null)
    {
        var forced = (variantOverride
                      ?? Environment.GetEnvironmentVariable("MINING_FLEET_VARIANT")
                      ?? Environment.GetEnvironmentVariable("XMRIG_FLEET_VARIANT"))?.Trim();
        if (string.Equals(forced, "full", StringComparison.OrdinalIgnoreCase))
            return FleetVariant.Full;

        var lines = runtimeLines ?? DotNetRuntimeLines();
        return CanRunLight(lines, agent) ? FleetVariant.Light : FleetVariant.Full;
    }

    /// <summary>
    /// Reads installed .NET shared runtimes on this machine, checking the 64-bit system
    /// <c>Program Files\dotnet</c> host before <c>PATH</c> so Windows services under
    /// <c>LocalSystem</c> find the runtime even with a minimal service <c>PATH</c>.
    /// </summary>
    public static IReadOnlyList<string> DotNetRuntimeLines()
    {
        foreach (var candidate in DotNetHostCandidates())
        {
            if (TryListRuntimesFromProcess(candidate, out var lines) && lines.Count > 0)
                return lines;
        }

        return ScanSharedFrameworkDirectories();
    }

    private static bool MatchesFrameworkLine(string? line, string major, params string[] prefixes)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var text = line.Trim();

        // A 32-bit runtime in Program Files (x86) cannot run a 64-bit light binary.
        if (Environment.Is64BitProcess
            && text.Contains("Program Files (x86)", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var runtime in prefixes)
        {
            if (!text.StartsWith(runtime, StringComparison.Ordinal)) continue;
            var version = text[runtime.Length..].Trim();
            if (version.StartsWith(major + ".", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static IEnumerable<string> DotNetHostCandidates()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            var fromEnv = Path.Combine(envRoot.Trim(), exe);
            if (File.Exists(fromEnv)) yield return fromEnv;
        }

        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(pf))
            {
                var systemHost = Path.Combine(pf, "dotnet", "dotnet.exe");
                if (File.Exists(systemHost)) yield return systemHost;
            }
        }
        else
        {
            foreach (var root in (string[])["/usr/share/dotnet", "/usr/lib/dotnet", "/usr/local/share/dotnet"])
            {
                var host = Path.Combine(root, "dotnet");
                if (File.Exists(host)) yield return host;
            }
        }

        yield return "dotnet";
    }

    private static bool TryListRuntimesFromProcess(string fileName, out IReadOnlyList<string> lines)
    {
        lines = [];
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--list-runtimes");

            using var process = Process.Start(psi);
            if (process is null) return false;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(4000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return false;
            }

            if (process.ExitCode != 0) return false;
            lines = output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ScanSharedFrameworkDirectories()
    {
        var roots = new List<string>();
        var envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot)) roots.Add(envRoot.Trim());
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(pf)) roots.Add(Path.Combine(pf, "dotnet"));
        }
        else
        {
            roots.AddRange(["/usr/share/dotnet", "/usr/lib/dotnet", "/usr/local/share/dotnet"]);
        }

        var lines = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var shared = Path.Combine(root, "shared");
            if (!Directory.Exists(shared)) continue;
            foreach (var fw in (string[])["Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App", "Microsoft.AspNetCore.App"])
            {
                var fwDir = Path.Combine(shared, fw);
                if (!Directory.Exists(fwDir)) continue;
                try
                {
                    foreach (var verDir in Directory.EnumerateDirectories(fwDir))
                        lines.Add($"{fw} {Path.GetFileName(verDir)} [{fwDir}]");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return lines;
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
