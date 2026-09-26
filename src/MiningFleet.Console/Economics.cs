namespace MiningFleet.Console;

/// <summary>Money view of the fleet: what it earns, what the electricity costs, what is left.</summary>
public sealed record FleetEconomics(
    double TotalHashrate,
    double TotalWatts,
    double CostPerDay,
    double? XmrPerDay,
    double? RevenuePerDay,
    double? ProfitPerDay,
    double? CostPerXmr,
    string Currency,
    double TotalGpuHashrate,
    double? XtmPerDay,
    double? GpuRevenuePerDay);

public static class Economics
{
    private const double XtmPerGsPerDay = 87.5; // benchmark: 4 g/s ~350 XTM => 87.5 per g/s (from 49 RUB per 4g/s at 0.14 RUB)

    public static FleetEconomics Calculate(
        IEnumerable<NodeState> nodes,
        FleetConfig config,
        PoolNetworkStats? network,
        double? price,
        double? xtmPrice = null)
    {
        var list = nodes.ToList();
        var hashrate = list.Where(n => n.Mining).Sum(n => n.Hashrate);
        var watts = list.Where(n => n.Mining).Sum(n => n.PowerWatts);
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

        var gpuHashrate = list.Where(n => n.GpuMining).Sum(n => n.GpuHashrate);
        double? xtmPerDay = gpuHashrate > 0 ? gpuHashrate * XtmPerGsPerDay : null;
        var gpuRevenuePerDay = xtmPerDay is not null && xtmPrice is not null ? xtmPerDay * xtmPrice : null;

        return new FleetEconomics(
            hashrate,
            watts,
            costPerDay,
            xmrPerDay,
            revenuePerDay,
            profitPerDay,
            costPerXmr,
            config.Electricity.Currency,
            gpuHashrate,
            xtmPerDay,
            gpuRevenuePerDay);
    }

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

    public static string FormatGpuHashrate(double hash) => hash > 0 ? $"{hash:0.00} g/s" : "-";

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
