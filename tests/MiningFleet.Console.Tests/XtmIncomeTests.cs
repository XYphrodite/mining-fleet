using System.Net;
using System.Text.Json;
using MiningFleet.Console;
using MiningFleet.Console.Ui;
using Spectre.Console.Testing;

namespace MiningFleet.Console.Tests;

public sealed class XtmIncomeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 18, 35, 0, TimeSpan.Zero);
    private const string Pool = """{"config":{"symbol":"XTM","coinUnits":1000000}}""";

    private static string Wallet(int historyHours = 50, double day = 111777698,
        double twoDays = 327008491) => JsonSerializer.Serialize(new
    {
        rewardStats = new[] { Window(24, day), Window(48, twoDays), Window(72, 600000000) },
        rewards = new[] { new { timestamp = Now.AddHours(-historyHours).ToUnixTimeMilliseconds(), minerReward = 1000000 } },
        stats = new { paid = 999999999999, locked = 111777698, unlocked = 215230793 }
    });

    private static object Window(int hours, double amount) => new
    {
        period = $"{hours}h", amount,
        startTime = Now.AddHours(-hours).ToUnixTimeMilliseconds() + 1,
        endTime = Now.ToUnixTimeMilliseconds()
    };

    private static XtmIncome? Parse(string wallet, string pool = Pool, DateTimeOffset? observed = null)
    {
        using var p = JsonDocument.Parse(pool);
        using var w = JsonDocument.Parse(wallet);
        return XtmIncomeService.Parse(p.RootElement, w.RootElement, Now, observed ?? Now);
    }

    [Fact]
    public void Rewards_are_scaled_and_payouts_are_not_income()
    {
        var income = Parse(Wallet())!;
        Assert.Equal(111.777698, income.Day!.Amount, 6);
        var basis = income.ForecastBasis(Now)!;
        Assert.Equal(48, basis.Hours);
        Assert.Equal(163.5042455, basis.Amount * 24 / basis.Hours, 6);
    }

    [Fact]
    public void Short_history_has_actual_accruals_but_no_extrapolated_forecast()
    {
        var income = Parse(Wallet(historyHours: 6))!;
        Assert.NotNull(income.Day);
        Assert.Null(income.ForecastBasis(Now));
        Assert.Equal(24, Parse(Wallet(historyHours: 25))!.ForecastBasis(Now)!.Hours);
        Assert.Equal(72, Parse(Wallet(historyHours: 80))!.ForecastBasis(Now)!.Hours);
    }

    [Fact]
    public void Zero_is_valid_and_downtime_is_included_in_the_mean()
    {
        var income = Parse(Wallet(day: 0, twoDays: 200000000))!;
        Assert.Equal(0, income.Day!.Amount);
        var basis = income.ForecastBasis(Now)!;
        Assert.Equal(100, basis.Amount * 24 / basis.Hours);
    }

    [Fact]
    public void Observations_preserve_coverage_when_the_pool_truncates_reward_history()
    {
        var income = Parse(Wallet(historyHours: 2), observed: Now.AddHours(-49))!;
        Assert.Equal(48, income.ForecastBasis(Now)!.Hours);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"rewardStats\":null}")]
    [InlineData("{\"rewardStats\":[null]}")]
    [InlineData("{\"rewardStats\":[{\"period\":\"24h\",\"amount\":0}]}")]
    public void Missing_fields_are_not_zero_earnings(string wallet) => Assert.Null(Parse(wallet));

    [Theory]
    [InlineData("{\"config\":{\"symbol\":123,\"coinUnits\":1000000}}")]
    [InlineData("{\"config\":{\"symbol\":\"XMR\",\"coinUnits\":1000000}}")]
    [InlineData("{\"config\":{\"symbol\":\"XTM\",\"coinUnits\":0}}")]
    [InlineData("{\"config\":{\"symbol\":\"XTM\",\"coinUnits\":\"NaN\"}}")]
    public void Invalid_units_or_wrong_coin_are_not_accepted(string pool) => Assert.Null(Parse(Wallet(), pool));

    [Fact]
    public void Numeric_strings_are_accepted_but_negative_or_nonfinite_rewards_are_not()
    {
        Assert.Equal(111.777698, Parse(Wallet().Replace("111777698", "\"111777698\""))!.Day!.Amount, 6);
        Assert.Null(Parse(Wallet().Replace("111777698", "\"NaN\"")));
        Assert.Null(Parse(Wallet(day: -1)));
    }

    [Fact]
    public void Fresh_http_response_does_not_hide_stale_pool_data()
    {
        var income = Parse(Wallet())!;
        Assert.True(income.IsStale(Now.AddMinutes(11)));
        Assert.Null(income.ForecastBasis(Now.AddMinutes(11)));
        Assert.Equal(111.777698, income.Day!.Amount, 6);
    }

    [Fact]
    public void Mislabeled_windows_and_future_timestamps_are_rejected()
    {
        var wallet = Wallet().Replace(Now.ToUnixTimeMilliseconds().ToString(), Now.AddHours(1).ToUnixTimeMilliseconds().ToString());
        Assert.Null(Parse(wallet));
        Assert.Null(Parse(JsonSerializer.Serialize(new { rewardStats = new[] { new
            { period = "24h", amount = 42, startTime = Now.AddHours(-1).ToUnixTimeMilliseconds(), endTime = Now.ToUnixTimeMilliseconds() } } })));
    }

    [Fact]
    public void Cards_and_regions_sharing_a_wallet_have_one_account()
    {
        var a = XtmAccount.From("taric29.luckypool.io:3111", "wallet/3060");
        var b = XtmAccount.From("stratum+tcp://taric29-ca.luckypool.io:3111", "wallet.4060");
        Assert.NotNull(a);
        Assert.Equal(a, b);
        Assert.Single(new[] { a, b }.Distinct());
        Assert.NotEqual(a, XtmAccount.From("taric29.luckypool.io:3111", "other/3060"));
        Assert.Null(XtmAccount.From("turx.luckypool.io:10118", "wallet"));
        Assert.Null(XtmAccount.From("taric29.luckypool.io.attacker.test:3111", "wallet"));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = XtmIncomeTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool Fail { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/stats") ? Pool : Wallet()) });
        }
    }

    [Fact]
    public async Task Shared_wallet_is_fetched_once_and_failures_preserve_age_and_back_off()
    {
        var handler = new Handler();
        var clock = new Clock();
        using var service = new XtmIncomeService(handler, clock);
        var a = XtmAccount.From("taric29.luckypool.io:3111", "wallet/3060")!;
        var b = XtmAccount.From("taric29-sg.luckypool.io:3111", "wallet/4060")!;
        var original = await service.GetAsync(a, default);
        Assert.Same(original, await service.GetAsync(b, default));
        Assert.Equal(2, handler.Requests);
        handler.Fail = true;
        clock.Now = Now.AddMinutes(11);
        var stale = await service.GetAsync(a, default);
        Assert.Same(original, stale);
        Assert.True(stale!.IsStale(clock.Now));
        Assert.Null(stale.ForecastBasis(clock.Now));
        await service.GetAsync(a, default);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public void Panel_escapes_names_and_labels_stale_data_without_forecasting_it()
    {
        var income = Parse(Wallet())!;
        var account = XtmAccount.From("taric29.luckypool.io:3111", "wallet")!;
        var console = new TestConsole();
        console.Profile.Width = 200;
        console.Write(XtmIncomePanel.Render([new("rig[windows]", account, income)], Now.AddMinutes(11)));
        Assert.Contains("rig[windows]", console.Output);
        Assert.Contains("STALE", console.Output);
        Assert.Contains("111.78", console.Output);
        Assert.DoesNotContain("163.50", console.Output);
    }
}
