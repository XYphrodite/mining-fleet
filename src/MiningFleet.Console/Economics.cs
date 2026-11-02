namespace MiningFleet.Console;

/// <summary>
/// Live pool yield and spot price for the coin one node's GPU is currently mining.
/// Either <see cref="CoinsPerHashrateDay"/> (multiplied by <see cref="NodeState.GpuHashrate"/>)
/// or an already-resolved <see cref="CoinsPerDay"/> may supply the daily coin output.
/// </summary>
public sealed record NodeGpuQuote(
    string Coin,
    double? CoinsPerHashrateDay,
    double? Price,
    double? CoinsPerDay = null);

/// <summary>Combined daily income (CPU + GPU), electricity cost and net profit for one machine.</summary>
public sealed record NodeEconomics(
    NodeState State,
    double CostPerDay,
    double? CpuXmrPerDay,
    double? CpuRevenuePerDay,
    string? GpuCoin,
    double? GpuCoinsPerDay,
    double? GpuRevenuePerDay,
    double? TotalRevenuePerDay,
    double? ProfitPerDay,
    double? KnownRevenuePerDay,
    bool CpuRevenueMissing,
    bool GpuRevenueMissing)
{
    public bool HasMissingRevenue => CpuRevenueMissing || GpuRevenueMissing;
}

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
    double? GpuRevenuePerDay = null,
    double? TotalRevenuePerDay = null,
    double? TotalProfitPerDay = null,
    double? KnownRevenuePerDay = null,
    bool HasMissingRevenue = false,
    IReadOnlyList<NodeEconomics>? Nodes = null)
{
    public IReadOnlyList<NodeEconomics> NodeBreakdown => Nodes ?? [];

    public NodeEconomics? ForNode(string name) =>
        NodeBreakdown.FirstOrDefault(n => string.Equals(n.State.Node.Name, name, StringComparison.OrdinalIgnoreCase));
}

public static class Economics
{
    public static FleetEconomics Calculate(
        IEnumerable<NodeState> nodes,
        FleetConfig config,
        PoolNetworkStats? network,
        double? price,
        IReadOnlyDictionary<string, NodeGpuQuote>? gpuQuotes = null)
    {
        var list = nodes.ToList();
        var hashrate = list.Where(n => n.Mining).Sum(n => n.Hashrate);
        var watts = list.Where(n => n.AnyMining).Sum(n => n.PowerWatts);
        var costPerDay = list.Where(n => n.AnyMining).Sum(n => DailyCost(n, config));

        var xmrPerHashDay = XmrPerHashDay(network);

        double? xmrPerDay = xmrPerHashDay is not null && hashrate > 0 ? hashrate * xmrPerHashDay.Value : null;
        var revenuePerDay = xmrPerDay is not null && price is not null ? xmrPerDay * price : null;
        var profitPerDay = revenuePerDay is not null ? revenuePerDay - costPerDay : null;
        var costPerXmr = xmrPerDay is > 0 ? costPerDay / xmrPerDay : null;

        var gpuHashrate = list.Where(n => n.GpuMining).Sum(n => n.GpuHashrate);
        var nodeBreakdown = list
            .Select(n => CalculateNode(n, config, xmrPerHashDay, price, FindQuote(gpuQuotes, n.Node.Name)))
            .ToList();

        var active = nodeBreakdown.Where(n => n.State.AnyMining).ToList();
        var gpuActive = nodeBreakdown.Where(n => n.State.GpuMining).ToList();

        double? gpuRevenuePerDay = active.Count == 0
            ? null
            : gpuActive.Count == 0
                ? 0.0
                : gpuActive.All(n => n.GpuRevenuePerDay is not null)
                    ? gpuActive.Sum(n => n.GpuRevenuePerDay!.Value)
                    : null;

        double? totalRevenuePerDay = active.Count > 0 && active.All(n => n.TotalRevenuePerDay is not null)
            ? active.Sum(n => n.TotalRevenuePerDay!.Value)
            : null;

        double? totalProfitPerDay = totalRevenuePerDay is { } rev ? rev - costPerDay : null;

        double? knownRevenuePerDay = active.Any(n => n.KnownRevenuePerDay is not null)
            ? active.Sum(n => n.KnownRevenuePerDay ?? 0.0)
            : null;

        var hasMissing = active.Any(n => n.HasMissingRevenue);

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
            gpuRevenuePerDay,
            totalRevenuePerDay,
            totalProfitPerDay,
            knownRevenuePerDay,
            hasMissing,
            nodeBreakdown);
    }

    private static NodeGpuQuote? FindQuote(IReadOnlyDictionary<string, NodeGpuQuote>? quotes, string nodeName)
    {
        if (quotes is null) return null;
        if (quotes.TryGetValue(nodeName, out var exact)) return exact;
        foreach (var (key, value) in quotes)
        {
            if (string.Equals(key, nodeName, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    private static NodeEconomics CalculateNode(
        NodeState node,
        FleetConfig config,
        double? xmrPerHashDay,
        double? xmrPrice,
        NodeGpuQuote? gpuQuote)
    {
        var cost = node.AnyMining ? DailyCost(node, config) : 0.0;

        double? cpuXmr;
        double? cpuRevenue;
        bool cpuMissing;
        if (!node.Mining)
        {
            cpuXmr = 0.0;
            cpuRevenue = 0.0;
            cpuMissing = false;
        }
        else
        {
            cpuXmr = xmrPerHashDay is not null && node.Hashrate > 0 ? node.Hashrate * xmrPerHashDay.Value : null;
            cpuRevenue = cpuXmr is not null && xmrPrice is not null ? cpuXmr.Value * xmrPrice.Value : null;
            cpuMissing = cpuRevenue is null;
        }

        string? gpuCoin;
        double? gpuCoins;
        double? gpuRevenue;
        bool gpuMissing;
        if (!node.GpuMining)
        {
            gpuCoin = null;
            gpuCoins = 0.0;
            gpuRevenue = 0.0;
            gpuMissing = false;
        }
        else
        {
            gpuCoin = gpuQuote?.Coin;
            gpuCoins = gpuQuote?.CoinsPerDay is { } direct && direct >= 0 && double.IsFinite(direct)
                ? direct
                : gpuQuote?.CoinsPerHashrateDay is > 0 && node.GpuHashrate > 0
                    ? node.GpuHashrate * gpuQuote.CoinsPerHashrateDay.Value
                    : null;
            gpuRevenue = gpuCoins is not null && gpuQuote?.Price is { } gp && gp >= 0 && double.IsFinite(gp)
                ? gpuCoins.Value * gp
                : null;
            gpuMissing = gpuRevenue is null;
        }

        if (!node.AnyMining)
        {
            return new NodeEconomics(node, 0.0, 0.0, 0.0, null, 0.0, 0.0, 0.0, 0.0, 0.0, false, false);
        }

        // Never pass CPU-only income off as a dual-mining machine's whole day when its GPU
        // pool or price is missing: keep TotalRevenuePerDay null and surface KnownRevenuePerDay
        // alongside the missing flag so the screen can say "+ GPU?" plainly.
        double? totalRevenue = cpuRevenue is { } c && gpuRevenue is { } g ? c + g : null;
        double? profit = totalRevenue is { } total ? total - cost : null;
        var anyKnownActive = (node.Mining && cpuRevenue is not null) || (node.GpuMining && gpuRevenue is not null);
        double? knownRevenue = anyKnownActive ? (cpuRevenue ?? 0.0) + (gpuRevenue ?? 0.0) : null;

        return new NodeEconomics(
            node,
            cost,
            cpuXmr,
            cpuRevenue,
            gpuCoin,
            gpuCoins,
            gpuRevenue,
            totalRevenue,
            profit,
            knownRevenue,
            cpuMissing,
            gpuMissing);
    }

    public static double? XmrPerHashDay(PoolNetworkStats? network)
    {
        if (network?.NetworkHashrate is > 0 && network.BlockRewardXmr is > 0)
        {
            var blockTime = network.BlockTimeSeconds > 0 ? network.BlockTimeSeconds : MarketService.DefaultBlockTimeSeconds;
            var blocksPerDay = 86400.0 / blockTime;
            return blocksPerDay * network.BlockRewardXmr.Value / network.NetworkHashrate.Value;
        }
        return null;
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
