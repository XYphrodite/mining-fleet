using System.Runtime.InteropServices;

namespace MiningFleet.Contracts;

/// <summary>
/// Names this product ships under. The product is mining-fleet; live agents, the Windows
/// service and GitHub still answer to xmrig-fleet. Lookups try the new name first and fall
/// back to the old one so one release can upgrade both an already-renamed console and a
/// node that has not moved yet.
///
/// Matched in full rather than by fragment: each zip's name is a prefix of the other
/// side's, and a substring match would unpack the agent over the console (or the reverse).
/// </summary>
public static class ReleaseAssets
{
    public const string Product = "mining-fleet";
    public const string LegacyProduct = "xmrig-fleet";

    public static IReadOnlyList<string> ConsoleZipNames => Names(agent: false);

    public static IReadOnlyList<string> AgentZipNames => Names(agent: true);

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

    private static IReadOnlyList<string> Names(bool agent)
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            var other => other.ToString().ToLowerInvariant(),
        };
        var role = agent ? "-agent" : "";
        return [$"{Product}{role}-{os}-{arch}.zip", $"{LegacyProduct}{role}-{os}-{arch}.zip"];
    }
}
