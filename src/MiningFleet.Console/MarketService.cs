using System.Globalization;
using System.Text.Json;

namespace MiningFleet.Console;

/// <summary>Wallet-level numbers Hashvault reports for the configured address.</summary>
public sealed record PoolWalletStats(
    double? HashrateNow,
    double? Hashrate1h,
    double? Hashrate24h,
    long? ValidShares,
    long? InvalidShares,
    double? ConfirmedBalanceXmr,
    double? UnconfirmedBalanceXmr,
    double? TotalPaidXmr,
    double? CreditedTodayXmr,
    double? PayoutThresholdXmr,
    long? PaymentsSent,
    DateTimeOffset? LastShare,
    DateTimeOffset? LastWithdrawal)
{
    /// <summary>Everything earned but not yet in the wallet.</summary>
    public double? PendingXmr =>
        ConfirmedBalanceXmr is null && UnconfirmedBalanceXmr is null
            ? null
            : (ConfirmedBalanceXmr ?? 0) + (UnconfirmedBalanceXmr ?? 0);
}

/// <summary>Network and pool figures used for the income estimate.</summary>
public sealed record PoolNetworkStats(
    double? PoolHashrate,
    long? PoolMiners,
    double? NetworkHashrate,
    double? NetworkDifficulty,
    long? NetworkHeight,
    double? BlockRewardXmr,
    int BlockTimeSeconds,
    double? Price,
    string? PriceCurrency);

/// <summary>
/// Reads Hashvault and, if the pool does not carry the currency, a price feed.
///
/// Hashvault nests everything (pool_statistics.collective, network_statistics, revenue)
/// and reports XMR in atomic units scaled by config.sigDivisor. Every field here is
/// optional on purpose: a renamed field should blank one cell, not break the screen.
/// </summary>
public sealed class MarketService : IDisposable
{
    /// <summary>Monero atomic units per XMR, used when the pool does not state its own divisor.</summary>
    private const double DefaultSigDivisor = 1e12;

    /// <summary>Monero targets a 2-minute block; the pool confirms this as config.coinDiffTarget.</summary>
    public const int DefaultBlockTimeSeconds = 120;

    private readonly FleetConfig _config;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public MarketService(FleetConfig config)
    {
        _config = config;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("xmrig-fleet-console");
    }

    public async Task<PoolWalletStats?> GetWalletStatsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.Pool.Wallet)) return null;

        var root = await GetJsonAsync($"{ApiBase}/wallet/{_config.Pool.Wallet}/stats", ct);
        if (root is null) return null;

        var element = root.Value;
        var divisor = await GetSigDivisorAsync(ct);

        var collective = Path(element, "collective");
        var revenue = Path(element, "revenue");

        return new PoolWalletStats(
            Number(collective, "hashRate"),
            Number(collective, "avg1hashRate"),
            Number(collective, "avg24hashRate"),
            Integer(collective, "validShares"),
            Integer(collective, "invalidShares"),
            Scale(Number(revenue, "confirmedBalance"), divisor),
            Scale(Number(Path(revenue, "unconfirmedBalance", "collective"), "total"), divisor),
            Scale(Number(revenue, "totalPaid"), divisor),
            Scale(Number(revenue, "dailyCredited"), divisor),
            Scale(Number(revenue, "payoutThreshold"), divisor),
            Integer(revenue, "totalPaymentsSent"),
            UnixMilliseconds(Number(collective, "lastShare")),
            UnixMilliseconds(Number(revenue, "lastWithdrawal")));
    }

    public async Task<PoolNetworkStats?> GetNetworkStatsAsync(CancellationToken ct)
    {
        var root = await GetPoolStatsAsync(ct);
        if (root is null) return null;

        var element = root.Value;
        var config = Path(element, "config");
        var divisor = Number(config, "sigDivisor") ?? DefaultSigDivisor;
        var blockTime = (int?)Number(config, "coinDiffTarget") ?? DefaultBlockTimeSeconds;

        var network = Path(element, "network_statistics");
        var difficulty = Number(network, "difficulty");
        var collective = Path(element, "pool_statistics", "collective");

        // Monero has no published network hashrate; it is difficulty over the block time.
        var networkHashrate = difficulty is > 0 && blockTime > 0 ? difficulty / blockTime : null;

        var (price, priceCurrency) = ReadPoolPrice(Path(element, "market"));

        return new PoolNetworkStats(
            Number(collective, "hashRate"),
            Integer(collective, "miners"),
            networkHashrate,
            difficulty,
            Integer(network, "height"),
            // The average of the last ten blocks tracks the real reward better than a constant.
            Scale(Number(Path(element, "pool_statistics", "general"), "last10blocksAvgReward"), divisor)
                ?? Scale(Number(network, "value"), divisor),
            blockTime,
            price,
            priceCurrency);
    }

    /// <summary>
    /// Spot XMR price in the configured currency. The pool publishes only a handful of
    /// currencies (btc, usd, eur, rub), so the external feed covers the rest.
    ///
    /// Returns null rather than a price in some other currency: every screen labels this
    /// number with the configured currency, so a USD figure shown as KZT would be wrong by
    /// a factor of ~500 and would silently poison the whole economics screen.
    /// </summary>
    public async Task<double?> GetPriceAsync(CancellationToken ct)
    {
        var pool = await GetPoolStatsAsync(ct);
        if (pool is not null)
        {
            var (price, _) = ReadPoolPrice(Path(pool.Value, "market"));
            if (price is not null) return price;
        }

        if (string.IsNullOrWhiteSpace(_config.PriceApiUrl)) return null;

        var currency = _config.Electricity.Currency.ToLowerInvariant();
        var url = _config.PriceApiUrl.Replace(CurrencyPlaceholder, currency, StringComparison.OrdinalIgnoreCase);

        var root = await GetJsonAsync(url, ct);
        return root is null ? null : Number(Path(root.Value, "monero"), currency);
    }

    /// <summary>Replaced with the configured currency code before the price feed is called.</summary>
    public const string CurrencyPlaceholder = "{currency}";

    private (double? Price, string? Currency) ReadPoolPrice(JsonElement market)
    {
        var currency = _config.Electricity.Currency.ToLowerInvariant();
        if (Number(market, $"price_{currency}") is { } direct)
            return (direct, _config.Electricity.Currency);
        return (null, null);
    }

    /// <summary>
    /// How to show amounts: the fleet currency, plus the second one when the pool quotes XMR
    /// in both. Taking the cross rate from a single quote keeps every figure on screen
    /// mutually consistent; if either side is missing the echo is dropped rather than
    /// fetched from somewhere else.
    /// </summary>
    public async Task<MoneyFormat> GetMoneyFormatAsync(CancellationToken ct)
    {
        var primary = _config.Electricity.Currency;
        var secondary = _config.Electricity.SecondaryCurrency;
        if (string.IsNullOrWhiteSpace(secondary) ||
            secondary.Equals(primary, StringComparison.OrdinalIgnoreCase))
        {
            return MoneyFormat.Single(primary);
        }

        var pool = await GetPoolStatsAsync(ct);
        if (pool is null) return MoneyFormat.Single(primary);

        var market = Path(pool.Value, "market");
        var inPrimary = Number(market, $"price_{primary.ToLowerInvariant()}");
        var inSecondary = Number(market, $"price_{secondary.ToLowerInvariant()}");

        return inPrimary is > 0 && inSecondary is > 0
            ? new MoneyFormat(primary, secondary, inSecondary.Value / inPrimary.Value)
            : MoneyFormat.Single(primary);
    }

    private string ApiBase => _config.Pool.ApiBase.TrimEnd('/');

    /// <summary>Pool stats back several screens at once, so one response is reused briefly.</summary>
    private JsonElement? _poolStatsCache;
    private DateTimeOffset _poolStatsFetchedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan PoolStatsTtl = TimeSpan.FromSeconds(30);

    private async Task<JsonElement?> GetPoolStatsAsync(CancellationToken ct)
    {
        if (_poolStatsCache is not null && DateTimeOffset.UtcNow - _poolStatsFetchedAt < PoolStatsTtl)
            return _poolStatsCache;

        var fetched = await GetJsonAsync($"{ApiBase}/pool/stats", ct);
        if (fetched is null) return _poolStatsCache;

        _poolStatsCache = fetched;
        _poolStatsFetchedAt = DateTimeOffset.UtcNow;
        return fetched;
    }

    private async Task<double> GetSigDivisorAsync(CancellationToken ct)
    {
        var pool = await GetPoolStatsAsync(ct);
        return pool is null ? DefaultSigDivisor : Number(Path(pool.Value, "config"), "sigDivisor") ?? DefaultSigDivisor;
    }

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var text = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Walks nested objects, returning an undefined element when any step is missing.</summary>
    private static JsonElement Path(JsonElement element, params string[] names)
    {
        var current = element;
        foreach (var name in names)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next))
                return default;
            current = next;
        }
        return current;
    }

    private static double? Scale(double? atomic, double divisor) =>
        atomic is null || divisor <= 0 ? null : atomic.Value / divisor;

    private static double? Number(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    private static long? Integer(JsonElement element, params string[] names) =>
        Number(element, names) is { } value ? (long)value : null;

    private static DateTimeOffset? UnixMilliseconds(double? value) =>
        value is null or 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds((long)value.Value);

    public void Dispose() => _http.Dispose();
}
