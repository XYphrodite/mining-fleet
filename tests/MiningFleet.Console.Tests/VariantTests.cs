using MiningFleet.Agent;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Tests;

/// <summary>
/// The light (framework-dependent) twin of every payload: the installer picks it when
/// the .NET runtime is present, the marker next to the binary keeps it across updates,
/// and the node's identity files survive in every variant.
/// </summary>
public sealed class VariantTests
{
    [Theory]
    [InlineData("Microsoft.NETCore.App 10.0.1 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]", true)]
    [InlineData("Microsoft.WindowsDesktop.App 10.0.5 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]", true)]
    [InlineData("Microsoft.NETCore.App 9.0.1 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]", false)]
    [InlineData("Microsoft.WindowsDesktop.App 9.0.1 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]", false)]
    [InlineData("Microsoft.NETCore.App 11.0.0 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]", false)]
    [InlineData("Microsoft.AspNetCore.App 10.0.1 [C:\\Program Files\\dotnet\\shared\\Microsoft.AspNetCore.App]", false)]
    [InlineData("Microsoft.NETCore.App", false)]
    [InlineData("", false)]
    public void A_runtime_line_qualifies_only_the_target_major(string? line, bool expected)
    {
        Assert.Equal(expected, ReleaseAssets.IsCompatibleRuntimeLine(line));
    }

    [Fact]
    public void Light_names_mirror_the_full_names_with_a_suffix()
    {
        var full = ReleaseAssets.ConsoleZipNamesFor(FleetVariant.Full);
        var light = ReleaseAssets.ConsoleZipNamesFor(FleetVariant.Light);

        Assert.Equal(full.Count, light.Count);
        foreach (var (f, l) in full.Zip(light))
            Assert.Equal(f.Replace(".zip", "-light.zip"), l);
    }

    [Fact]
    public void The_light_agent_asset_is_not_an_acceptable_full_match()
    {
        var light = ReleaseAssets.AgentZipNamesFor(FleetVariant.Light);

        Assert.All(light, name => Assert.EndsWith("-light.zip", name));
        Assert.Empty(ReleaseAssets.AgentZipNames.Intersect(light, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(light[0], ReleaseAssets.Pick(light, light));
    }

    [Theory]
    [InlineData("light", FleetVariant.Light)]
    [InlineData("Light", FleetVariant.Light)]
    [InlineData("full", FleetVariant.Full)]
    [InlineData("anything-else", FleetVariant.Full)]
    public void The_marker_decides_the_variant_and_defaults_to_full(string marker, FleetVariant expected)
    {
        using var dir = new TempDirectory();
        var exe = Path.Combine(dir.Path, "mining-fleet");
        File.WriteAllText(Path.Combine(dir.Path, ReleaseAssets.VariantMarkerFileName), marker);

        Assert.Equal(expected, ReleaseAssets.InstalledVariant(exe));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_marker_means_a_pre_variant_full_install(string? processPath)
    {
        using var dir = new TempDirectory();
        var exe = processPath is null ? null : Path.Combine(dir.Path, "mining-fleet");

        Assert.Equal(FleetVariant.Full, ReleaseAssets.InstalledVariant(exe));
    }

    [Fact]
    public void Each_side_updates_within_its_installed_variant()
    {
        var config = new UpdateConfig();

        Assert.Equal(
            ReleaseAssets.ConsoleZipNamesFor(FleetVariant.Light),
            FleetConsoleUpdate.Options(config, FleetVariant.Light).ExecutableAssetNames);
        Assert.Equal(
            ReleaseAssets.AgentZipNamesFor(FleetVariant.Light),
            FleetAgentUpdate.Options(FleetVariant.Light).ExecutableAssetNames);
        Assert.Equal(
            ReleaseAssets.AgentZipNames,
            FleetAgentUpdate.Options(FleetVariant.Full).ExecutableAssetNames);
    }

    [Fact]
    public void The_light_agent_update_still_protects_node_identity()
    {
        var options = FleetAgentUpdate.Options(FleetVariant.Light);

        foreach (var name in AgentUpdateService.ProtectedFileNames)
            Assert.Contains(name, options.ExcludedFileNames, StringComparer.OrdinalIgnoreCase);
    }
}
