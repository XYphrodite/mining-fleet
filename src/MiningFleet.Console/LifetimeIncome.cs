using System.Collections.Concurrent;
using System.Text.Json;
using MiningFleet.Contracts;

namespace MiningFleet.Console;

/// <summary>Cumulative GPU coin balance mined by one machine (or across the fleet) since tracking began.</summary>
public sealed record NodeLifetimeCoin(
    string Slug,
    string Ticker,
    double Amount,
    double? Value);

/// <summary>All-time cumulative work, coins, electricity and net profit for a single machine.</summary>
public sealed record NodeLifetimeSummary(
    string NodeName,
    bool Online,
    bool NeedsAgentUpgrade,
    bool HasData,
    DateTimeOffset? TrackedSince,
    DateTimeOffset? UpdatedAt,
    double ActiveMiningSeconds,
    double CpuMiningSeconds,
    double GpuMiningSeconds,
    double TotalCpuHashes,
    double CpuXmr,
    double? CpuRevenue,
    IReadOnlyList<NodeLifetimeCoin> GpuCoins,
    double? GpuRevenue,
    double? TotalRevenue,
    double? KnownRevenue,
    bool CpuRevenueMissing,
    bool GpuRevenueMissing,
    double EnergyKwh,
    double ElectricityCost,
    double? NetProfit)
{
    public bool HasMissingRevenue => CpuRevenueMissing || GpuRevenueMissing;
}

/// <summary>Fleet-wide rollup of per-machine all-time cumulative income and electricity cost.</summary>
public sealed record FleetLifetimeSummary(
    IReadOnlyList<NodeLifetimeSummary> Nodes,
    string Currency,
    DateTimeOffset? EarliestTrackedSince,
    double TotalCpuXmr,
    IReadOnlyList<NodeLifetimeCoin> TotalGpuCoins,
    double? CpuRevenue,
    double? GpuRevenue,
    double? TotalRevenue,
    double? KnownRevenue,
    bool HasMissingRevenue,
    double TotalEnergyKwh,
    double TotalElectricityCost,
    double? TotalNetProfit)
{
    public bool AnyTracked => Nodes.Any(n => n.HasData);
    public int UpgradeNeededCount => Nodes.Count(n => n.NeedsAgentUpgrade);

    public NodeLifetimeSummary? ForNode(string name) =>
        Nodes.FirstOrDefault(n => string.Equals(n.NodeName, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Persists per-machine cumulative coins and energy beside <c>fleet.json</c> in
/// <c>lifetime-income.json</c>, reconciling monotonic 24/7 counters from each node's agent with
/// live pool yields so closing the console for weeks or reinstalling an OS never loses history.
/// Wallet addresses are never written to this file.
/// </summary>
public sealed class LifetimeIncomeLedger
{
    public const string FileName = "lifetime-income.json";

    private static readonly ConcurrentDictionary<string, LifetimeIncomeLedger> Shared = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, PersistedNodeRecord> _records;

    public LifetimeIncomeLedger(string? path = null)
    {
        _path = path;
        _records = Load(path);
    }

    public static LifetimeIncomeLedger For(FleetConfig config)
    {
        var directory = string.IsNullOrWhiteSpace(config.Path)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(config.Path));
        if (directory is null) return new LifetimeIncomeLedger();
        return Shared.GetOrAdd(directory, d => new LifetimeIncomeLedger(Path.Combine(d, FileName)));
    }

    /// <summary>
    /// Lists every Kryptex coin slug referenced by any online node's lifetime GPU buckets or by
    /// already-banked history so the caller can resolve pool rates and spot prices in one pass.
    /// </summary>
    public IReadOnlyList<string> KnownGpuCoinSlugs(IEnumerable<NodeState> states, FleetConfig config)
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var state in states)
        {
            var fallbackCoin = GpuPoolService.CoinForPool(config.GpuMinerFor(state.Node).PoolUrl);
            if (state.Snapshot?.Lifetime is { } lt)
            {
                foreach (var bucket in lt.GpuBuckets ?? [])
                {
                    var slug = GpuPoolService.CoinForPool(bucket.Pool) ?? fallbackCoin;
                    if (!string.IsNullOrWhiteSpace(slug)) slugs.Add(slug);
                }
            }
        }

        lock (_gate)
        {
            foreach (var rec in _records.Values)
            {
                foreach (var slug in rec.GpuCoins.Keys)
                    if (!string.IsNullOrWhiteSpace(slug)) slugs.Add(slug);
                foreach (var slug in rec.PendingGpuHashrateDays.Keys)
                    if (!string.IsNullOrWhiteSpace(slug)) slugs.Add(slug);
            }
        }

        return slugs.ToArray();
    }

    public FleetLifetimeSummary UpdateAndCalculate(
        IReadOnlyList<NodeState> states,
        FleetConfig config,
        PoolNetworkStats? network,
        double? xmrPrice,
        IReadOnlyDictionary<string, NodeGpuQuote>? coinQuotes = null,
        IReadOnlyDictionary<string, NodeGpuQuote>? nodeQuotes = null)
    {
        var xmrPerHashDay = Economics.XmrPerHashDay(network);

        lock (_gate)
        {
            var dirty = false;

            foreach (var state in states)
            {
                if (state.Snapshot?.Lifetime is not { } lt) continue;

                if (!_records.TryGetValue(state.Node.Name, out var rec))
                {
                    rec = new PersistedNodeRecord
                    {
                        NodeName = state.Node.Name,
                        TrackedSince = lt.StartedAt,
                        UpdatedAt = lt.UpdatedAt,
                    };
                    _records[state.Node.Name] = rec;
                    dirty = true;
                }

                rec.NodeName = state.Node.Name;
                if (lt.StartedAt != default && (rec.TrackedSince == default || lt.StartedAt < rec.TrackedSince))
                {
                    rec.TrackedSince = lt.StartedAt;
                    dirty = true;
                }

                // Detect a fresh lifetime.json on the node (e.g. OS reinstall) and reset only cursors,
                // keeping every coin and kWh already banked on the console.
                if (rec.AgentStartedAt is { } prevEpoch
                    && (lt.StartedAt != prevEpoch
                        || lt.TotalCpuHashes + 1e-6 < rec.LastCpuHashes
                        || lt.ActiveMiningSeconds + 1e-6 < rec.LastActiveMiningSeconds))
                {
                    rec.LastActiveMiningSeconds = 0;
                    rec.LastPoweredMiningSeconds = 0;
                    rec.LastCpuMiningSeconds = 0;
                    rec.LastGpuMiningSeconds = 0;
                    rec.LastCpuHashes = 0;
                    rec.LastEnergyKwh = 0;
                    rec.LastGpuBuckets.Clear();
                    dirty = true;
                }

                if (rec.AgentStartedAt != lt.StartedAt)
                {
                    rec.AgentStartedAt = lt.StartedAt;
                    dirty = true;
                }

                var dActive = PositiveDelta(lt.ActiveMiningSeconds, rec.LastActiveMiningSeconds);
                var dPowered = PositiveDelta(lt.PoweredMiningSeconds, rec.LastPoweredMiningSeconds);
                var dCpuSec = PositiveDelta(lt.CpuMiningSeconds, rec.LastCpuMiningSeconds);
                var dGpuSec = PositiveDelta(lt.GpuMiningSeconds, rec.LastGpuMiningSeconds);
                var dHashes = PositiveDelta(lt.TotalCpuHashes, rec.LastCpuHashes);
                var dKwh = PositiveDelta(lt.TotalEnergyKwh, rec.LastEnergyKwh);

                // If the node lacked power sensors and had no pushed powerFallbackWatts on the agent,
                // cover only the unpowered active seconds using the console's per-node fallback watts.
                var unpoweredSeconds = Math.Max(0.0, dActive - dPowered);
                if (unpoweredSeconds > 0 && state.Node.PowerFallbackWatts is { } fallbackWatts && fallbackWatts > 0)
                {
                    dKwh += fallbackWatts / 1000.0 * (unpoweredSeconds / 3600.0);
                }

                if (dActive > 0 || dCpuSec > 0 || dGpuSec > 0 || dHashes > 0 || dKwh > 0)
                {
                    rec.ActiveMiningSeconds += dActive;
                    rec.CpuMiningSeconds += dCpuSec;
                    rec.GpuMiningSeconds += dGpuSec;
                    rec.TotalCpuHashes += dHashes;
                    rec.PendingCpuHashes += dHashes;
                    rec.EnergyKwh += dKwh;

                    rec.LastActiveMiningSeconds = Clean(lt.ActiveMiningSeconds);
                    rec.LastPoweredMiningSeconds = Clean(lt.PoweredMiningSeconds);
                    rec.LastCpuMiningSeconds = Clean(lt.CpuMiningSeconds);
                    rec.LastGpuMiningSeconds = Clean(lt.GpuMiningSeconds);
                    rec.LastCpuHashes = Clean(lt.TotalCpuHashes);
                    rec.LastEnergyKwh = Clean(lt.TotalEnergyKwh);
                    dirty = true;
                }

                if (lt.UpdatedAt > rec.UpdatedAt)
                {
                    rec.UpdatedAt = lt.UpdatedAt;
                }

                var fallbackCoin = GpuPoolService.CoinForPool(config.GpuMinerFor(state.Node).PoolUrl);
                foreach (var bucket in lt.GpuBuckets ?? [])
                {
                    var key = BucketKey(bucket);
                    var currentWork = Clean(bucket.TotalHashrateDays);
                    var prevWork = rec.LastGpuBuckets.GetValueOrDefault(key, 0.0);
                    if (currentWork + 1e-12 < prevWork) prevWork = 0.0;

                    var dWork = PositiveDelta(currentWork, prevWork);
                    if (dWork <= 0) continue;

                    rec.LastGpuBuckets[key] = currentWork;
                    var coinSlug = ResolveBucketCoin(bucket, fallbackCoin, state.Node.Name, nodeQuotes);
                    if (!string.IsNullOrWhiteSpace(coinSlug))
                    {
                        rec.PendingGpuHashrateDays[coinSlug] =
                            rec.PendingGpuHashrateDays.GetValueOrDefault(coinSlug) + dWork;
                    }

                    dirty = true;
                }
            }

            // Settle pending CPU hashes and GPU hashrate-days into coins whenever pool yields are known.
            foreach (var rec in _records.Values)
            {
                if (rec.PendingCpuHashes > 0 && xmrPerHashDay is { } xmrYield && xmrYield > 0 && double.IsFinite(xmrYield))
                {
                    rec.CpuXmr += rec.PendingCpuHashes * (xmrYield / 86400.0);
                    rec.PendingCpuHashes = 0;
                    dirty = true;
                }

                if (rec.PendingGpuHashrateDays.Count > 0)
                {
                    foreach (var (slug, pendingDays) in rec.PendingGpuHashrateDays.ToArray())
                    {
                        if (pendingDays <= 0)
                        {
                            rec.PendingGpuHashrateDays.Remove(slug);
                            dirty = true;
                            continue;
                        }

                        var quote = FindCoinQuote(coinQuotes, slug) ?? FindNodeQuote(nodeQuotes, rec.NodeName, slug);
                        if (quote?.CoinsPerHashrateDay is { } gpuYield && gpuYield > 0 && double.IsFinite(gpuYield))
                        {
                            rec.GpuCoins[slug] = rec.GpuCoins.GetValueOrDefault(slug) + pendingDays * gpuYield;
                            rec.PendingGpuHashrateDays.Remove(slug);
                            dirty = true;
                        }
                    }
                }
            }

            if (dirty) SaveLocked();

            return BuildFleetSummaryLocked(states, config, xmrPerHashDay, xmrPrice, coinQuotes, nodeQuotes);
        }
    }

    private FleetLifetimeSummary BuildFleetSummaryLocked(
        IReadOnlyList<NodeState> states,
        FleetConfig config,
        double? xmrPerHashDay,
        double? xmrPrice,
        IReadOnlyDictionary<string, NodeGpuQuote>? coinQuotes,
        IReadOnlyDictionary<string, NodeGpuQuote>? nodeQuotes)
    {
        var stateByName = states.ToDictionary(s => s.Node.Name, StringComparer.OrdinalIgnoreCase);
        var orderedNames = states
            .Select(s => s.Node.Name)
            .Concat(_records.Keys.Where(k => !stateByName.ContainsKey(k)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summaries = new List<NodeLifetimeSummary>(orderedNames.Count);
        foreach (var name in orderedNames)
        {
            stateByName.TryGetValue(name, out var state);
            _records.TryGetValue(name, out var rec);
            var nodeCfg = state?.Node ?? config.FindNode(name) ?? new NodeConfig { Name = name };
            summaries.Add(BuildNodeSummary(name, state, nodeCfg, rec, config, xmrPerHashDay, xmrPrice, coinQuotes, nodeQuotes));
        }

        var tracked = summaries.Where(s => s.HasData).ToList();
        DateTimeOffset? earliest = tracked
            .Where(s => s.TrackedSince is not null)
            .Select(s => s.TrackedSince!.Value)
            .DefaultIfEmpty()
            .Min();
        if (earliest == default) earliest = null;

        var totalCpuXmr = tracked.Sum(s => s.CpuXmr);
        double? fleetCpuRev = tracked.Count == 0
            ? null
            : tracked.All(s => !s.CpuRevenueMissing)
                ? tracked.Sum(s => s.CpuRevenue ?? 0.0)
                : null;

        var gpuTotalsBySlug = new Dictionary<string, (string Ticker, double Amount, double? Value, bool Missing)>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in tracked)
        {
            foreach (var coin in node.GpuCoins)
            {
                if (!gpuTotalsBySlug.TryGetValue(coin.Slug, out var agg))
                    agg = (coin.Ticker, 0.0, 0.0, false);

                var missing = agg.Missing || coin.Value is null;
                double? sumVal = missing ? null : (agg.Value ?? 0.0) + coin.Value!.Value;
                gpuTotalsBySlug[coin.Slug] = (coin.Ticker, agg.Amount + coin.Amount, sumVal, missing);
            }
        }

        var totalGpuCoins = gpuTotalsBySlug
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new NodeLifetimeCoin(kv.Key, kv.Value.Ticker, kv.Value.Amount, kv.Value.Value))
            .ToArray();

        double? fleetGpuRev = tracked.Count == 0
            ? null
            : tracked.All(s => !s.GpuRevenueMissing)
                ? tracked.Sum(s => s.GpuRevenue ?? 0.0)
                : null;

        double? fleetTotalRev = tracked.Count == 0
            ? null
            : fleetCpuRev is { } fc && fleetGpuRev is { } fg
                ? fc + fg
                : null;

        double? fleetKnownRev = tracked.Any(s => s.KnownRevenue is not null)
            ? tracked.Sum(s => s.KnownRevenue ?? 0.0)
            : null;

        var hasMissing = tracked.Any(s => s.HasMissingRevenue);
        var totalKwh = tracked.Sum(s => s.EnergyKwh);
        var totalCost = tracked.Sum(s => s.ElectricityCost);
        double? totalNet = fleetTotalRev is { } tr ? tr - totalCost : null;

        return new FleetLifetimeSummary(
            summaries,
            config.Electricity.Currency,
            earliest,
            totalCpuXmr,
            totalGpuCoins,
            fleetCpuRev,
            fleetGpuRev,
            fleetTotalRev,
            fleetKnownRev,
            hasMissing,
            totalKwh,
            totalCost,
            totalNet);
    }

    private static NodeLifetimeSummary BuildNodeSummary(
        string name,
        NodeState? state,
        NodeConfig nodeCfg,
        PersistedNodeRecord? rec,
        FleetConfig config,
        double? xmrPerHashDay,
        double? xmrPrice,
        IReadOnlyDictionary<string, NodeGpuQuote>? coinQuotes,
        IReadOnlyDictionary<string, NodeGpuQuote>? nodeQuotes)
    {
        var online = state?.Online == true;
        var needsUpgrade = online && state?.Snapshot?.Lifetime is null && rec is null;
        if (rec is null)
        {
            return new NodeLifetimeSummary(
                name, online, needsUpgrade, false,
                null, null, 0, 0, 0, 0, 0,
                null, [], null, null, null, false, false, 0, 0, null);
        }

        var cpuXmr = rec.CpuXmr + (rec.PendingCpuHashes > 0 && xmrPerHashDay is > 0
            ? rec.PendingCpuHashes * (xmrPerHashDay.Value / 86400.0)
            : 0.0);
        var hasUnsettledCpu = rec.PendingCpuHashes > 0 && !(xmrPerHashDay is > 0);

        double? cpuRevenue;
        bool cpuMissing;
        if (cpuXmr <= 0 && !hasUnsettledCpu)
        {
            cpuRevenue = 0.0;
            cpuMissing = false;
        }
        else if (!hasUnsettledCpu && xmrPrice is { } xp && xp >= 0 && double.IsFinite(xp))
        {
            cpuRevenue = cpuXmr * xp;
            cpuMissing = false;
        }
        else
        {
            cpuRevenue = null;
            cpuMissing = true;
        }

        var allGpuSlugs = rec.GpuCoins.Keys
            .Concat(rec.PendingGpuHashrateDays.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gpuCoins = new List<NodeLifetimeCoin>(allGpuSlugs.Count);
        var gpuMissing = false;
        var knownGpuSum = 0.0;
        var anyKnownGpu = false;

        foreach (var slug in allGpuSlugs)
        {
            var quote = FindCoinQuote(coinQuotes, slug) ?? FindNodeQuote(nodeQuotes, name, slug);
            var banked = rec.GpuCoins.GetValueOrDefault(slug, 0.0);
            var pendingDays = rec.PendingGpuHashrateDays.GetValueOrDefault(slug, 0.0);
            var settledPending = pendingDays > 0 && quote?.CoinsPerHashrateDay is > 0
                ? pendingDays * quote.CoinsPerHashrateDay.Value
                : 0.0;
            var unsettled = pendingDays > 0 && !(quote?.CoinsPerHashrateDay is > 0);
            var amount = banked + settledPending;
            var ticker = quote?.Coin ?? slug.Split('-')[0].ToUpperInvariant();

            double? val = null;
            if (!unsettled && quote?.Price is { } gp && gp >= 0 && double.IsFinite(gp))
            {
                val = amount * gp;
                knownGpuSum += val.Value;
                anyKnownGpu = true;
            }
            else if (amount > 0 || unsettled)
            {
                gpuMissing = true;
            }

            if (amount > 0 || unsettled)
                gpuCoins.Add(new NodeLifetimeCoin(slug, ticker, amount, val));
        }

        double? gpuRevenue = gpuCoins.Count == 0
            ? 0.0
            : gpuMissing
                ? null
                : knownGpuSum;

        var totalRevenue = cpuRevenue is { } c && gpuRevenue is { } g ? c + g : (double?)null;
        var anyKnown = cpuRevenue is not null || anyKnownGpu || gpuCoins.Count == 0;
        double? knownRevenue = anyKnown ? (cpuRevenue ?? 0.0) + knownGpuSum : null;

        var kwh = rec.EnergyKwh;
        var cost = kwh * config.PricePerKwhFor(nodeCfg);
        double? netProfit = totalRevenue is { } tr ? tr - cost : null;

        return new NodeLifetimeSummary(
            name,
            online,
            OnlineNeedsUpgrade(online, state),
            true,
            rec.TrackedSince == default ? null : rec.TrackedSince,
            rec.UpdatedAt == default ? null : rec.UpdatedAt,
            rec.ActiveMiningSeconds,
            rec.CpuMiningSeconds,
            rec.GpuMiningSeconds,
            rec.TotalCpuHashes,
            cpuXmr,
            cpuRevenue,
            gpuCoins,
            gpuRevenue,
            totalRevenue,
            knownRevenue,
            cpuMissing,
            gpuMissing,
            kwh,
            cost,
            netProfit);
    }

    private static bool OnlineNeedsUpgrade(bool online, NodeState? state) =>
        online && state?.Snapshot?.Lifetime is null;

    private static string BucketKey(GpuLifetimeBucketDto b) =>
        $"{SanitizePoolHost(b.Pool)}|{b.Algorithm.Trim().ToLowerInvariant()}|{(b.HashrateUnit ?? "").Trim().ToLowerInvariant()}";

    private static string SanitizePoolHost(string? pool)
    {
        if (string.IsNullOrWhiteSpace(pool)) return "";
        var s = pool.Trim();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var slash = s.IndexOfAny(['/', '?', '#']);
        if (slash >= 0) s = s[..slash];
        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];
        return s.Trim().ToLowerInvariant();
    }

    private static string? ResolveBucketCoin(
        GpuLifetimeBucketDto bucket,
        string? fallbackCoin,
        string nodeName,
        IReadOnlyDictionary<string, NodeGpuQuote>? nodeQuotes)
    {
        if (GpuPoolService.CoinForPool(bucket.Pool) is { } fromBucket) return fromBucket;
        if (!string.IsNullOrWhiteSpace(fallbackCoin)) return fallbackCoin;
        if (nodeQuotes is not null)
        {
            foreach (var (k, v) in nodeQuotes)
            {
                if (string.Equals(k, nodeName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v.Coin))
                    return v.Coin.Trim().ToLowerInvariant();
            }
        }
        return null;
    }

    private static NodeGpuQuote? FindCoinQuote(IReadOnlyDictionary<string, NodeGpuQuote>? quotes, string slug)
    {
        if (quotes is null) return null;
        if (quotes.TryGetValue(slug, out var exact)) return exact;
        var ticker = slug.Split('-')[0];
        foreach (var (k, v) in quotes)
        {
            if (string.Equals(k, slug, StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, ticker, StringComparison.OrdinalIgnoreCase)
                || string.Equals(v.Coin, ticker, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }
        return null;
    }

    private static NodeGpuQuote? FindNodeQuote(IReadOnlyDictionary<string, NodeGpuQuote>? quotes, string nodeName, string slug)
    {
        if (quotes is null) return null;
        var ticker = slug.Split('-')[0];
        foreach (var (k, v) in quotes)
        {
            if (string.Equals(k, nodeName, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(v.Coin, slug, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(v.Coin, ticker, StringComparison.OrdinalIgnoreCase)))
            {
                return v;
            }
        }
        return null;
    }

    private static double PositiveDelta(double current, double previous)
    {
        var c = Clean(current);
        var p = Clean(previous);
        return c > p ? c - p : 0.0;
    }

    private static double Clean(double value) => double.IsFinite(value) && value > 0 ? value : 0.0;

    private static Dictionary<string, PersistedNodeRecord> Load(string? path)
    {
        var map = new Dictionary<string, PersistedNodeRecord>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return map;

        try
        {
            var dto = JsonSerializer.Deserialize<PersistedLedgerFile>(File.ReadAllText(path), JsonOptions);
            if (dto?.Nodes is null) return map;
            foreach (var rec in dto.Nodes)
            {
                if (string.IsNullOrWhiteSpace(rec.NodeName)) continue;
                rec.GpuCoins = new Dictionary<string, double>(rec.GpuCoins ?? new(), StringComparer.OrdinalIgnoreCase);
                rec.PendingGpuHashrateDays = new Dictionary<string, double>(rec.PendingGpuHashrateDays ?? new(), StringComparer.OrdinalIgnoreCase);
                rec.LastGpuBuckets = new Dictionary<string, double>(rec.LastGpuBuckets ?? new(), StringComparer.OrdinalIgnoreCase);
                map[rec.NodeName] = rec;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return map;
    }

    private void SaveLocked()
    {
        if (string.IsNullOrWhiteSpace(_path)) return;

        var file = new PersistedLedgerFile
        {
            Nodes = _records.Values
                .OrderBy(r => r.NodeName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class PersistedLedgerFile
    {
        public List<PersistedNodeRecord> Nodes { get; set; } = [];
    }

    private sealed class PersistedNodeRecord
    {
        public string NodeName { get; set; } = "";
        public DateTimeOffset TrackedSince { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? AgentStartedAt { get; set; }

        public double ActiveMiningSeconds { get; set; }
        public double CpuMiningSeconds { get; set; }
        public double GpuMiningSeconds { get; set; }
        public double TotalCpuHashes { get; set; }
        public double CpuXmr { get; set; }
        public double PendingCpuHashes { get; set; }
        public double EnergyKwh { get; set; }

        public double LastActiveMiningSeconds { get; set; }
        public double LastPoweredMiningSeconds { get; set; }
        public double LastCpuMiningSeconds { get; set; }
        public double LastGpuMiningSeconds { get; set; }
        public double LastCpuHashes { get; set; }
        public double LastEnergyKwh { get; set; }

        public Dictionary<string, double> GpuCoins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> PendingGpuHashrateDays { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> LastGpuBuckets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
