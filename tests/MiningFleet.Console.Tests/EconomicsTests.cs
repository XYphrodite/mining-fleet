using MiningFleet.Console;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Tests;

/// <summary>
/// The money maths. These numbers are what an operator decides on, so a silent arithmetic
/// change is worse than a crash: nothing looks wrong.
/// </summary>
public sealed class EconomicsTests
{
    private static NodeState Mining(string name, double hashrate, double watts, double? tariff = null) =>
        new(
            new NodeConfig { Name = name, Host = "h", Enabled = true, PowerFallbackWatts = watts, PricePerKwh = tariff },
            new NodeSnapshotDto(
                new AgentInfoDto(name, "os", "1.0", ApiVersion.Current, 1, true),
                new MinerStatusDto { Running = true, Installed = true, Hashrate60s = hashrate },
                new HardwareDto { PowerIsMeasured = false }),
            Error: null,
            PolledAt: DateTimeOffset.Now);

    private static FleetConfig Fleet(double defaultTariff = 5.0)
    {
        var config = new FleetConfig();
        config.Electricity.PricePerKwh = defaultTariff;
        config.Electricity.Currency = "RUB";
        return config;
    }

    [Fact]
    public void Electricity_is_summed_per_node_at_each_own_tariff()
    {
        var config = Fleet(defaultTariff: 5.0);
        var nodes = new[]
        {
            Mining("default-rate", hashrate: 0, watts: 200),
            Mining("own-rate", hashrate: 0, watts: 200, tariff: 12.0),
        };
        foreach (var n in nodes) config.Nodes.Add(n.Node);

        var economics = Economics.Calculate(nodes, config, network: null, price: null);

        // 200 W for 24 h is 4.8 kWh: 4.8*5.00 + 4.8*12.00. A single fleet rate would give 48.00.
        Assert.Equal(81.60, economics.CostPerDay, precision: 2);
        Assert.Equal(400, economics.TotalWatts, precision: 2);
    }

    [Fact]
    public void A_node_without_its_own_tariff_uses_the_fleet_default()
    {
        var config = Fleet(defaultTariff: 7.5);
        var node = Mining("rig", hashrate: 0, watts: 1000);
        config.Nodes.Add(node.Node);

        Assert.Equal(7.5, config.PricePerKwhFor(node.Node));
        Assert.Equal(180.0, Economics.DailyCost(node, config), precision: 2);
    }

    [Fact]
    public void Income_follows_hashrate_times_reward_over_difficulty()
    {
        var config = Fleet();
        var node = Mining("rig", hashrate: 10_000, watts: 0);
        config.Nodes.Add(node.Node);

        const double difficulty = 679_692_267_443;
        const double reward = 0.607664596;
        var network = new PoolNetworkStats(
            PoolHashrate: null, PoolMiners: null,
            NetworkHashrate: difficulty / 120,
            NetworkDifficulty: difficulty,
            NetworkHeight: null,
            BlockRewardXmr: reward,
            BlockTimeSeconds: 120,
            Price: null, PriceCurrency: null);

        var economics = Economics.Calculate([node], config, network, price: null);

        var expected = 10_000 * 86_400 * reward / difficulty;
        Assert.Equal(expected, economics.XmrPerDay!.Value, precision: 9);
    }

    [Fact]
    public void Income_is_blank_without_pool_data()
    {
        var config = Fleet();
        var node = Mining("rig", hashrate: 10_000, watts: 0);
        config.Nodes.Add(node.Node);

        var economics = Economics.Calculate([node], config, network: null, price: null);

        Assert.Null(economics.XmrPerDay);
        Assert.Null(economics.ProfitPerDay);
    }

    [Fact]
    public void Idle_nodes_are_not_charged_for_electricity()
    {
        var config = Fleet();
        var idle = new NodeState(
            new NodeConfig { Name = "idle", Host = "h", Enabled = true, PowerFallbackWatts = 500 },
            new NodeSnapshotDto(
                new AgentInfoDto("idle", "os", "1.0", ApiVersion.Current, 1, true),
                new MinerStatusDto { Running = false, Installed = true },
                new HardwareDto()),
            Error: null,
            PolledAt: DateTimeOffset.Now);
        config.Nodes.Add(idle.Node);

        var economics = Economics.Calculate([idle], config, network: null, price: null);

        Assert.Equal(0, economics.CostPerDay, precision: 6);
    }

    [Fact]
    public void A_configured_fallback_beats_an_unmeasured_agent_estimate()
    {
        var node = new NodeState(
            new NodeConfig { Name = "rig", Host = "h", PowerFallbackWatts = 220 },
            new NodeSnapshotDto(
                new AgentInfoDto("rig", "os", "1.0", ApiVersion.Current, 1, true),
                new MinerStatusDto { Running = true },
                new HardwareDto { EstimatedPowerWatts = 52, PowerIsMeasured = false }),
            Error: null,
            PolledAt: DateTimeOffset.Now);

        Assert.Equal(220, node.PowerWatts);
    }

    [Fact]
    public void A_measured_reading_beats_the_configured_fallback()
    {
        var node = new NodeState(
            new NodeConfig { Name = "rig", Host = "h", PowerFallbackWatts = 220 },
            new NodeSnapshotDto(
                new AgentInfoDto("rig", "os", "1.0", ApiVersion.Current, 1, true),
                new MinerStatusDto { Running = true },
                new HardwareDto { EstimatedPowerWatts = 143, PowerIsMeasured = true }),
            Error: null,
            PolledAt: DateTimeOffset.Now);

        Assert.Equal(143, node.PowerWatts);
    }
}
