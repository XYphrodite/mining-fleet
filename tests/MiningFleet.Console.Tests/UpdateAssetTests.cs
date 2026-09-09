using MiningFleet.Console;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Tests;

/// <summary>
/// A release carries both the console and the agent for the same platform. Matching the
/// asset by a platform fragment picked whichever came first, so `update` once downloaded
/// the node agent and unpacked it over the console.
/// </summary>
public sealed class UpdateAssetTests
{
    [Fact]
    public void The_wanted_asset_names_the_console_not_the_agent()
    {
        Assert.StartsWith("mining-fleet-", UpdateService.AssetName);
        Assert.DoesNotContain("agent", UpdateService.AssetName);
        Assert.EndsWith(".zip", UpdateService.AssetName);
    }

    [Fact]
    public void A_release_with_both_names_picks_the_new_console_zip()
    {
        string[] assets =
        [
            "mining-fleet-agent-win-x64.zip",
            "xmrig-fleet-agent-win-x64.zip",
            "xmrig-fleet-win-x64.zip",
            "mining-fleet-win-x64.zip",
        ];

        Assert.Equal("mining-fleet-win-x64.zip", UpdateService.PickAsset(assets));
    }

    [Fact]
    public void A_legacy_only_release_still_updates_the_console()
    {
        string[] assets = ["xmrig-fleet-agent-win-x64.zip", "xmrig-fleet-win-x64.zip"];

        Assert.Equal("xmrig-fleet-win-x64.zip", UpdateService.PickAsset(assets));
    }

    [Fact]
    public void The_agent_asset_is_not_an_acceptable_match()
    {
        string[] assets = ["xmrig-fleet-agent-win-x64.zip", "mining-fleet-agent-win-x64.zip"];

        Assert.Null(UpdateService.PickAsset(assets));
    }

    [Fact]
    public void Backup_files_are_named_so_start_up_can_clear_them()
    {
        // CleanUpPreviousUpdate globs on this suffix; the two must not drift apart.
        Assert.Equal(".old", UpdateService.BackupSuffix);
    }

    [Fact]
    public void The_default_release_repo_is_the_renamed_github_name()
    {
        Assert.Equal("XYphrodite/mining-fleet", new UpdateConfig().Repository);
        Assert.Equal("XYphrodite/mining-fleet", ReleaseAssets.GitHubRepositories[0]);
        Assert.Contains("XYphrodite/xmrig-fleet", ReleaseAssets.GitHubRepositories);
    }

    [Fact]
    public void A_fleet_json_written_before_the_rename_still_tries_the_new_repo()
    {
        var tried = ReleaseAssets.RepositoriesToTry("XYphrodite/xmrig-fleet");
        Assert.Equal("XYphrodite/xmrig-fleet", tried[0]);
        Assert.Equal("XYphrodite/mining-fleet", tried[1]);
    }

    [Fact]
    public void A_custom_fork_is_not_second_guessed()
    {
        var tried = ReleaseAssets.RepositoriesToTry("someone/else");
        Assert.Single(tried);
        Assert.Equal("someone/else", tried[0]);
    }
}
