using MiningFleet.Console;

namespace MiningFleet.Console.Tests;

/// <summary>
/// Guards the reading of what a card actually earned - the half of the fleet's money that
/// Hashvault cannot see, because the card mines a coin it has never heard of.
///
/// Two things here fail quietly rather than loudly, which is why they are tested. A wrong wallet
/// address does not produce an error from a balance API; it produces somebody else's zero. And a
/// daily rate taken from the wrong field looks entirely plausible while being several times out.
/// </summary>
public sealed class GpuPoolTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 14, 0, 0, TimeSpan.Zero);

    private static GpuMinerConfig Kryptex(string pool = "xtm-c29.kryptex.network:7040",
                                          string user = "12P9address/MKS68I7RTX") =>
        new() { Enabled = true, PoolUrl = pool, User = user };

    private static GpuPoolStats Stats(params (int HoursAgo, double Amount)[] payouts) =>
        new(new GpuPoolTarget("kryptex", "xtm-c29", "12P9address"),
            Confirmed: 90.35, Unconfirmed: 173.33, Paid: 1155.12, Threshold: 200,
            Payouts: payouts.Select(p => new GpuPayout(Now.AddHours(-p.HoursAgo), p.Amount)).ToList());

    [Fact]
    public void The_pool_and_the_coin_are_read_out_of_the_stratum_host()
    {
        var target = GpuPoolService.TargetFor(Kryptex());

        Assert.NotNull(target);
        Assert.Equal("kryptex", target!.Provider);
        // The slug is algorithm-specific. Asking this API for "xtm" answers 404, and the same card
        // on SHA3x lives at a different slug entirely.
        Assert.Equal("xtm-c29", target.Coin);
        Assert.Equal("12P9address", target.Address);
    }

    [Fact]
    public void The_worker_name_is_not_part_of_the_address()
    {
        var target = GpuPoolService.TargetFor(Kryptex(user: "12P9address/MKS68I7RTX"));

        // Sent whole, this reaches the balance endpoint as an address that does not exist - and
        // the pool answers a 200 with zeroes rather than a 404.
        Assert.Equal("12P9address", target!.Address);
    }

    [Fact]
    public void A_pool_this_console_cannot_read_is_declined_rather_than_guessed_at()
    {
        // unMineable spells its login XMR:address.worker, so splitting on '/' would hand a balance
        // API a confident, wrong address. Knowing nothing is the better answer.
        Assert.Null(GpuPoolService.TargetFor(Kryptex(pool: "rx.unmineable.com:3333", user: "XMR:12P9address.rig")));
        Assert.Null(GpuPoolService.TargetFor(Kryptex(pool: "localhost:4444")));
    }

    [Fact]
    public void A_card_that_is_not_mining_has_nothing_to_read()
    {
        Assert.Null(GpuPoolService.TargetFor(null));
        Assert.Null(GpuPoolService.TargetFor(new GpuMinerConfig { Enabled = false, PoolUrl = "xtm-c29.kryptex.network:7040", User = "a/b" }));
        Assert.Null(GpuPoolService.TargetFor(new GpuMinerConfig { Enabled = true, PoolUrl = "xtm-c29.kryptex.network:7040" }));
    }

    /// <summary>
    /// The rate comes from money that was actually sent, over the window the payouts cover. The
    /// pool also publishes reward.week, which is tempting and wrong: on a card mining for a day it
    /// holds the whole run, so dividing by seven under-reports by that factor. Measured against the
    /// live fleet it gave 178 XTM/day where the payouts said 1,039.
    /// </summary>
    [Fact]
    public void The_daily_rate_is_measured_over_the_window_the_payouts_cover()
    {
        // The oldest payout starts the window; the three after it are what was earned inside it.
        var stats = Stats((22, 202.61), (19, 214.89), (13, 255.32), (7, 219.22), (0, 263.07));

        var perDay = stats.PaidPerDay();

        Assert.NotNull(perDay);
        // 952.50 XTM over 22 h.
        Assert.InRange(perDay!.Value, 1030, 1045);
        Assert.Equal(22, stats.PayoutSpan()!.Value.TotalHours, 1);
    }

    [Fact]
    public void One_payout_says_how_much_but_never_how_fast()
    {
        var stats = Stats((3, 202.61));

        // A single payment covers an unknown stretch of mining. Dividing it by anything invents
        // the divisor, and the screen says "too few payouts to say" instead.
        Assert.Null(stats.PayoutSpan());
        Assert.Null(stats.PaidPerDay());
    }

    [Fact]
    public void A_long_history_is_read_over_the_last_day_rather_than_all_of_it()
    {
        // Ten days of payouts, but the last day is what the card is doing now: difficulty and the
        // card's own state both move, and an average over a fortnight describes neither.
        var stats = Stats((240, 100), (200, 100), (20, 400), (10, 400), (0, 400));

        var span = stats.PayoutSpan();
        var perDay = stats.PaidPerDay();

        Assert.Equal(24, span!.Value.TotalHours, 1);
        // Only the three inside the last 24 h count, and 1200 over a day is 1200.
        Assert.InRange(perDay!.Value, 1195, 1205);
    }

    [Fact]
    public void Pending_is_everything_earned_and_not_yet_sent()
    {
        var stats = Stats((5, 200), (0, 200));

        Assert.Equal(263.68, stats.Pending!.Value, 2);
    }
}
