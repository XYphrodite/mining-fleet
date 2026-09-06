using Microsoft.Extensions.Logging.Abstractions;
using XmrigFleet.Agent;
using XmrigFleet.Console;
using XmrigFleet.Contracts;

namespace XmrigFleet.Console.Tests;

/// <summary>
/// Guards the parts of GPU mining that fail silently: a settings push that drops what only the
/// node knows, a pause rule that inherits a condition nobody asked for, a per-node algorithm that
/// fails to override the fleet default, and the two miners colliding on one API port.
///
/// The merge is the sharpest of these. <see cref="MinerConfigStore.Update"/> enumerates every
/// field by hand, so a field added to the contract and forgotten there is accepted over HTTP,
/// echoed back in the response as though it were saved, and dropped on the next write — and the
/// console's read-back check reports success.
/// </summary>
public class GpuMiningTests
{
    private static MinerConfigStore Store(TempDirectory dir) => new(dir.Path);

    [Fact]
    public void A_push_that_only_enables_gpu_mining_keeps_what_the_node_knows()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);

        store.Update(new MinerConfigDto
        {
            GpuMiner = new GpuMinerSettingsDto
            {
                Algorithm = "CR29",
                PoolUrl = "xtm-c29.kryptex.network:7040",
                ExecutablePath = @"C:\mining\lolminer\lolMiner.exe",
                RunInInteractiveSession = true,
            },
        });

        var saved = store.Update(new MinerConfigDto { GpuMiner = new GpuMinerSettingsDto { Enabled = true } });

        // The installer wrote the path and nothing else knows where lolMiner landed; the session
        // flag records which launch actually works on this machine. Neither is the operator's to
        // resend, so a push that names one field must not take them with it.
        Assert.True(saved.GpuMiner!.Enabled);
        Assert.Equal(@"C:\mining\lolminer\lolMiner.exe", saved.GpuMiner.ExecutablePath);
        Assert.True(saved.GpuMiner.RunInInteractiveSession);
        Assert.Equal("CR29", saved.GpuMiner.Algorithm);
    }

    [Fact]
    public void A_pause_rule_naming_a_port_drops_a_previously_named_process()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);

        store.Update(new MinerConfigDto
        {
            GpuMiner = new GpuMinerSettingsDto { PauseWhile = new GpuPauseRuleDto { ProcessName = "game", QuietSeconds = 60 } },
        });

        var saved = store.Update(new MinerConfigDto
        {
            GpuMiner = new GpuMinerSettingsDto { PauseWhile = new GpuPauseRuleDto { TcpPort = 11434 } },
        });

        // The one place "null means leave alone" is wrong. A push carries the node's whole rule,
        // so a condition absent from it is one the operator removed - and inheriting it would
        // leave the node standing down for a reason found in nobody's config.
        Assert.Equal(11434, saved.GpuMiner!.PauseWhile!.TcpPort);
        Assert.Null(saved.GpuMiner.PauseWhile.ProcessName);
        Assert.Equal(60, saved.GpuMiner.PauseWhile.QuietSeconds);
    }

    /// <summary>
    /// The defect this pair exists for, and it cost real money before it was found: the node was
    /// set to hand its card to a local model on port 11434, a game was started, and the miner went
    /// on holding 97.6% of the 3D engine and 6,879 MB of an 8,188 MB card. The game got 1.1% and
    /// 270 MB, and the machine froze.
    ///
    /// Nothing was misconfigured. The rule was read as "a port or a process", and the port won
    /// before the process name was so much as looked at.
    /// </summary>
    [Fact]
    public void A_process_stands_the_card_down_even_when_the_rule_also_watches_a_port()
    {
        var rule = new GpuPauseRuleDto { TcpPort = 11434, ProcessName = "dontstarve_steam_x64" };

        var busy = GpuPauseRule.Evaluate(rule, portConnections: 0, runningProcesses: ["dontstarve_steam_x64"]);

        Assert.True(busy.Busy);
        Assert.Contains("dontstarve_steam_x64 is running", busy.Description);
    }

    [Fact]
    public void A_quiet_rule_names_everything_it_was_watching()
    {
        var rule = new GpuPauseRuleDto { TcpPort = 11434, ProcessNames = ["dontstarve_steam_x64", "blender"] };

        var quiet = GpuPauseRule.Evaluate(rule, portConnections: 0, runningProcesses: []);

        // A node that is not pausing should say what it is not pausing for, the same way a missing
        // sensor says why it is missing. "Quiet" alone cannot be checked against the config.
        Assert.False(quiet.Busy);
        Assert.Contains("port 11434", quiet.Description);
        Assert.Contains("dontstarve_steam_x64", quiet.Description);
        Assert.Contains("blender", quiet.Description);
    }

    [Fact]
    public void Every_reason_the_card_is_wanted_is_reported_not_just_the_first()
    {
        var rule = new GpuPauseRuleDto { TcpPort = 11434, ProcessNames = ["blender"] };

        var busy = GpuPauseRule.Evaluate(rule, portConnections: 2, runningProcesses: ["blender"]);

        // Both, because the card comes back only when the last of them lets go: an operator told
        // only about the port would restart the miner into a render still running.
        Assert.True(busy.Busy);
        Assert.Contains("port 11434 busy, 2 connection(s)", busy.Description);
        Assert.Contains("blender is running", busy.Description);
    }

    [Fact]
    public void The_singular_process_field_is_folded_in_beside_the_list()
    {
        // Every node already deployed has the singular field written into its miner.json, and an
        // operator's fleet.json may have either. A field that quietly stops being read is how a
        // rig ends up mining through a game with nothing anywhere to say why.
        var rule = new GpuPauseRuleDto { ProcessName = "blender", ProcessNames = ["dontstarve_steam_x64", "BLENDER"] };

        // Two conditions, not three: the same process named in both fields is one process. Which
        // spelling survives is not asserted because it cannot matter - Windows matches process
        // names without regard to case, and so does the rule.
        Assert.Equal(2, rule.Processes.Count);
        Assert.Contains("dontstarve_steam_x64", rule.Processes);
        Assert.True(rule.NamesACondition);

        Assert.True(GpuPauseRule.Evaluate(rule, 0, ["Blender"]).Busy);
    }

    [Fact]
    public void A_rule_naming_several_games_is_pushed_whole()
    {
        var config = new FleetConfig
        {
            GpuMiner = new GpuMinerConfig
            {
                Enabled = true,
                Algorithm = "CR29",
                PoolUrl = "xtm-c29.kryptex.network:7040",
                User = "address/rig",
                PauseWhile = new GpuPauseConfig
                {
                    TcpPort = 11434,
                    ProcessNames = ["dontstarve_steam_x64", "blender"],
                    QuietSeconds = 300,
                },
            },
        };

        var resolved = config.GpuMinerFor(new NodeConfig { Name = "mks68i7rtx" });

        Assert.Equal(11434, resolved.PauseWhile!.TcpPort);
        Assert.Equal(["dontstarve_steam_x64", "blender"], resolved.PauseWhile.Processes);

        // The operator reads this line back to check what a node was actually left set to, so it
        // has to name every condition rather than whichever one happens to be looked at first.
        var described = FleetService.DescribeGpuSettings(resolved);
        Assert.Contains("port 11434 is busy", described);
        Assert.Contains("dontstarve_steam_x64 runs", described);
        Assert.Contains("blender runs", described);
    }

    [Fact]
    public void A_node_overrides_only_the_gpu_settings_it_names()
    {
        var config = new FleetConfig
        {
            GpuMiner = new GpuMinerConfig
            {
                Enabled = true,
                Algorithm = "CR29",
                PoolUrl = "xtm-c29.kryptex.network:7040",
                User = "address/rig",
                PauseWhile = new GpuPauseConfig { TcpPort = 11434, QuietSeconds = 300 },
            },
        };

        // A 4 GB card cannot run Cuckaroo29 at all, so the algorithm is the field a node most
        // often has to contradict — and it must not have to restate the pool login to do it.
        var weakCard = config.GpuMinerFor(new NodeConfig
        {
            Name = "rig-amd",
            GpuMiner = new GpuMinerConfig { Algorithm = "NEXA", PoolUrl = "nexapow.unmineable.com:3333" },
        });
        Assert.Equal("NEXA", weakCard.Algorithm);
        Assert.Equal("nexapow.unmineable.com:3333", weakCard.PoolUrl);
        Assert.Equal("address/rig", weakCard.User);
        Assert.Equal(11434, weakCard.PauseWhile!.TcpPort);

        var plainNode = config.GpuMinerFor(new NodeConfig { Name = "rig-nvidia" });
        Assert.Equal("CR29", plainNode.Algorithm);
        Assert.True(plainNode.Enabled);
    }

    [Fact]
    public void A_pause_rule_with_only_a_quiet_time_is_never_sent()
    {
        var config = new FleetConfig
        {
            GpuMiner = new GpuMinerConfig { PauseWhile = new GpuPauseConfig { QuietSeconds = 300 } },
        };

        // A rule with no condition to watch would stand a node down with nothing able to wake it.
        Assert.Null(config.GpuMinerFor(new NodeConfig { Name = "rig" }).PauseWhile);
    }

    [Fact]
    public void The_gpu_api_port_is_not_the_xmrig_api_port()
    {
        // Two miners answering on one port is a bug that only appears when both happen to run.
        Assert.NotEqual(FleetConfig.DefaultGpuApiPort, new AgentOptions().XmrigApiPort);
    }

    [Fact]
    public void Mining_stands_down_on_the_very_next_sample()
    {
        var rule = new GpuPauseRule();
        var now = DateTimeOffset.UtcNow;

        Assert.False(rule.Paused);
        Assert.True(rule.Update(busy: true, "port 11434 busy", quietSeconds: 300, now));

        // Somebody waiting on the card must not wait for the miner to notice them: the model
        // answered at 19 tok/s while the card mined and 53 tok/s once it stopped.
        Assert.True(rule.Paused);
    }

    [Fact]
    public void Quiet_does_not_resume_mining_until_the_wait_has_elapsed()
    {
        var rule = new GpuPauseRule();
        var start = DateTimeOffset.UtcNow;

        rule.Update(true, "port 11434 busy", 300, start);

        var quietFrom = start.AddSeconds(5);
        Assert.False(rule.Update(false, "", 300, quietFrom));
        Assert.False(rule.Update(false, "", 300, quietFrom.AddSeconds(299)));
        Assert.True(rule.Paused);

        Assert.True(rule.Update(false, "", 300, quietFrom.AddSeconds(300)));
        Assert.False(rule.Paused);
    }

    [Fact]
    public void One_more_request_restarts_the_wait()
    {
        var rule = new GpuPauseRule();
        var start = DateTimeOffset.UtcNow;

        rule.Update(true, "port 11434 busy", 300, start);
        rule.Update(false, "", 300, start.AddSeconds(200));

        // A conversation is a burst of requests with gaps in it. Restarting the miner in a gap
        // both wastes the restart and slows the next answer.
        rule.Update(true, "port 11434 busy", 300, start.AddSeconds(250));

        // The wait runs from the first quiet sample after the last request - 500s here - so the
        // 200 seconds of quiet already banked before it are deliberately forgotten.
        Assert.False(rule.Update(false, "", 300, start.AddSeconds(500)));
        Assert.False(rule.Update(false, "", 300, start.AddSeconds(799)));
        Assert.True(rule.Paused);

        Assert.True(rule.Update(false, "", 300, start.AddSeconds(800)));
        Assert.False(rule.Paused);
    }

    [Fact]
    public void Removing_the_rule_gives_the_miner_back()
    {
        var rule = new GpuPauseRule();
        var now = DateTimeOffset.UtcNow;

        rule.Update(true, "port 11434 busy", 300, now);
        rule.Clear(now.AddSeconds(1));

        // A rule that no longer exists must not keep holding the card.
        Assert.False(rule.Paused);
        Assert.Equal("no pause rule", rule.Reason);
    }

    /// <summary>
    /// The card comes back after a reboot, which is what makes the agent a real replacement for a
    /// scheduled task with a boot trigger. There is no separate autostart setting: `Enabled` is
    /// already the answer, and the agent reads it back out of the node's own miner.json.
    /// </summary>
    [Fact]
    public void An_enabled_card_is_still_enabled_after_the_agent_restarts()
    {
        using var dir = new TempDirectory();

        Store(dir).Update(new MinerConfigDto
        {
            GpuMiner = new GpuMinerSettingsDto
            {
                Enabled = true,
                Algorithm = "CR29",
                PoolUrl = "xtm-c29.kryptex.network:7040",
                User = "address/worker",
                ApiPort = 21556,
            },
        });

        // A second store over the same directory is what the next agent process sees.
        var afterRestart = Store(dir).Current;

        Assert.True(afterRestart.GpuMiner!.Enabled);
        Assert.Equal("CR29", afterRestart.GpuMiner.Algorithm);
        Assert.Equal(21556, afterRestart.GpuMiner.ApiPort);
    }

    /// <summary>
    /// A node that lost power while the card was paused must not come back owing a restart it has
    /// already made. The flag means "this agent stopped the miner and will start it again"; once
    /// the boot-time start has run, a later quiet tick would otherwise resume a miner nobody
    /// paused — and, worse, one the operator may have stopped by hand since.
    /// </summary>
    [Fact]
    public void A_boot_time_start_clears_the_debt_the_pause_flag_records()
    {
        using var dir = new TempDirectory();
        var store = Store(dir);

        store.Update(new MinerConfigDto
        {
            GpuMiner = new GpuMinerSettingsDto { Enabled = true },
            GpuStoppedByPause = true,
        });

        Assert.True(Store(dir).Current.GpuStoppedByPause);

        var cleared = store.Update(new MinerConfigDto { GpuStoppedByPause = false });

        // False must be storable, not read as "nothing said" — the whole clearing path is a
        // patch that names exactly one field and sets it to false.
        Assert.False(cleared.GpuStoppedByPause);
        Assert.True(cleared.GpuMiner!.Enabled);
    }
}
