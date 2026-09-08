namespace MiningFleet.Console;

/// <summary>Money view of the fleet: what it earns, what the electricity costs, what is left.</summary>
public sealed record FleetEconomics(
    double TotalHashrate,
    double TotalWatts,
    double CostPerDay,
    double? XmrPerDay,
    double? RevenuePerDay,
    double? ProfitPerDay,
    /// <summary>Electricity spent per mined XMR, which is also the break-even XMR price.</summary>
    double? CostPerXmr,
    string Currency);

public static class Economics
{
    /// <summary>
    /// Income is estimated from network difficulty: your share of the network hashrate
    /// times the daily block reward. It is an expectation, not a measurement, so the
    /// real payout swings around it.
    /// </summary>
    public static FleetEconomics Calculate(
        IEnumerable<NodeState> nodes,
        FleetConfig config,
        PoolNetworkStats? network,
        double? price)
    {
        var list = nodes.ToList();
        var hashrate = list.Where(n => n.Mining).Sum(n => n.Hashrate);

        // Idle nodes still burn power, but only mining nodes are charged here: the machines
        // would be on anyway, so what mining costs is the draw while it runs.
        var watts = list.Where(n => n.Mining).Sum(n => n.PowerWatts);

        // Summed per node, not from the fleet total: each machine can sit on its own tariff,
        // so one average rate would misprice every rig that is not on it.
        var costPerDay = list.Where(n => n.Mining).Sum(n => DailyCost(n, config));

        double? xmrPerDay = null;
        if (network?.NetworkHashrate is > 0 && network.BlockRewardXmr is > 0 && hashrate > 0)
        {
            var blockTime = network.BlockTimeSeconds > 0 ? network.BlockTimeSeconds : MarketService.DefaultBlockTimeSeconds;
            var blocksPerDay = 86400.0 / blockTime;
            var share = hashrate / network.NetworkHashrate.Value;
            xmrPerDay = share * blocksPerDay * network.BlockRewardXmr.Value;
        }

        var revenuePerDay = xmrPerDay is not null && price is not null ? xmrPerDay * price : null;
        var profitPerDay = revenuePerDay is not null ? revenuePerDay - costPerDay : null;
        var costPerXmr = xmrPerDay is > 0 ? costPerDay / xmrPerDay : null;

        return new FleetEconomics(
            hashrate,
            watts,
            costPerDay,
            xmrPerDay,
            revenuePerDay,
            profitPerDay,
            costPerXmr,
            config.Electricity.Currency);
    }

    /// <summary>What one node costs to run for 24 hours at its own tariff.</summary>
    public static double DailyCost(NodeState node, FleetConfig config) =>
        node.PowerWatts / 1000.0 * 24.0 * config.PricePerKwhFor(node.Node);

    public static string FormatHashrate(double hashesPerSecond) => hashesPerSecond switch
    {
        >= 1_000_000_000 => $"{hashesPerSecond / 1_000_000_000:0.00} GH/s",
        >= 1_000_000 => $"{hashesPerSecond / 1_000_000:0.00} MH/s",
        >= 1_000 => $"{hashesPerSecond / 1_000:0.00} kH/s",
        > 0 => $"{hashesPerSecond:0} H/s",
        _ => "-",
    };

    public static string FormatDuration(double seconds)
    {
        if (seconds <= 0) return "-";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d {span.Hours}h"
            : span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m"
                : $"{span.Minutes}m {span.Seconds}s";
    }
}
