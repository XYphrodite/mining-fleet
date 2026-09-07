using XmrigFleet.Agent;
using XmrigFleet.Console;
using XmrigFleet.Contracts;

namespace XmrigFleet.Console.Tests;

/// <summary>
/// Guards the two ceilings a node mines under: how much of its own full speed it may use, and how
/// hot it may get.
///
/// The thermal half is where the quiet failures live. A governor with no margin settles exactly on
/// its limit and then changes the miner's thread count forever; one that treats an unreadable
/// sensor as cool cooks the node it was installed to protect; and one that steps up as eagerly as
/// it steps down spends the day oscillating instead of mining.
/// </summary>
public sealed class CpuBudgetTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The fleet's i7-12700KF: 12 physical cores, 25 MB of L3.</summary>
    private const int I7Cores = 12;
    private const long I7L3 = 25L * 1024 * 1024;

    [Fact]
    public void Full_speed_is_one_thread_per_core_until_the_cache_runs_out()
    {
        // All three nodes in this fleet, against what xmrig actually chose on each.
        Assert.Equal(12, CpuReservation.FullThreadCount(I7Cores, I7L3));
        Assert.Equal(14, CpuReservation.FullThreadCount(14, 35L * 1024 * 1024));
        Assert.Equal(6, CpuReservation.FullThreadCount(6, 12L * 1024 * 1024));

        // RandomX wants 2 MB of L3 per thread, so a cache-starved chip runs fewer threads than it
        // has cores. Getting this wrong means over-provisioning a node into cache thrash.
        Assert.Equal(4, CpuReservation.FullThreadCount(16, 8L * 1024 * 1024));

        // A machine that will not report its cache is taken at its core count rather than guessed
        // downward: cache binds only sometimes, and inventing a limit would slow every node.
        Assert.Equal(12, CpuReservation.FullThreadCount(I7Cores, 0));
    }

    [Fact]
    public void The_percentage_is_a_share_of_the_miner_not_of_the_machine()
    {
        // The convention the throttle ladder already uses. 67% of twelve threads is eight, which
        // is the setting measured at 89.3 C on the live node.
        Assert.Equal(8, CpuReservation.ThreadsFor(12, 67));
        Assert.Equal(12, CpuReservation.ThreadsFor(12, 100));
        Assert.Equal(12, CpuReservation.ThreadsFor(12, null));
    }

    [Fact]
    public void A_ceiling_is_rounded_down_because_it_is_a_ceiling()
    {
        // 67% of twelve is 8.04. Rounding up would run the node at nine threads - 75% of full
        // speed - under a setting that plainly says 67, and the operator would have no way to
        // tell from the number they typed.
        Assert.Equal(8, CpuReservation.ThreadsFor(12, 67));
        Assert.Equal(8, CpuReservation.ThreadsFor(12, 74));
        Assert.Equal(9, CpuReservation.ThreadsFor(12, 75));
    }

    [Fact]
    public void A_tiny_percentage_gives_a_slow_miner_and_never_a_stopped_one()
    {
        // Rounded up, and floored at one. Stopping the miner is what the throttle's level 0 is
        // for; a ceiling that silently stopped a rig would look exactly like a crash.
        Assert.Equal(1, CpuReservation.ThreadsFor(12, 1));
        Assert.Equal(1, CpuReservation.ThreadsFor(12, 0));
    }

    [Fact]
    public void The_mining_cpus_are_the_first_thread_of_each_core()
    {
        var cores = new List<ulong>();
        for (var p = 0; p < 8; p++) cores.Add(3UL << (p * 2));
        for (var e = 16; e < 20; e++) cores.Add(1UL << e);

        // Exactly the list xmrig picked for itself on that node. A second thread on the same core
        // would share execution units with a RandomX thread already saturating them.
        Assert.Equal(new[] { 0, 2, 4, 6, 8, 10, 12, 14, 16, 17, 18, 19 }, CpuReservation.MiningCpus(cores));
    }

    [Fact]
    public void Without_a_temperature_limit_the_percentage_is_the_whole_answer()
    {
        var governor = new ThermalGovernor();

        // Straight back to the ceiling in one move, not a thread every ten minutes: a node whose
        // thermal rule was removed is not a node recovering from being hot.
        Assert.Equal(12, governor.Decide(current: 6, ceiling: 12, temperature: 99, limit: null, Start));
    }

    [Fact]
    public void Over_the_limit_drops_a_thread()
    {
        var governor = new ThermalGovernor();

        Assert.Equal(11, governor.Decide(12, 12, temperature: 95, limit: 90, Start));
        Assert.Contains("over", governor.Reason);
    }

    [Fact]
    public void A_second_thread_is_not_dropped_before_the_first_has_settled()
    {
        var governor = new ThermalGovernor();
        governor.Decide(12, 12, 95, 90, Start);

        // The reading right after a change still describes the old thread count. Acting on it
        // walks a node down to its floor in three ticks over one hot minute.
        Assert.Equal(11, governor.Decide(11, 12, 95, 90, Start.AddSeconds(30)));
        Assert.Equal(10, governor.Decide(11, 12, 95, 90, Start.AddSeconds(61)));
    }

    [Fact]
    public void One_thread_is_the_floor_even_when_it_is_still_too_hot()
    {
        var governor = new ThermalGovernor();

        // A machine that cannot cool itself at one thread has a cooling fault, and stopping its
        // miner would hide that rather than fix it.
        Assert.Equal(1, governor.Decide(1, 12, temperature: 99, limit: 90, Start.AddMinutes(30)));
        Assert.Contains("floor", governor.Reason);
    }

    [Fact]
    public void An_unreadable_sensor_holds_rather_than_guesses()
    {
        var governor = new ThermalGovernor();

        // Two nodes in this fleet cannot read a CPU temperature at all. Stepping up would assume
        // they are cool and stepping down would assume they are hot; neither is known.
        Assert.Equal(8, governor.Decide(current: 8, ceiling: 12, temperature: null, limit: 90, Start));
        Assert.Contains("no CPU temperature", governor.Reason);
    }

    [Fact]
    public void Coming_back_up_waits_out_a_long_cool_stretch()
    {
        var governor = new ThermalGovernor();
        var cool = 90 - ThermalGovernor.Margin - 1;

        Assert.Equal(8, governor.Decide(8, 12, cool, 90, Start));
        Assert.Equal(8, governor.Decide(8, 12, cool, 90, Start.AddMinutes(9)));

        // Heat is cumulative and its cost is paid in silicon, so the wait is long and one-sided.
        Assert.Equal(9, governor.Decide(8, 12, cool, 90, Start.AddMinutes(10)));
    }

    [Fact]
    public void Sitting_just_under_the_limit_is_not_cool_enough_to_try_another_thread()
    {
        var governor = new ThermalGovernor();

        // Without the margin a node settles exactly on its limit and then pays a thread change
        // for every crossing, forever.
        Assert.Equal(8, governor.Decide(8, 12, temperature: 89, limit: 90, Start));
        Assert.Equal(8, governor.Decide(8, 12, 89, 90, Start.AddHours(2)));
        Assert.Contains("within", governor.Reason);
    }

    [Fact]
    public void One_hot_reading_restarts_the_wait_to_come_back_up()
    {
        var governor = new ThermalGovernor();
        var cool = 90 - ThermalGovernor.Margin - 1;

        governor.Decide(8, 12, cool, 90, Start);

        // Nine cool minutes banked, one minute short of earning a thread back - and then a spike.
        Assert.Equal(7, governor.Decide(8, 12, 95, 90, Start.AddMinutes(9)));

        // Those nine minutes are deliberately forgotten: the wait restarts from the first cool
        // sample after the spike, the same shape the GPU pause uses for a burst of requests with
        // gaps in it. Ten more minutes from there, not one.
        Assert.Equal(7, governor.Decide(7, 12, cool, 90, Start.AddMinutes(18)));
        Assert.Equal(7, governor.Decide(7, 12, cool, 90, Start.AddMinutes(27)));
        Assert.Equal(8, governor.Decide(7, 12, cool, 90, Start.AddMinutes(28)));
    }

    [Fact]
    public void The_governor_never_goes_above_what_the_percentage_allows()
    {
        var governor = new ThermalGovernor();
        var cool = 60.0;

        // Two bounds on one number, so the answer is the lower of them and there is nothing to
        // resolve between them.
        Assert.Equal(8, governor.Decide(8, ceiling: 8, temperature: cool, limit: 90, Start.AddHours(3)));
        Assert.Contains("full", governor.Reason);
    }

    [Fact]
    public void A_node_answers_with_the_ceilings_it_stored()
    {
        using var dir = new TempDirectory();
        var store = new MinerConfigStore(dir.Path);

        store.Update(new MinerConfigDto { MaxCpuPercent = 67, MaxCpuTemperatureC = 90 });

        // MinerConfigStore.Update enumerates every field by hand, so one forgotten there is
        // accepted over HTTP, echoed back as saved, and dropped on the next write.
        var afterRestart = new MinerConfigStore(dir.Path).Current;
        Assert.Equal(67, afterRestart.MaxCpuPercent);
        Assert.Equal(90, afterRestart.MaxCpuTemperatureC);

        // And a push about something else leaves them alone.
        var saved = store.Update(new MinerConfigDto { AutoStartMiner = true });
        Assert.Equal(67, saved.MaxCpuPercent);
        Assert.Equal(90, saved.MaxCpuTemperatureC);
    }

    [Fact]
    public void A_node_sets_its_own_ceilings_and_otherwise_follows_the_fleet()
    {
        var config = new FleetConfig { MaxCpuTemperatureC = 85 };

        var hot = config.CpuBudgetFor(new NodeConfig { Name = "mks68i7rtx", MaxCpuPercent = 67, MaxCpuTemperatureC = 90 });
        Assert.Equal(67, hot.MaxCpuPercent);
        Assert.Equal(90, hot.MaxCpuTemperatureC);

        // The temperature is a fact about a cooler and a room, so the node's answer wins; what it
        // does not name still comes from the fleet.
        var other = config.CpuBudgetFor(new NodeConfig { Name = "rig" });
        Assert.Null(other.MaxCpuPercent);
        Assert.Equal(85, other.MaxCpuTemperatureC);
    }

    [Fact]
    public void A_fleet_that_has_set_neither_ceiling_invents_neither()
    {
        var (percent, temperature) = new FleetConfig().CpuBudgetFor(new NodeConfig { Name = "rig" });

        // Picking a temperature on the operator's behalf would be picking one for hardware this
        // console has never seen. The agent leaves a node alone when both are absent.
        Assert.Null(percent);
        Assert.Null(temperature);
        Assert.Equal("none", FleetService.Ceiling(percent, temperature));
    }
}
