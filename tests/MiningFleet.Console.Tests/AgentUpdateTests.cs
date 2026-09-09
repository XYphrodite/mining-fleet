using MiningFleet.Agent;

namespace MiningFleet.Console.Tests;

/// <summary>
/// The agent now updates itself on a command from the console, which puts two things one
/// mistake away from bricking a node that nobody can reach without a physical visit:
/// picking the wrong asset out of a release, and overwriting the files that hold the node's
/// identity. Both have precedent - `update` once unpacked the agent over the console, and a
/// re-run of install-agent.ps1 replaced a node's fleet token and locked the console out.
/// </summary>
public sealed class AgentUpdateTests
{
    [Fact]
    public void The_wanted_asset_names_the_agent_not_the_console()
    {
        Assert.StartsWith("mining-fleet-agent-", AgentUpdateService.AssetName);
        Assert.EndsWith(".zip", AgentUpdateService.AssetName);
    }

    [Fact]
    public void A_release_with_both_names_picks_the_new_agent_zip()
    {
        string[] assets =
        [
            "mining-fleet-win-x64.zip",
            "xmrig-fleet-win-x64.zip",
            "xmrig-fleet-agent-win-x64.zip",
            "mining-fleet-agent-win-x64.zip",
        ];

        Assert.Equal("mining-fleet-agent-win-x64.zip", AgentUpdateService.PickAsset(assets));
    }

    [Fact]
    public void A_legacy_only_release_still_updates_the_agent()
    {
        string[] assets = ["xmrig-fleet-win-x64.zip", "xmrig-fleet-agent-win-x64.zip"];

        Assert.Equal("xmrig-fleet-agent-win-x64.zip", AgentUpdateService.PickAsset(assets));
    }

    [Fact]
    public void The_console_asset_is_not_an_acceptable_match()
    {
        // The agent name contains the console name, so a fragment match in either direction is a trap.
        string[] assets = ["xmrig-fleet-win-x64.zip", "mining-fleet-win-x64.zip"];

        Assert.Null(AgentUpdateService.PickAsset(assets));
    }

    [Fact]
    public void The_two_sides_never_want_the_same_asset()
    {
        Assert.NotEqual(UpdateService.AssetName, AgentUpdateService.AssetName);
        Assert.Empty(UpdateService.AssetNames.Intersect(AgentUpdateService.AssetNames, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("appsettings.json")]           // the fleet token
    [InlineData("xmrig-api.token")]            // the miner's API token; losing it blanks hashrate
    [InlineData("miner.json")]                 // the pushed pool and wallet
    public void Node_identity_files_are_never_overwritten(string fileName)
    {
        Assert.Contains(fileName, AgentUpdateService.ProtectedFileNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_payload_is_accepted_under_either_agent_exe_name()
    {
        using var dir = new TempDirectory();
        Assert.Null(AgentIdentity.FindPayloadExe(dir.Path));

        File.WriteAllText(Path.Combine(dir.Path, "xmrig-fleet-agent.exe"), "legacy");
        Assert.Equal("xmrig-fleet-agent.exe", AgentIdentity.FindPayloadExe(dir.Path));

        File.WriteAllText(Path.Combine(dir.Path, "mining-fleet-agent.exe"), "new");
        Assert.Equal("mining-fleet-agent.exe", AgentIdentity.FindPayloadExe(dir.Path));
    }

    [Fact]
    public void The_restart_helper_starts_the_new_service_and_falls_back_to_the_old()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, AgentIdentity.ExeFileName), "agent");
        var helper = AgentUpdateService.WriteRestartHelper(dir.Path);
        var text = File.ReadAllText(helper);
        Assert.Contains("sc start %NEW%", text);
        Assert.Contains("sc delete %OLD%", text);
        Assert.Contains("sc start %OLD%", text);
        Assert.Contains(dir.Path, text);
        File.Delete(helper);
    }

    [Fact]
    public void A_release_tag_matches_the_four_part_assembly_version_it_produced()
    {
        // release.ps1 stamps v1.4.0 as 1.4.0.0, so a plain string comparison would re-download
        // the build the node is already running, on every single run.
        Assert.True(AgentUpdateService.IsSameVersion("1.4.0.0", "v1.4.0"));
        Assert.True(AgentUpdateService.IsSameVersion("1.4.0.0", "1.4.0"));
        Assert.False(AgentUpdateService.IsSameVersion("1.4.0.0", "v1.4.1"));
        Assert.False(AgentUpdateService.IsSameVersion("1.4.0.0", ""));
    }
}
