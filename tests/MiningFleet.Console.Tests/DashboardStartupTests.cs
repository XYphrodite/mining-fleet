using System.Net;
using System.Text;
using MiningFleet.Console;
using MiningFleet.Console.Ui;
using Spectre.Console;
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
    public void Live_display_fits_six_tailscale_nodes_totals_and_wallets_in_26_row_terminal()
    {
        var now = DateTimeOffset.UtcNow;
        NodeState ActiveNode(NodeConfig node, double cpuH, double gpuH, string unit, double watts) =>
            new(node, new MiningFleet.Contracts.NodeSnapshotDto(
                new MiningFleet.Contracts.AgentInfoDto(node.Name, "Windows", "1.0", MiningFleet.Contracts.ApiVersion.Current, 100, true),
                new MiningFleet.Contracts.MinerStatusDto
                {
                    Installed = true, Running = cpuH > 0, Hashrate60s = cpuH, MiningThreads = cpuH > 0 ? 14 : 0,
                    MsrMod = "intel", SharesGood = 1241, SharesTotal = 1243, UptimeSeconds = 35940,
                },
                new MiningFleet.Contracts.HardwareDto { CpuLoadPercent = 59, CpuTemperatureC = 65, EstimatedPowerWatts = watts, PowerIsMeasured = true })
            {
                GpuMiner = gpuH > 0 ? new MiningFleet.Contracts.GpuMinerStatusDto
                {
                    Running = true, Hashrate = gpuH, HashrateUnit = unit, AcceptedShares = 587, StaleShares = 5,
                } : null,
            }, null, now);

        var nodes = new List<NodeConfig>
        {
            new() { Name = "desktop-ib88isg", Host = "desktop-ib88isg.tail08a9a5.ts.net", PowerFallbackWatts = 187 },
            new() { Name = "mks68i7rtx", Host = "mks68i7rtx.tail08a9a5.ts.net", PowerFallbackWatts = 206 },
            new() { Name = "moscow_enjoyer", Host = "moscow-enjoyer.tail08a9a5.ts.net", PowerFallbackWatts = 100 },
            new() { Name = "R5-1600", Host = "r5-1600.tail08a9a5.ts.net", PowerFallbackWatts = 90 },
            new() { Name = "re-7lqd67ahcm0r", Host = "re-7lqd67ahcm0r.tail08a9a5.ts.net", PowerFallbackWatts = 219 },
            new() { Name = "xy-mobdesk-r7", Host = "xy-mobdesk-r7.tail08a9a5.ts.net", PowerFallbackWatts = 38 },
        };
        var config = new FleetConfig { Nodes = nodes };
        var states = new[]
        {
            ActiveNode(nodes[0], 5790, 63.47, "MH/s", 187),
            ActiveNode(nodes[1], 4880, 49.98, "TH/s", 206),
            new NodeState(nodes[2], null, "timeout", now),
            new NodeState(nodes[3], null, "timeout", now),
            ActiveNode(nodes[4], 0, 50.98, "TH/s", 219),
            new NodeState(nodes[5], new MiningFleet.Contracts.NodeSnapshotDto(
                new MiningFleet.Contracts.AgentInfoDto(nodes[5].Name, "Windows", "1.0", MiningFleet.Contracts.ApiVersion.Current, 100, true),
                new MiningFleet.Contracts.MinerStatusDto { Installed = true, Running = false },
                new MiningFleet.Contracts.HardwareDto { CpuLoadPercent = 4, CpuTemperatureC = 49, EstimatedPowerWatts = 38, PowerIsMeasured = true }), null, now),
        };
        var sparklines = new SparklineHistory(capacity: 8);
        for (var tick = 0; tick < 8; tick++)
            foreach (var s in states) sparklines.RecordOne(s);
        var quotes = new Dictionary<string, NodeGpuQuote>(StringComparer.OrdinalIgnoreCase)
        {
            ["desktop-ib88isg"] = new("QTC", 1e-10, 10.0),
            ["mks68i7rtx"] = new("PRL", 1.3e-14, 135.0),
            ["re-7lqd67ahcm0r"] = new("PRL", 1.3e-14, 135.0),
        };
        var network = new PoolNetworkStats(null, null, 6.43e9, 7.7e11, null, 0.6, 120, 41_553.0, "RUB");
        var econ = Economics.Calculate(states, config, network, 41_553.0, quotes);
        var lifetimeNodes = new[]
        {
            new NodeLifetimeSummary("desktop-ib88isg", true, false, true, now.AddDays(-7), now, 36000, 36000, 36000, 1e8, 0.01, 200, [], 579, 779, 779, false, false, 100, 1024, -245),
            new NodeLifetimeSummary("mks68i7rtx", true, false, true, now.AddDays(-7), now, 36000, 36000, 36000, 1e8, 0.01, 200, [], 4496, 4696, 4696, false, false, 150, 738, 3958),
            new NodeLifetimeSummary("moscow_enjoyer", false, false, true, now.AddDays(-7), now, 36000, 36000, 0, 1e8, 0.01, 472, [], 0, 472, 472, false, false, 30, 143, 329),
            new NodeLifetimeSummary("R5-1600", false, false, false, null, null, 0, 0, 0, 0, 0, null, [], null, null, null, false, false, 0, 0, null),
            new NodeLifetimeSummary("re-7lqd67ahcm0r", true, false, true, now.AddDays(-7), now, 36000, 0, 36000, 0, 0, 0, [], 3022, 3022, 3022, false, false, 1, 1, 3021),
            new NodeLifetimeSummary("xy-mobdesk-r7", true, false, true, now.AddDays(-7), now, 100, 100, 0, 1e4, 0, 4.58, [], 0, 4.58, 4.58, false, false, 2, 8.67, -4.09),
        };
        var lifetime = new FleetLifetimeSummary(lifetimeNodes, "RUB", now.AddDays(-7), 0.03, [], 876, 8097, 8973, 8973, false, 430.2, 1908.45, 7064.55);
        var prlTarget = new GpuPoolTarget("kryptex", "prl", "addr1");
        var qtcTarget = new GpuPoolTarget("kryptex", "qtc", "addr2");
        var prlIncome = new GpuIncome(
            new GpuPoolStats(prlTarget, Confirmed: 12.45, Unconfirmed: 0.35, Paid: 48.2, Threshold: 50.0, Payouts: [], FetchedAt: now.AddMinutes(-2)),
            [
                new GpuIncomeSample(now.AddHours(-25), 47.00),
                new GpuIncomeSample(now.AddHours(-16), 52.10),
                new GpuIncomeSample(now.AddHours(-8), 56.55),
                new GpuIncomeSample(now.AddMinutes(-2), 61.00),
            ]);
        var qtcIncome = new GpuIncome(
            new GpuPoolStats(qtcTarget, Confirmed: 4.12, Unconfirmed: 0.18, Paid: 20.0, Threshold: 10.0, Payouts: [], FetchedAt: now.AddMinutes(-2)),
            [
                new GpuIncomeSample(now.AddHours(-25), 19.60),
                new GpuIncomeSample(now.AddHours(-16), 21.20),
                new GpuIncomeSample(now.AddHours(-8), 22.80),
                new GpuIncomeSample(now.AddMinutes(-2), 24.30),
            ]);
        var gpuRows = new[]
        {
            new GpuIncomeRow("re-7lqd67ahcm0r, mks68i7rtx", prlTarget, prlIncome, 135.0),
            new GpuIncomeRow("desktop-ib88isg", qtcTarget, qtcIncome, 10.0),
        };
        var frame = Dashboard.RenderView(states, econ, MoneyFormat.Single("RUB"), network, 41_553.0, [], gpuRows, now, sparklines, lifetime, viewportHeight: 26);

        using var console = new TestConsole();
        console.Profile.Width = 165;
        console.Profile.Height = 26;
        var live = Dashboard.ConfigureLiveDisplay(console.Live(frame));
        live.Start(_ => { });

        Assert.Equal(Spectre.Console.VerticalOverflow.Ellipsis, live.Overflow);
        Assert.Equal(Spectre.Console.VerticalOverflowCropping.Bottom, live.Cropping);
        Assert.True(live.AutoClear);
        var output = console.Output;
        Assert.True(output.Contains(Economics.FormatHashrate(5790)), $"CPU hashrate missing:\n{output}");
        Assert.True(output.Contains(Markup.Remove(UiHelpers.GpuBadge(states[0].Gpu))), $"GPU hashrate missing:\n{output}");
        Assert.True(output.Contains("1241/1243"), $"1241/1243 missing:\n{output}");
        Assert.True(output.Contains("desktop-ib88isg"), $"desktop-ib88isg missing:\n{output}");
        Assert.True(output.Contains("xy-mobdesk-r7"), $"xy-mobdesk-r7 missing:\n{output}");
        Assert.True(output.Contains("Totals"), $"Totals missing:\n{output}");
        Assert.True(output.Contains("All-time net"), $"All-time net missing:\n{output}");
        Assert.True(output.Contains("PRL") && output.Contains("re-7lqd67ahcm0r, mks68i7rtx"), $"PRL row missing:\n{output}");
        Assert.True(output.Contains("QTC") && output.Contains("24h mean"), $"QTC row missing:\n{output}");
        Assert.True(output.Contains("press any key to return"), $"footer missing:\n{output}");
        Assert.DoesNotContain("tail08a9a5.ts.net", output);
        Assert.True(!output.Contains('…') && !output.Contains("..."), $"overflow ellipsis present:\n{output}");
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
