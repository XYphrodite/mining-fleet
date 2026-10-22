using MiningFleet.Console.Ui;

namespace MiningFleet.Console.Tests;

/// <summary>
/// Guards Pearl's income accounting. Kryptex pays PRL at one whole coin, so a card can mine for days
/// with no payout to measure; the numbers instead come from the growth of what the pool holds for the
/// wallet. That has to stay exact across restarts and gaps, and it has to refuse to invent a rate it
/// has not watched long enough to know.
/// </summary>
public sealed class GpuIncomeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly GpuPoolTarget Prl = new("kryptex", "prl", "prl1address");

    private static GpuPoolStats Stats(double unconfirmed = 0, double confirmed = 0, double? paid = 0, DateTimeOffset? at = null) =>
        new(Prl, confirmed, unconfirmed, paid, 1.0, [], at ?? Now);

    private static GpuIncome Income(params (double HoursAgo, double Earned)[] samples) =>
        new(Stats(), samples.OrderByDescending(s => s.HoursAgo)
            .Select(s => new GpuIncomeSample(Now.AddHours(-s.HoursAgo), s.Earned)).ToList());

    [Fact]
    public void Earned_is_pending_plus_paid_and_is_unknown_when_either_is_missing()
    {
        Assert.Equal(1.75, GpuIncome.EarnedOf(Stats(unconfirmed: 0.25, confirmed: 0.5, paid: 1.0)));
        Assert.Null(GpuIncome.EarnedOf(Stats(unconfirmed: 0.25, paid: null)));
    }

    [Fact]
    public void A_payout_does_not_look_like_a_loss_because_paid_rises_as_pending_falls()
    {
        var before = GpuIncome.EarnedOf(Stats(unconfirmed: 1.0, paid: 0));
        var after = GpuIncome.EarnedOf(Stats(unconfirmed: 0.1, paid: 0.9));

        Assert.Equal(before!.Value, after!.Value, 9);
    }

    [Fact]
    public void The_last_day_is_the_growth_since_a_reading_a_day_old()
    {
        var income = Income((24, 1.0), (12, 1.5), (0, 2.5));

        Assert.Equal(1.5, income.Day!.Amount, 6);
        Assert.Equal(24, income.Day.Hours);
    }

    [Fact]
    public void Without_a_day_of_history_there_is_no_last_day_and_no_long_forecast()
    {
        var income = Income((5, 1.0), (0, 1.5));

        Assert.Null(income.Day);
        Assert.Null(income.Accrued(12));
        Assert.Equal(3, income.ForecastBasis!.Hours);
        // The 3 h mark falls between the 5 h and 0 h readings: 1.0 + 0.5 * 2/5 = 1.2, so 0.3 accrued since.
        Assert.Equal(0.3, income.Accrued(3)!.Amount, 6);
    }

    [Fact]
    public void A_window_starting_between_two_readings_is_interpolated_between_them()
    {
        // Readings at 30 h and 20 h ago straddle the 24 h mark: 4/10 of the way from 1.0 to 2.0.
        var income = Income((30, 1.0), (20, 2.0), (0, 4.0));

        Assert.Equal(4.0 - 1.6, income.Day!.Amount, 6);
    }

    [Fact]
    public void A_long_silence_is_not_interpolated_across()
    {
        // The console was closed for 40 hours: what happened in the middle is unknown.
        var income = Income((50, 1.0), (10, 5.0), (0, 6.0));

        Assert.Null(income.Day);
        Assert.NotNull(income.Accrued(6));
    }

    [Fact]
    public void The_forecast_scales_the_longest_observed_window_to_a_day()
    {
        var income = Income((6, 10.0), (0, 11.0));

        Assert.Equal(6, income.ForecastBasis!.Hours);
        Assert.Equal(4.0, income.ForecastPerDay!.Value, 6);
    }

    [Fact]
    public void A_falling_balance_is_not_income_and_gives_no_forecast()
    {
        var income = Income((6, 10.0), (0, 9.0));

        Assert.Null(income.ForecastPerDay);
    }

    [Fact]
    public void One_reading_says_nothing_about_a_rate()
    {
        var income = Income((0, 1.0));

        Assert.Null(income.Day);
        Assert.Null(income.ForecastPerDay);
        Assert.Null(income.SinceStart);
    }

    [Fact]
    public void Everything_since_the_first_reading_is_available_before_there_is_a_full_hour()
    {
        var income = Income((0.5, 1.0), (0, 1.2));

        Assert.Null(income.ForecastBasis);
        Assert.Equal(0.2, income.SinceStart!.Amount, 6);
        Assert.Equal(0.5, income.SinceStart.Hours, 6);
    }

    [Fact]
    public void Data_that_stopped_arriving_is_flagged_stale()
    {
        var fresh = new GpuIncome(Stats(at: Now.AddMinutes(-3)), []);
        var old = new GpuIncome(Stats(at: Now.AddMinutes(-30)), []);

        Assert.False(fresh.IsStale(Now));
        Assert.True(old.IsStale(Now));
    }

    [Fact]
    public void The_ledger_records_in_order_ignores_duplicates_and_prunes_old_readings()
    {
        var ledger = new GpuIncomeLedger();

        ledger.Record(Prl, Now.AddDays(-10), 1.0);
        ledger.Record(Prl, Now.AddHours(-1), 2.0);
        ledger.Record(Prl, Now.AddHours(-1).AddSeconds(10), 2.1);
        var history = ledger.Record(Prl, Now, 3.0);

        Assert.Equal([2.0, 3.0], history.Select(s => s.Earned));
    }

    [Fact]
    public void A_reading_the_pool_could_not_complete_is_not_recorded_as_zero()
    {
        var ledger = new GpuIncomeLedger();
        ledger.Record(Prl, Now.AddHours(-1), 2.0);

        var history = ledger.Record(Prl, Now, null);

        Assert.Single(history);
    }

    [Fact]
    public void The_history_survives_a_restart_and_never_stores_the_wallet_address()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "gpu-income.json");

        new GpuIncomeLedger(path).Record(Prl, Now, 2.0);
        var reopened = new GpuIncomeLedger(path);

        Assert.Equal(2.0, Assert.Single(reopened.History(Prl)).Earned);
        Assert.DoesNotContain("prl1address", File.ReadAllText(path));
    }

    [Fact]
    public void A_damaged_history_file_starts_fresh_instead_of_failing()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "gpu-income.json");
        File.WriteAllText(path, "{ not json");

        var ledger = new GpuIncomeLedger(path);

        Assert.Empty(ledger.History(Prl));
    }

    [Fact]
    public void Two_wallets_never_share_a_history()
    {
        var ledger = new GpuIncomeLedger();
        ledger.Record(Prl, Now, 2.0);

        Assert.Empty(ledger.History(Prl with { Address = "someone-else" }));
    }

    [Theory]
    [InlineData(0.0015814, "0001581PRL")]
    [InlineData(2.5, "25000PRL")]
    [InlineData(1234.5, "123450PRL")]
    public void Small_pearl_amounts_keep_enough_digits_to_be_non_zero(double value, string expected)
    {
        var text = GpuIncomePanel.Amount(value, "PRL");

        // Separators follow the machine's culture, so only the digits and the coin are compared.
        Assert.Equal(expected, new string(text.Where(char.IsLetterOrDigit).ToArray()));
    }
}
