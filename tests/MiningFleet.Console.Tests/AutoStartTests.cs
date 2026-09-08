using MiningFleet.Agent;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Tests;

/// <summary>
/// Guards the setting that decides whether an outage costs an hour or costs until somebody
/// notices: a node that came back on its own either returns to mining or sits there earning
/// nothing, and nobody is signed in to tell the difference.
///
/// The three ways it can go wrong are all silent. A push that carries only autostart could take
/// the node's tuned ladder with it; the installed default could keep overriding what the operator
/// set; and autostart could overrule a stop the power limit made deliberately, putting a miner
/// back on a machine somebody is using.
/// </summary>
public class AutoStartTests
{
    [Fact]
    public void Turning_autostart_on_leaves_the_rest_of_the_node_config_alone()
    {
        using var directory = new TempDirectory();
        var store = new MinerConfigStore(directory.Path);

        var tuned = new List<ThrottleStepDto> { new(0, 100), new(35, 50) };
        store.Update(new MinerConfigDto
        {
            PoolUrl = "pool.hashvault.pro:443",
            Wallet = "4AAA",
            KeepMonitorOpen = true,
            Throttle = new ThrottleSettingsDto { Enabled = true, Steps = tuned, FloorLevel = 25 },
        });

        store.Update(new MinerConfigDto { AutoStartMiner = true });

        Assert.True(store.Current.AutoStartMiner);
        Assert.Equal("pool.hashvault.pro:443", store.Current.PoolUrl);
        Assert.Equal("4AAA", store.Current.Wallet);
        Assert.True(store.Current.KeepMonitorOpen);
        Assert.Equal(tuned, store.Current.Throttle!.Steps);
        Assert.Equal(25, store.Current.Throttle!.FloorLevel);
    }

    [Fact]
    public void A_push_that_says_nothing_about_autostart_does_not_clear_it()
    {
        using var directory = new TempDirectory();
        var store = new MinerConfigStore(directory.Path);

        store.Update(new MinerConfigDto { AutoStartMiner = true });
        store.Update(new MinerConfigDto { PoolUrl = "pool.hashvault.pro:443" });

        Assert.True(store.Current.AutoStartMiner);
    }

    [Fact]
    public void The_setting_survives_an_agent_restart()
    {
        using var directory = new TempDirectory();
        new MinerConfigStore(directory.Path).Update(new MinerConfigDto { AutoStartMiner = true });

        // Every self-update restarts the agent, so a setting that lived in memory would be
        // forgotten by the very reboots it exists to survive.
        Assert.True(new MinerConfigStore(directory.Path).Current.AutoStartMiner);
    }

    [Theory]
    // What the operator set wins over what install-agent.ps1 wrote, in both directions.
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    // A node nobody has told either way still behaves the way it was installed.
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    public void The_nodes_own_setting_beats_the_installed_default(bool? stored, bool installedDefault, bool expected)
    {
        var config = new MinerConfigDto { AutoStartMiner = stored };

        Assert.Equal(expected, MinerConfigStore.ShouldAutoStart(config, installedDefault));
    }

    [Fact]
    public void Autostart_does_not_restart_a_miner_the_power_limit_stopped()
    {
        // The throttle stops the miner outright at rung zero because a capped miner still holds
        // ~2.3 GB, and it records that it was the one who did it. Starting again on the next agent
        // restart would put a miner back on a machine somebody is sitting at.
        var stoppedByThrottle = new MinerConfigDto { AutoStartMiner = true, MinerStoppedByThrottle = true };

        Assert.False(MinerConfigStore.ShouldAutoStart(stoppedByThrottle, installedDefault: true));
        Assert.True(MinerConfigStore.ShouldAutoStart(stoppedByThrottle with { MinerStoppedByThrottle = false }, false));
    }

    [Theory]
    [InlineData(true, "starts mining at boot")]
    [InlineData(false, "stays idle at boot")]
    // Null is an answer in its own right: the console must not report an untold node as "off".
    [InlineData(null, "unset - follows the node's appsettings.json")]
    public void Unset_reads_differently_from_off(bool? autoStart, string expected)
    {
        Assert.Equal(expected, FleetService.DescribeAutoStart(autoStart));
    }
}
