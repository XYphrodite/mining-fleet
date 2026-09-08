using XmrigFleet.Agent;
using XmrigFleet.Console;
using XmrigFleet.Contracts;

namespace XmrigFleet.Console.Tests;

/// <summary>
/// Guards stopping CPU mining outright while somebody is using the machine.
///
/// The failure this protects against is a node that goes quiet and stays quiet. Three separate
/// things can each leave a rig idle for a night with nothing anywhere to explain it: a flag written
/// after the kill instead of before it, an autostart that ignores the flag, and a resume that fires
/// while another rule still wants the miner down.
/// </summary>
public sealed class MinerPauseTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_node_stores_the_rule_and_answers_with_it()
    {
        using var dir = new TempDirectory();
        var store = new MinerConfigStore(dir.Path);

        store.Update(new MinerConfigDto
        {
            PauseWhile = new GpuPauseRuleDto { ProcessNames = ["cs2"], QuietSeconds = 300 },
        });

        // MinerConfigStore.Update enumerates every field by hand, so one forgotten there is
        // accepted over HTTP, echoed back as saved, and dropped on the next write.
        var afterRestart = new MinerConfigStore(dir.Path).Current;
        Assert.Equal(["cs2"], afterRestart.PauseWhile!.Processes);
        Assert.Equal(300, afterRestart.PauseWhile.QuietSeconds);
    }

    [Fact]
    public void A_miner_stopped_for_a_game_is_not_restarted_by_autostart()
    {
        var paused = new MinerConfigDto { AutoStartMiner = true, MinerStoppedByPause = true };

        // Autostart exists so a rig that rebooted returns to work — not so a machine somebody is
        // playing on starts mining under them the moment the agent restarts.
        Assert.False(MinerConfigStore.ShouldAutoStart(paused, installedDefault: true));

        Assert.True(MinerConfigStore.ShouldAutoStart(
            new MinerConfigDto { AutoStartMiner = true, MinerStoppedByPause = false },
            installedDefault: false));
    }

    [Fact]
    public void The_throttle_and_the_pause_are_tracked_separately()
    {
        using var dir = new TempDirectory();
        var store = new MinerConfigStore(dir.Path);

        store.Update(new MinerConfigDto { MinerStoppedByPause = true });
        var saved = store.Update(new MinerConfigDto { MinerStoppedByThrottle = true });

        // Two reasons to be stopped, and clearing one must not clear the other: a node whose game
        // closed while the throttle still wants it down has to stay down.
        Assert.True(saved.MinerStoppedByPause);
        Assert.True(saved.MinerStoppedByThrottle);

        Assert.True(store.Update(new MinerConfigDto { MinerStoppedByPause = false }).MinerStoppedByThrottle);
    }

    [Fact]
    public void Stopping_is_immediate_and_coming_back_waits()
    {
        var rule = new GpuPauseRule();

        // The person is already there by the time this is noticed, so there is nothing to gain by
        // hesitating - and a restart costs the RandomX dataset, so there is much to lose by
        // flapping back the moment the game exits.
        Assert.True(rule.Update(true, "cs2 is running", 300, Start));
        Assert.True(rule.Paused);

        // The wait runs from the first quiet sample, not from the moment the game closed: the
        // rule cannot know when that was, only when it first saw the machine go quiet.
        var quietFrom = Start.AddSeconds(45);
        Assert.False(rule.Update(false, "", 300, quietFrom));
        Assert.False(rule.Update(false, "", 300, quietFrom.AddSeconds(299)));
        Assert.True(rule.Paused);

        Assert.True(rule.Update(false, "", 300, quietFrom.AddSeconds(300)));
        Assert.False(rule.Paused);
    }

    [Fact]
    public void A_game_stops_the_miner_even_when_the_rule_also_watches_a_port()
    {
        var rule = new GpuPauseRuleDto { TcpPort = 11434, ProcessNames = ["cs2"] };

        // The defect that let a game freeze a node once already, guarded on the CPU side too
        // because both miners now share one evaluation.
        var busy = GpuPauseRule.Evaluate(rule, portConnections: 0, runningProcesses: ["cs2"]);

        Assert.True(busy.Busy);
        Assert.Contains("cs2 is running", busy.Description);
    }

    [Fact]
    public void A_node_names_its_own_rule_and_otherwise_follows_the_fleet()
    {
        var config = new FleetConfig
        {
            PauseWhile = new GpuPauseConfig { TcpPort = 11434, QuietSeconds = 300 },
        };

        var xeon = config.MinerPauseFor(new NodeConfig
        {
            Name = "desktop-ib88isg",
            PauseWhile = new GpuPauseConfig { ProcessNames = ["cs2"], QuietSeconds = 300 },
        });

        // Replaced whole, not merged: a node told to stand down for a game must not inherit a
        // port nobody asked it to watch.
        Assert.Equal(["cs2"], xeon!.Processes);
        Assert.Null(xeon.TcpPort);

        Assert.Equal(11434, config.MinerPauseFor(new NodeConfig { Name = "rig" })!.TcpPort);
    }

    [Fact]
    public void A_fleet_with_no_rule_sends_none()
    {
        // A rig nobody sits at should never stop mining, and an empty block must not become a
        // rule that stands a node down with nothing able to wake it.
        Assert.Null(new FleetConfig().MinerPauseFor(new NodeConfig { Name = "rig" }));
        Assert.Null(new FleetConfig { PauseWhile = new GpuPauseConfig { QuietSeconds = 300 } }
            .MinerPauseFor(new NodeConfig { Name = "rig" }));
    }

    [Fact]
    public void The_operator_is_told_every_condition_the_node_stored()
    {
        var rule = new GpuPauseRuleDto { TcpPort = 11434, ProcessNames = ["cs2", "dontstarve_steam_x64"] };

        var described = FleetService.DescribeConditions(rule);

        Assert.Contains("port 11434 is busy", described);
        Assert.Contains("cs2 runs", described);
        Assert.Contains("dontstarve_steam_x64 runs", described);
    }
}
