using MiningFleet.Agent;
using MiningFleet.Console;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Tests;

/// <summary>
/// Guards the watchdog: a wanted miner that died comes back, a stopped one stays down.
///
/// The failure this protects against is the quiet 04:37 on `re-7lqd67ahcm0r`, where xmrig
/// and lolMiner both died mid-night with the agent alive, no pause or throttle flag set,
/// and nothing anywhere to restart them. The wish itself is recorded by starts and stops;
/// a crash records nothing, which is how the two tell apart.
/// </summary>
public sealed class WatchdogTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 22, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_retry_is_immediate_and_later_ones_back_off()
    {
        Assert.Equal(TimeSpan.Zero, WatchdogPolicy.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(15), WatchdogPolicy.Delay(2));
        Assert.Equal(TimeSpan.FromSeconds(30), WatchdogPolicy.Delay(3));
        Assert.Equal(TimeSpan.FromMinutes(1), WatchdogPolicy.Delay(4));
        Assert.Equal(TimeSpan.FromMinutes(2), WatchdogPolicy.Delay(5));

        // A miner that never starts (bad pool, bad wallet) must not hammer the pool forever.
        Assert.Equal(TimeSpan.FromMinutes(5), WatchdogPolicy.Delay(6));
        Assert.Equal(TimeSpan.FromMinutes(5), WatchdogPolicy.Delay(100));
    }

    [Fact]
    public void A_death_restarts_at_once_but_a_failure_waits_its_turn()
    {
        // No attempt yet: due, so a miner killed after hours of work comes back immediately.
        Assert.True(WatchdogPolicy.IsDue(1, null, Start));

        var attempted = Start;
        Assert.False(WatchdogPolicy.IsDue(2, attempted, attempted.AddSeconds(14)));
        Assert.True(WatchdogPolicy.IsDue(2, attempted, attempted.AddSeconds(15)));
    }

    [Fact]
    public void The_wish_survives_an_agent_restart()
    {
        using var dir = new TempDirectory();
        var store = new MinerConfigStore(dir.Path);

        store.Update(new MinerConfigDto { MinerWanted = true, GpuWanted = true });

        // MinerConfigStore.Update enumerates every field by hand, so one forgotten there is
        // accepted over HTTP, echoed back as saved, and dropped on the next write.
        var afterRestart = new MinerConfigStore(dir.Path).Current;
        Assert.True(afterRestart.MinerWanted);
        Assert.True(afterRestart.GpuWanted);
    }

    [Fact]
    public void A_stop_clears_the_wish_but_keeps_the_pause_memory()
    {
        using var dir = new TempDirectory();
        var store = new MinerConfigStore(dir.Path);

        store.Update(new MinerConfigDto { MinerWanted = true, MinerStoppedByPause = true });
        var saved = store.Update(new MinerConfigDto { MinerWanted = false });

        // An explicit stop wins, and the pause still owns the resume: clearing one must not
        // clear the other, or a game closing would either mine over somebody's shoulder or
        // never mine again depending on which write landed last.
        Assert.False(saved.MinerWanted);
        Assert.True(saved.MinerStoppedByPause);
    }

    [Fact]
    public void A_silent_miner_accrues_and_a_healthy_one_clears()
    {
        // Running, seen, silent: the clock starts and holds its first-zero time.
        var zero = WatchdogPolicy.NextZeroSince(true, apiOk: true, hashrate: 0, zeroSince: null, now: Start);
        Assert.Equal(Start, zero);
        Assert.Equal(Start, WatchdogPolicy.NextZeroSince(true, true, 0, zero, Start.AddMinutes(3)));

        // Not stuck yet at three minutes: a reconnect and a fresh start both read zero.
        Assert.False(WatchdogPolicy.IsStuck(zero, Start.AddMinutes(3)));
        Assert.True(WatchdogPolicy.IsStuck(zero, Start.AddMinutes(5)));

        // Anything else clears it: stopped, blind, or hashing again.
        Assert.Null(WatchdogPolicy.NextZeroSince(false, true, 0, zero, Start.AddMinutes(6)));
        Assert.Null(WatchdogPolicy.NextZeroSince(true, false, null, zero, Start.AddMinutes(6)));
        Assert.Null(WatchdogPolicy.NextZeroSince(true, true, null, zero, Start.AddMinutes(6)));
        Assert.Null(WatchdogPolicy.NextZeroSince(true, true, 1454.9, zero, Start.AddMinutes(6)));
    }

    [Fact]
    public void A_blind_miner_is_held_not_restarted()
    {
        // Null API on a running miner means the agent cannot see it — possibly one started
        // by hand — so no clock ever starts, however long it stays null.
        DateTimeOffset? zero = null;
        for (var m = 0; m <= 30; m++)
            zero = WatchdogPolicy.NextZeroSince(true, apiOk: false, hashrate: null, zero, Start.AddMinutes(m));

        Assert.Null(zero);
        Assert.False(WatchdogPolicy.IsStuck(zero, Start.AddMinutes(30)));
    }

    [Fact]
    public void An_old_miner_json_wants_nothing()
    {
        using var dir = new TempDirectory();
        var store = new MinerConfigStore(dir.Path);

        // A node that never heard a start or a stop from this agent version answers null,
        // which the watchdog reads as not wanted. The first explicit start records it.
        Assert.Null(store.Current.MinerWanted);
        Assert.Null(store.Current.GpuWanted);
    }
}
