using Microsoft.Extensions.Logging.Abstractions;
using MiningFleet.Agent;
using MiningFleet.Console;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Tests;

/// <summary>
/// Guards the three things the power limit gets wrong in ways nobody notices until a rig has been
/// mining at a quarter speed for a week: the asymmetry between coming down and going up, a settings
/// push that quietly discards the tuned ladder, and a per-node exception that fails to override.
/// </summary>
public class ThrottleTests
{
    private static readonly IReadOnlyList<ThrottleStepDto> Steps = ThrottleSettingsDto.DefaultSteps;

    [Fact]
    public void Load_pushes_the_miner_down_on_the_very_next_sample()
    {
        var ladder = new ThrottleLadder();
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(100, ladder.Current);
        Assert.True(ladder.Update(50, Steps, floorLevel: 0, rampUpSeconds: 120, now));

        // Somebody sitting at the machine must not wait for the miner to notice them.
        Assert.Equal(25, ladder.Current);
    }

    [Fact]
    public void Quiet_does_not_raise_the_miner_until_the_ramp_has_elapsed()
    {
        var ladder = new ThrottleLadder();
        var start = DateTimeOffset.UtcNow;

        ladder.Update(80, Steps, 0, 120, start);
        Assert.Equal(0, ladder.Current);

        // The clock starts at the first quiet sample, not at the moment the load fell: the wait
        // measures quiet that was actually observed.
        var quietFrom = start.AddSeconds(10);
        Assert.False(ladder.Update(0, Steps, 0, 120, quietFrom));
        Assert.False(ladder.Update(0, Steps, 0, 120, quietFrom.AddSeconds(119)));
        Assert.Equal(0, ladder.Current);

        Assert.True(ladder.Update(0, Steps, 0, 120, quietFrom.AddSeconds(121)));
        Assert.Equal(100, ladder.Current);
    }

    [Fact]
    public void A_burst_during_the_ramp_restarts_the_wait()
    {
        var ladder = new ThrottleLadder();
        var start = DateTimeOffset.UtcNow;

        ladder.Update(80, Steps, 0, 120, start);
        ladder.Update(0, Steps, 0, 120, start.AddSeconds(100));   // waiting to climb

        // A single burst - opening a folder, a browser tab - and the wait begins again, which is
        // what stops the miner rocking up and down while somebody works.
        ladder.Update(80, Steps, 0, 120, start.AddSeconds(110));
        Assert.Equal(0, ladder.Current);

        Assert.False(ladder.Update(0, Steps, 0, 120, start.AddSeconds(200)));
        Assert.Equal(0, ladder.Current);
    }

    [Fact]
    public void The_floor_stops_a_node_being_taken_all_the_way_down()
    {
        // A node whose miner must never be stopped - restarting it there risks the huge pages
        // that decide RandomX throughput - is held at the floor however busy the machine gets.
        Assert.Equal(25, ThrottleLadder.LevelFor(Steps, otherCpuPercent: 95, floorLevel: 25));
        Assert.Equal(0, ThrottleLadder.LevelFor(Steps, otherCpuPercent: 95, floorLevel: 0));
    }

    [Fact]
    public void A_ladder_listed_out_of_order_still_reads_correctly()
    {
        // These come from a file an operator edits by hand. Taking the last matching line as
        // written would throttle by whichever rung happened to be typed last.
        var shuffled = new List<ThrottleStepDto> { new(70, 0), new(0, 100), new(25, 50), new(45, 25), new(10, 75) };

        Assert.Equal(100, ThrottleLadder.LevelFor(shuffled, 5, 0));
        Assert.Equal(75, ThrottleLadder.LevelFor(shuffled, 15, 0));
        Assert.Equal(50, ThrottleLadder.LevelFor(shuffled, 30, 0));
        Assert.Equal(0, ThrottleLadder.LevelFor(shuffled, 90, 0));
    }

    [Fact]
    public void Switching_the_limit_on_does_not_discard_the_tuned_ladder()
    {
        using var directory = new TempDirectory();
        var store = new MinerConfigStore(directory.Path);

        var tuned = new List<ThrottleStepDto> { new(0, 100), new(35, 50) };
        store.Update(new MinerConfigDto
        {
            Throttle = new ThrottleSettingsDto { Steps = tuned, FloorLevel = 25, RampUpSeconds = 300 },
        });

        // Replacing the whole object on any push would take a week of tuning with it. The console
        // always sends the full rules, but nothing in the contract says a caller must.
        var saved = store.Update(new MinerConfigDto { Throttle = new ThrottleSettingsDto { Enabled = true } });

        Assert.True(saved.Throttle!.Enabled);
        Assert.Equal(25, saved.Throttle.FloorLevel);
        Assert.Equal(300, saved.Throttle.RampUpSeconds);
        Assert.Equal(2, saved.Throttle.Steps!.Count);
    }

    [Fact]
    public void Handing_control_back_needs_its_own_flag_because_null_means_leave_alone()
    {
        using var directory = new TempDirectory();
        var store = new MinerConfigStore(directory.Path);

        store.Update(new MinerConfigDto { Throttle = new ThrottleSettingsDto { Enabled = true, ManualLevel = 50 } });
        Assert.Equal(50, store.Current.Throttle!.ManualLevel);

        // A push that says nothing about the pinned level must not clear it...
        store.Update(new MinerConfigDto { Throttle = new ThrottleSettingsDto { RampUpSeconds = 60 } });
        Assert.Equal(50, store.Current.Throttle!.ManualLevel);

        // ...and only the explicit flag does.
        store.Update(new MinerConfigDto { Throttle = new ThrottleSettingsDto { ClearManualLevel = true } });
        Assert.Null(store.Current.Throttle!.ManualLevel);
    }

    [Fact]
    public void A_node_overrides_only_the_settings_it_names()
    {
        var config = new FleetConfig
        {
            Throttle = new ThrottleConfig { Enabled = true, FloorLevel = 25, RampUpSeconds = 120 },
            Nodes =
            [
                new NodeConfig { Name = "gaming", Throttle = new ThrottleConfig { FloorLevel = 0 } },
                new NodeConfig { Name = "headless", Throttle = new ThrottleConfig { Enabled = false } },
                new NodeConfig { Name = "plain" },
            ],
        };

        var gaming = config.ThrottleFor(config.FindNode("gaming")!);
        Assert.Equal(0, gaming.FloorLevel);          // its own
        Assert.True(gaming.Enabled);                 // the fleet's
        Assert.Equal(120, gaming.RampUpSeconds);     // the fleet's

        Assert.False(config.ThrottleFor(config.FindNode("headless")!).Enabled);
        Assert.Equal(25, config.ThrottleFor(config.FindNode("plain")!).FloorLevel);
    }

    [Fact]
    public void A_node_with_no_ladder_of_its_own_is_sent_the_default_one()
    {
        // Never an empty ladder: a node that received one would read every load as full speed and
        // the feature would look enabled while doing nothing at all.
        var config = new FleetConfig { Nodes = [new NodeConfig { Name = "rig" }] };

        var resolved = config.ThrottleFor(config.Nodes[0]);
        Assert.NotEmpty(resolved.Steps!);
        Assert.Equal(ThrottleSettingsDto.DefaultSteps.Count, resolved.Steps!.Count);
    }

    [Fact]
    public void A_fresh_limit_never_claims_the_miner_is_already_uncapped()
    {
        // The job object outlives the agent, so a restarted agent inherits whatever cap the last
        // one left. Reporting 100 before applying anything would let the caller skip the very call
        // that lifts an inherited 25%, and the node would mine at a quarter speed in silence.
        var limit = new MinerCpuLimit(NullLogger<MinerCpuLimit>.Instance);
        Assert.NotEqual(100, limit.AppliedLevel);

        limit.Forget();
        Assert.NotEqual(100, limit.AppliedLevel);
    }

    [Fact]
    public void A_rung_means_a_share_of_the_miner_not_a_share_of_the_machine()
    {
        // Six mining threads on twelve logical CPUs: the miner wants half the machine. Holding it
        // to half speed therefore means a quarter of the machine, and a cap of half would be one
        // the miner never reaches. Measured on exactly that node: pinning 50% before this
        // conversion existed changed the hashrate by nothing.
        Assert.Equal(25, MinerCpuLimit.MachineRateFor(level: 50, minerFullSharePercent: 50));
        Assert.Equal(12, MinerCpuLimit.MachineRateFor(level: 25, minerFullSharePercent: 50));
        Assert.Equal(38, MinerCpuLimit.MachineRateFor(level: 75, minerFullSharePercent: 50));

        // A miner that really does want the whole machine is capped at the rung itself.
        Assert.Equal(25, MinerCpuLimit.MachineRateFor(level: 25, minerFullSharePercent: 100));

        // Never zero: a job object rate of zero is not a legal cap, and a stopped miner is the
        // other mechanism's job.
        Assert.Equal(1, MinerCpuLimit.MachineRateFor(level: 1, minerFullSharePercent: 1));
    }

    [Fact]
    public void The_first_load_sample_is_not_usable()
    {
        // A CPU percentage is the difference between two samples. Handing the ladder a zero here
        // would read as a machine nobody is using.
        var reader = new SystemLoadReader();
        Assert.False(reader.Read().Usable);
    }

    [Fact]
    public void A_reset_makes_the_next_sample_unusable_again()
    {
        // Reset is called after the throttle restarts the miner: the new process starts its CPU
        // time from zero, and without this the miner's own spin-up would be counted as somebody
        // else's load - which would have the throttle stop the miner it had just started.
        var reader = new SystemLoadReader();
        reader.Read();
        reader.Read();

        reader.Reset();
        Assert.False(reader.Read().Usable);
    }
}
