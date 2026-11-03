using System.Net;
using System.Text;
using MiningFleet.Console;
using MiningFleet.Console.Ui;
using Spectre.Console.Testing;

namespace MiningFleet.Console.Tests;

public sealed class DashboardStartupTests
{
    [Fact]
    public void Initial_states_render_immediately_as_polling_with_zero_watts_and_safe_markup()
    {
        var config = new FleetConfig
        {
            Nodes =
            [
                new NodeConfig { Name = "rig[alpha]", Host = "100.64.0.1", PowerFallbackWatts = 140 },
                new NodeConfig { Name = "offline-box", Host = "100.64.0.2", PowerFallbackWatts = 95 },
            ],
        };
        var fleet = new FleetService(config);
        using var market = new MarketService(config);
        using var dashboard = new Dashboard(config, fleet, market);

        var states = dashboard.BuildInitialStates(fleet.EnabledNodes);

        Assert.Equal(2, states.Length);
        Assert.All(states, s =>
        {
            Assert.True(s.Pending);
            Assert.False(s.Online);
            Assert.Equal(0, s.PowerWatts);
            Assert.Equal("[grey]polling...[/]", UiHelpers.StatusBadge(s));
        });

        var econ = Economics.Calculate(states, config, null, null);
        using var console = new TestConsole();
        console.Profile.Width = 160;
        console.Write(Dashboard.RenderView(
            states, econ, MoneyFormat.Single("RUB"), null, null, [], [], DateTimeOffset.UtcNow));

        Assert.Contains("rig[alpha]", console.Output);
        Assert.Contains("polling...", console.Output);
        Assert.Contains("(2 polling)", console.Output);
    }

    [Fact]
    public void Sparkline_history_ignores_pending_placeholder_states()
    {
        var history = new SparklineHistory(capacity: 5);
        var node = new NodeConfig { Name = "mks68i7rtx", Host = "100.64.0.5" };
        var pending = new NodeState(node, null, null, DateTimeOffset.UtcNow, Pending: true);

        history.RecordOne(pending);
        history.RecordOne(pending);

        Assert.Equal("", history.CpuMarkup("mks68i7rtx"));
        Assert.Equal("", history.GpuMarkup("mks68i7rtx"));
    }

    [Fact]
    public async Task GpuPoolService_resolves_multiple_coins_and_wallet_endpoints_in_parallel()
    {
        var handler = new ConcurrentStubHandler(url => url switch
        {
            "https://pool.kryptex.com/prl/api/v1/pool/stats" =>
                """{"ticker":"PRL","fee":0.01,"estimated_profit_day":1e-12}""",
            "https://pool.kryptex.com/xtm-c29/api/v1/pool/stats" =>
                """{"ticker":"XTM","fee":0.01,"net_hashrate":1000,"block_time":120,"block_reward":500}""",
            "https://api.coingecko.com/api/v3/simple/price?ids=pearl-2&vs_currencies=rub" =>
                """{"pearl-2":{"rub":135.5}}""",
            "https://api.coingecko.com/api/v3/simple/price?ids=minotari&vs_currencies=rub" =>
                """{"minotari":{"rub":1.2}}""",
            "https://pool.kryptex.com/prl/api/v1/miner/balance/addr1" =>
                """{"confirmed":0.25,"unconfirmed":0.5,"threshold":1.0}""",
            "https://pool.kryptex.com/prl/api/v1/miner/payouts/addr1" =>
                """{"results":[]}""",
            "https://pool.kryptex.com/prl/api/v1/miner/payouts/addr1/stats" =>
                """{"paid":2.0}""",
            _ => null,
        });

        using var service = new GpuPoolService(handler);
        var quotes = await service.ResolveCoinQuotesAsync(["prl", "xtm-c29", "PRL"], "RUB", CancellationToken.None);

        Assert.Equal(2, quotes.Count);
        Assert.Equal(135.5, quotes["prl"].Price);
        Assert.Equal(1.2, quotes["xtm-c29"].Price);

        var stats = await service.GetAsync(new GpuPoolTarget("kryptex", "prl", "addr1"), CancellationToken.None);
        Assert.NotNull(stats);
        Assert.Equal(0.75, stats!.Pending);
        Assert.Equal(2.0, stats.Paid);
    }

    private sealed class ConcurrentStubHandler(Func<string, string?> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            var body = responder(request.RequestUri!.AbsoluteUri);
            if (body is null)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
