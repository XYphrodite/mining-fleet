using MiningFleet.Console.Ui;
using MiningFleet.Contracts;
using Spectre.Console;
using Spectre.Console.Testing;

namespace MiningFleet.Console.Tests;

public sealed class FleetChartsTests
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

    private static NodeState Node(string name, bool cpuRunning, double cpuHr, bool gpuRunning, double gpuHr, double watts) =>
        new(
            new NodeConfig { Name = name, Host = "100.64.0.1", Enabled = true },
            new NodeSnapshotDto(
                new AgentInfoDto(name, "Windows", "1.0", ApiVersion.Current, 10, true),
                new MinerStatusDto { Installed = true, Running = cpuRunning, Hashrate60s = cpuHr },
                new HardwareDto { EstimatedPowerWatts = watts, PowerIsMeasured = true })
            {
                GpuMiner = new GpuMinerStatusDto
                {
                    Running = gpuRunning,
                    Hashrate = gpuRunning ? gpuHr : 0,
                    HashrateUnit = "H/s",
                    Pool = "prl.kryptex.network:7048",
                    Algorithm = "pearlhash",
                },
            },
            Error: null,
            PolledAt: DateTimeOffset.UtcNow);

    [Fact]
    public void Sparkline_scales_against_peak_so_steady_rate_stays_level_and_pauses_show_as_dots()
    {
        var console = new TestConsole();
        var markup = FleetCharts.SparklineMarkup([64.0, 64.2, 0.0, 0.0, 32.0, 64.1], "dim green");
        console.Write(new Markup(markup));

        // Two full bars, two pause dots, one half bar, one full bar.
        Assert.Equal("██··▄█", console.Output);
    }

    [Fact]
    public void Sparkline_stays_blank_before_two_samples_or_when_always_zero()
    {
        Assert.Equal("", FleetCharts.SparklineMarkup([50.0]));
        Assert.Equal("", FleetCharts.SparklineMarkup([0.0, 0.0, 0.0]));
    }

    [Fact]
    public void Dashboard_renders_live_cpu_and_gpu_sparklines_inside_existing_cells()
    {
        var config = new FleetConfig();
        var rig = Node("rig[4060]", true, 5690, true, 64e12, 240);
        var paused = Node("rig[4060]", true, 2800, false, 0, 120);

        var history = new SparklineHistory(capacity: 6);
        history.Record([rig]);
        history.Record([paused]);
        history.Record([rig]);

        var quotes = new Dictionary<string, NodeGpuQuote>
        {
            ["rig[4060]"] = new("PRL", 1.300964e-14, 116.36),
        };
        var econ = Economics.Calculate([rig], config, XmrNetwork, 48_016.0, quotes);

        var console = new TestConsole();
        console.Profile.Width = 280;
        console.Write(Dashboard.RenderView([rig], econ, MoneyFormat.Single("RUB"), XmrNetwork, 48_016.0, [], [], DateTimeOffset.UtcNow, history));

        Assert.Contains("█▄█", console.Output);
        Assert.Contains("█·█", console.Output);
        Assert.Contains("rig[4060]", console.Output);
    }

    [Fact]
    public void Economics_charts_render_cpu_gpu_mix_and_per_node_bars_with_hostile_names()
    {
        var config = new FleetConfig();
        config.Electricity.PricePerKwh = 5.0;
        config.Electricity.Currency = "RUB";

        var rig4060 = Node("mks[4060]", true, 5691.0, true, 64.31e12, 240.5);
        var xeonLoss = Node("xeon[cpu]", true, 5591.9, false, 0, 158.6);
        var quotes = new Dictionary<string, NodeGpuQuote>
        {
            ["mks[4060]"] = new("PRL", 1.300964e-14, 116.36),
        };

        var econ = Economics.Calculate([rig4060, xeonLoss], config, XmrNetwork, 48_016.0, quotes);
        var charts = FleetCharts.BuildEconomicsCharts(econ);
        Assert.NotNull(charts);

        var console = new TestConsole();
        console.Profile.Width = 140;
        console.Write(charts!);

        Assert.Contains("Income mix (CPU vs GPU)", console.Output);
        Assert.Contains("Income per node / day (RUB)", console.Output);
        Assert.Contains("mks[4060]", console.Output);
        Assert.Contains("xeon[cpu]", console.Output);
    }
}
