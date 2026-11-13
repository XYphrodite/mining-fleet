using System.Net;
using System.Text;
using System.Text.Json;
using MiningFleet.Console.Ui;
using MiningFleet.Contracts;
using Spectre.Console.Testing;

namespace MiningFleet.Console.Tests;

/// <summary>
/// Guards combined CPU + GPU per-machine income accounting and its rendering in Dashboard
/// and EconomicsScreen.
/// </summary>
public sealed class CombinedNodeIncomeTests
{
    private static readonly PoolNetworkStats XmrNetwork = new(
        PoolHashrate: null,
        PoolMiners: null,
        NetworkHashrate: 803_220_536_931.0 / 120.0,
        NetworkDifficulty: 803_220_536_931.0,
        NetworkHeight: null,
        BlockRewardXmr: 0.608344,
        BlockTimeSeconds: 120,
        Price: 48_016.0,
        PriceCurrency: "RUB");

    private static FleetConfig Fleet(double tariff = 5.0)
    {
        var cfg = new FleetConfig();
        cfg.Electricity.PricePerKwh = tariff;
        cfg.Electricity.Currency = "RUB";
        cfg.Electricity.SecondaryCurrency = "USD";
        return cfg;
    }

    private static NodeState Node(
        string name,
        bool cpuRunning,
        double cpuHashrate,
        bool gpuRunning,
        double gpuHashrate,
        double watts,
        string gpuPool = "prl.kryptex.network:7048",
        string gpuUnit = "H/s") =>
        new(
            new NodeConfig { Name = name, Host = "100.64.0.1", Enabled = true },
            new NodeSnapshotDto(
                new AgentInfoDto(name, "Windows", "1.0", ApiVersion.Current, 100, true),
                new MinerStatusDto { Installed = true, Running = cpuRunning, Hashrate60s = cpuHashrate },
                new HardwareDto { EstimatedPowerWatts = watts, PowerIsMeasured = true })
            {
                GpuMiner = new GpuMinerStatusDto
                {
                    Running = gpuRunning,
                    Hashrate = gpuRunning ? gpuHashrate : 0,
                    HashrateUnit = gpuUnit,
                    Pool = gpuPool,
                    Algorithm = "pearlhash",
                    AcceptedShares = 42,
                },
            },
            Error: null,
            PolledAt: DateTimeOffset.UtcNow);

    [Fact]
    public void Combined_node_income_sums_cpu_and_gpu_and_turns_false_cpu_loss_into_true_profit()
    {
        var config = Fleet(tariff: 5.0);
        var rig4060 = Node("mks68i7rtx", cpuRunning: true, cpuHashrate: 5691.0, gpuRunning: true, gpuHashrate: 64.31e12, watts: 240.5);
        var rig3060 = Node("re-7lqd67ahcm0r", cpuRunning: true, cpuHashrate: 1556.8, gpuRunning: true, gpuHashrate: 51.56e12, watts: 247.3);
        var xeonCpuOnly = Node("DESKTOP-IB88ISG", cpuRunning: true, cpuHashrate: 5591.9, gpuRunning: false, gpuHashrate: 0, watts: 158.6);
        var quotes = new Dictionary<string, NodeGpuQuote>
        {
            ["mks68i7rtx"] = new("PRL", 1.300964e-14, 116.36),
            ["re-7lqd67ahcm0r"] = new("PRL", 1.300964e-14, 116.36),
        };

        var econ = Economics.Calculate([rig4060, rig3060, xeonCpuOnly], config, XmrNetwork, price: 48_016.0, quotes);

        var mks = econ.ForNode("mks68i7rtx")!;
        Assert.InRange(mks.CpuRevenuePerDay!.Value, 17.8, 18.0);
        Assert.InRange(mks.GpuRevenuePerDay!.Value, 97.2, 97.5);
        Assert.Equal(mks.CpuRevenuePerDay.Value + mks.GpuRevenuePerDay.Value, mks.TotalRevenuePerDay!.Value, precision: 6);
        Assert.InRange(mks.ProfitPerDay!.Value, 86.0, 86.8);

        var xeon = econ.ForNode("DESKTOP-IB88ISG")!;
        Assert.Equal(0.0, xeon.GpuRevenuePerDay!.Value, precision: 6);
        Assert.Equal(xeon.CpuRevenuePerDay!.Value, xeon.TotalRevenuePerDay!.Value, precision: 6);

        Assert.False(econ.HasMissingRevenue);
        Assert.Equal(
            econ.NodeBreakdown.Sum(n => n.TotalRevenuePerDay!.Value),
            econ.TotalRevenuePerDay!.Value,
            precision: 6);
        Assert.Equal(econ.TotalRevenuePerDay.Value - econ.CostPerDay, econ.TotalProfitPerDay!.Value, precision: 6);
    }

    [Fact]
    public void Gpu_only_machine_pays_its_electricity_and_computes_profit_without_monero_pool()
    {
        var config = Fleet(tariff: 5.0);
        var gpuOnly = Node("gpu-rig", cpuRunning: false, cpuHashrate: 0, gpuRunning: true, gpuHashrate: 50e12, watts: 200);
        var quotes = new Dictionary<string, NodeGpuQuote>
        {
            ["gpu-rig"] = new("PRL", 1.3e-14, 120.0),
        };

        var econ = Economics.Calculate([gpuOnly], config, network: null, price: null, quotes);

        // 200 W * 24 h / 1000 * 5 RUB = 24 RUB/day; 50e12 * 1.3e-14 * 120 = 78 RUB/day.
        Assert.Equal(24.0, econ.CostPerDay, precision: 6);
        Assert.Equal(78.0, econ.GpuRevenuePerDay!.Value, precision: 6);
        Assert.Equal(78.0, econ.TotalRevenuePerDay!.Value, precision: 6);
        Assert.Equal(54.0, econ.TotalProfitPerDay!.Value, precision: 6);
    }

    [Fact]
    public void Missing_gpu_price_never_passes_cpu_only_income_off_as_total_profit()
    {
        var config = Fleet(tariff: 5.0);
        var dual = Node("dual-rig", cpuRunning: true, cpuHashrate: 5000, gpuRunning: true, gpuHashrate: 50e12, watts: 240);
        var quotes = new Dictionary<string, NodeGpuQuote>
        {
            ["dual-rig"] = new("PRL", 1.3e-14, Price: null),
        };

        var econ = Economics.Calculate([dual], config, XmrNetwork, price: 48_000.0, quotes);
        var node = econ.ForNode("dual-rig")!;

        Assert.NotNull(node.CpuRevenuePerDay);
        Assert.Null(node.GpuRevenuePerDay);
        Assert.Null(node.TotalRevenuePerDay);
        Assert.Null(node.ProfitPerDay);
        Assert.True(node.GpuRevenueMissing);
        Assert.Equal(node.CpuRevenuePerDay!.Value, node.KnownRevenuePerDay!.Value, precision: 6);
        Assert.True(econ.HasMissingRevenue);
        Assert.Null(econ.TotalRevenuePerDay);
        Assert.Null(econ.TotalProfitPerDay);
    }

    [Theory]
    [InlineData("prl.kryptex.network:7048", "prl")]
    [InlineData("prl-ru.kryptex.network:7048", "prl")]
    [InlineData("qtc.kryptex.network:7049", "qtc")]
    [InlineData("qtc-ru.kryptex.network:7049", "qtc")]
    [InlineData("stratum+tcp://xtm-c29-eu.kryptex.network:7040", "xtm-c29")]
    [InlineData("xtm-c29.kryptex.network:7040", "xtm-c29")]
    [InlineData("rx.unmineable.com:3333", null)]
    [InlineData("prl.kryptex.network.attacker.test:7048", null)]
    public void Kryptex_coin_slug_normalizes_regional_endpoints_and_rejects_lookalikes(string pool, string? expected)
    {
        Assert.Equal(expected, GpuPoolService.CoinForPool(pool));
    }

    [Fact]
    public async Task Quantus_pool_rate_and_kryptex_spot_price_fallback_resolve_cleanly()
    {
        using var qtcJson = JsonDocument.Parse(
            """{"ticker":"qtc","fee_type":"PROP","fee":0.03,"estimated_profit_day":null,"net_hashrate":37612150453683,"block_time":12,"block_reward":0.3}""");
        var qtc = GpuPoolService.ParsePoolRate("qtc", qtcJson.RootElement)!;
        Assert.Equal("QTC", qtc.Ticker);
        var expectedPerHashDay = 86400.0 / 12.0 * 0.3 / 37612150453683.0 * 0.97;
        Assert.Equal(expectedPerHashDay * 1e12, qtc.CoinsPerHashrateDay * 1e12, precision: 8);

        using var service = new GpuPoolService(new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("api.coingecko.com"))
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (url.Contains("pool.kryptex.com/api/v1/rates"))
                return Json("""{"crypto":{"QTC":70.0},"fiat":{"RUB":80.0,"USD":1.0}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var rubPrice = await service.GetCoinPriceAsync("qtc", "RUB", CancellationToken.None);
        Assert.Equal(5600.0, rubPrice);
    }

    [Fact]
    public void Pool_rate_reads_pps_estimated_profit_and_prop_block_formula()
    {
        using var prlJson = JsonDocument.Parse("""{"ticker":"prl","fee":0.03,"estimated_profit_day":1.3412e-14}""");
        var prl = GpuPoolService.ParsePoolRate("prl", prlJson.RootElement)!;
        Assert.Equal("PRL", prl.Ticker);
        Assert.Equal(1.3412e-14 * 0.97 * 1e14, prl.CoinsPerHashrateDay * 1e14, precision: 10);

        using var xtmJson = JsonDocument.Parse("""{"ticker":"xtm","fee":0.01,"estimated_profit_day":null,"net_hashrate":20000,"block_time":480,"block_reward":10000}""");
        var xtm = GpuPoolService.ParsePoolRate("xtm-c29", xtmJson.RootElement)!;
        Assert.Equal("XTM", xtm.Ticker);
        // (86400 / 480) * 10000 / 20000 * 0.99 = 180 * 0.5 * 0.99 = 89.1 XTM / (g/s) / day.
        Assert.Equal(89.1, xtm.CoinsPerHashrateDay, precision: 6);
    }

    [Fact]
    public async Task ResolveQuotes_queries_shared_gpu_coin_once_across_all_nodes()
    {
        var poolHits = 0;
        var priceHits = 0;
        using var service = new GpuPoolService(new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("pool.kryptex.com/prl/api/v1/pool/stats"))
            {
                poolHits++;
                return Json("""{"ticker":"prl","fee":0.03,"estimated_profit_day":1.3e-14}""");
            }
            if (url.Contains("api.coingecko.com"))
            {
                priceHits++;
                return Json("""{"pearl-2":{"rub":120.0}}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var cfg = Fleet();
        var states = new[]
        {
            Node("rig-a", true, 5000, true, 50e12, 240, "prl.kryptex.network:7048"),
            Node("rig-b", true, 6000, true, 64e12, 240, "prl-ru.kryptex.network:7048"),
            Node("cpu-only", true, 5500, false, 0, 150),
        };

        var quotes = await service.ResolveQuotesAsync(states, cfg, fleet: null, CancellationToken.None);

        Assert.Equal(1, poolHits);
        Assert.Equal(1, priceHits);
        Assert.Equal(2, quotes.Count);
        Assert.Equal(120.0, quotes["rig-a"].Price);
        Assert.Equal(120.0, quotes["rig-b"].Price);
    }

    [Fact]
    public void Dashboard_and_economics_tables_render_combined_machine_income_and_escape_hostile_names()
    {
        var config = Fleet(tariff: 5.0);
        var rigA = Node("rig[4060]", true, 5691.0, true, 64.31e12, 240.5);
        var rigB = Node("xeon-cpu", true, 5591.9, false, 0, 158.6);
        var quotes = new Dictionary<string, NodeGpuQuote>
        {
            ["rig[4060]"] = new("PRL", 1.300964e-14, 116.36),
        };

        var econ = Economics.Calculate([rigA, rigB], config, XmrNetwork, 48_016.0, quotes);
        var money = new MoneyFormat("RUB", "USD", 1.0 / 80.3);
        var rigATotal = money.CompactMarkup(econ.ForNode("rig[4060]")!.TotalRevenuePerDay);
        var rigBTotal = money.CompactMarkup(econ.ForNode("xeon-cpu")!.TotalRevenuePerDay);

        var dashConsole = new TestConsole();
        dashConsole.Profile.Width = 280;
        dashConsole.Write(Dashboard.RenderView([rigA, rigB], econ, money, XmrNetwork, 48_016.0, [], [], DateTimeOffset.UtcNow));

        Assert.Contains("Income/d", dashConsole.Output);
        Assert.Contains(rigATotal, dashConsole.Output);
        Assert.Contains("Total income/day", dashConsole.Output);
        Assert.Contains("Net profit/day", dashConsole.Output);
        Assert.Contains("rig[4060]", dashConsole.Output);

        var econConsole = new TestConsole();
        econConsole.Profile.Width = 280;
        econConsole.Write(EconomicsScreen.BuildPeriodTable(econ, money));
        econConsole.Write(EconomicsScreen.BuildNodeBreakdownTable(econ, config, money));

        Assert.Contains("Total/day", econConsole.Output);
        Assert.Contains("Fleet total", econConsole.Output);
        Assert.Contains(rigATotal, econConsole.Output);
        Assert.Contains(rigBTotal, econConsole.Output);
    }

    [Fact]
    public async Task Pool_rate_falls_back_to_coin_info_and_net_stats_when_pool_stats_returns_500()
    {
        using var service = new GpuPoolService(new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("pool.kryptex.com/prl/api/v1/pool/stats"))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            if (url.Contains("pool.kryptex.com/api/v1/coin/prl/info"))
                return Json("""{"algo":"PearlHash","block_time":184.0,"block_reward":2172.3978357111}""");
            if (url.Contains("pool.kryptex.com/api/v1/net/stats/prl"))
                return Json("""{"day":[{"timestamp":1794558600000,"net_hashrate":9.0e19},{"timestamp":1794562200000,"net_hashrate":9.361963064108612e19}]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var rate = await service.GetPoolRateAsync("prl", CancellationToken.None);

        Assert.NotNull(rate);
        Assert.Equal("PRL", rate!.Ticker);
        var expected = 86400.0 / 184.0 * 2172.3978357111 / 9.361963064108612e19 * 0.97;
        Assert.Equal(expected * 1e14, rate.CoinsPerHashrateDay * 1e14, precision: 8);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
