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
    double TotalGpuHashrate);

public static class Economics
{
    public static FleetEconomics Calculate(
        IEnumerable<NodeState> nodes,
        FleetConfig config,
        PoolNetworkStats? network,
        double? price)
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

        return new FleetEconomics(
            hashrate,
            watts,
            costPerDay,
            xmrPerDay,
            revenuePerDay,
            profitPerDay,
            costPerXmr,
            config.Electricity.Currency,
            gpuHashrate);
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

    /// <summary>
    /// One card's rate with the unit its miner reported. SRBMiner sends raw hashes per second, which
    /// for pearlhash is tens of trillions, so a plain "59454100883390.32 H/s" is scaled; any other
    /// unit (lolMiner's own "g/s") is shown exactly as the miner named it.
    /// </summary>
    public static string FormatGpuRate(double? rate, string? unit)
    {
        if (rate is not { } value || value <= 0) return "-";
        if (string.Equals(unit, "H/s", StringComparison.OrdinalIgnoreCase)) return FormatRawHashes(value);
        return string.IsNullOrWhiteSpace(unit) ? $"{value:N2}" : $"{value:N2} {unit}";
    }

    private static string FormatRawHashes(double hashes) => hashes switch
    {
        >= 1e15 => $"{hashes / 1e15:0.00} PH/s",
        >= 1e12 => $"{hashes / 1e12:0.00} TH/s",
        >= 1e9 => $"{hashes / 1e9:0.00} GH/s",
        >= 1e6 => $"{hashes / 1e6:0.00} MH/s",
        >= 1e3 => $"{hashes / 1e3:0.00} kH/s",
        _ => $"{hashes:0} H/s",
    };

    /// <summary>
    /// The fleet's GPU rate, summed per unit and never across units: 59 TH/s of pearlhash and 4.5 g/s
    /// of Cuckaroo29 are different quantities, and one number for both would mean nothing.
    /// </summary>
    public static string FormatGpuFleetRate(IEnumerable<NodeState> nodes)
    {
        var parts = nodes
            .Where(n => n.GpuMining && n.GpuHashrate > 0)
            .GroupBy(n => n.Gpu?.HashrateUnit ?? "")
            .Select(g => FormatGpuRate(g.Sum(n => n.GpuHashrate), g.Key))
            .ToList();
        return parts.Count == 0 ? "-" : string.Join(" + ", parts);
    }

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
