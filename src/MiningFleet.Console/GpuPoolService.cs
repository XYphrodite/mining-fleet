using System.Globalization;
using System.Text.Json;

namespace MiningFleet.Console;

/// <summary>Which pool, which coin and which address a node's GPU settings point at.</summary>
/// <param name="Provider">Lower-case pool name taken from the stratum host, e.g. <c>kryptex</c>.</param>
/// <param name="Coin">The pool's own slug, which is algorithm-specific: <c>xtm-c29</c>, not <c>xtm</c>.</param>
/// <param name="Address">The wallet, without the worker name the miner appends to it.</param>
public sealed record GpuPoolTarget(string Provider, string Coin, string Address);

/// <summary>One payout the pool says it has sent.</summary>
public sealed record GpuPayout(DateTimeOffset At, double Amount);

/// <summary>What a GPU pool says about the address a card mines to.</summary>
public sealed record GpuPoolStats(
    GpuPoolTarget Target,
    double? Confirmed,
    double? Unconfirmed,
    double? Paid,
    double? Threshold,
    IReadOnlyList<GpuPayout> Payouts)
{
    /// <summary>Everything earned and not yet sent.</summary>
    public double? Pending => Confirmed is null && Unconfirmed is null ? null : (Confirmed ?? 0) + (Unconfirmed ?? 0);

    /// <summary>
    /// What the card actually earned per day, from money the pool has really sent.
    ///
    /// Deliberately not <c>reward.week / 7</c>, which the pool also offers and which is wrong here
    /// in a way that looks right: on a card mining for two days that field holds the whole run, so
    /// dividing by seven under-reports by the same factor. Measured against the live fleet it gave
    /// 178 XTM/day where the payouts said 1,039.
    /// </summary>
    public double? PaidPerDay()
    {
        if (Window() is not { } w) return null;
        return Payouts.Where(p => p.At > w.Start).Sum(p => p.Amount) / w.Span.TotalDays;
    }

    /// <summary>
    /// How much history the rate above is actually based on, so a screen can say "over 18 h"
    /// instead of implying a day it has not watched. Null when one payout or fewer is known: a
    /// single payment says how much, never how fast.
    /// </summary>
    public TimeSpan? PayoutSpan() => Window()?.Span;

    /// <summary>When the pool last sent anything, so a screen can say a rate has gone stale.</summary>
    public DateTimeOffset? LastPayoutAt => Payouts.Count == 0 ? null : Payouts.Max(p => p.At);

    /// <summary>
    /// The stretch the rate is measured over, anchored on the payouts themselves rather than on
    /// the clock. Anchoring on "now" looks equivalent and is not: a card that stopped mining
    /// yesterday has no payouts inside a window ending now, so its rate would come out as a
    /// confident zero instead of an obviously stale number.
    /// </summary>
    private (DateTimeOffset Start, TimeSpan Span)? Window()
    {
        if (Payouts.Count < 2) return null;

        var newest = Payouts.Max(p => p.At);
        var oldest = Payouts.Min(p => p.At);

        // A day at most: difficulty moves, and so does whatever else the machine is asked to do,
        // so a fortnight's average describes neither today nor that fortnight.
        var dayAgo = newest - TimeSpan.FromHours(24);
        var start = dayAgo > oldest ? dayAgo : oldest;

        // The payout at the start of the window is not inside it - those coins were earned before
        // this history begins, and counting them would inflate the rate.
        var span = newest - start;
        return span > TimeSpan.Zero ? (start, span) : null;
    }
}

/// <summary>
/// Reads what a graphics card has actually earned, which Hashvault cannot answer because it knows
/// only Monero and the card mines something else entirely.
///
/// The target is worked out from the node's own GPU settings rather than configured a second time:
/// a stratum host of <c>xtm-c29.kryptex.network</c> names both the pool and its coin slug, and
/// Kryptex's login is <c>address/worker</c>, so both halves are already on the node. An operator
/// who has set the card up has therefore already said everything this needs.
///
/// Only Kryptex is understood. unMineable spells its login <c>XMR:address.worker</c>, so the same
/// split would produce a plausible-looking wrong address - and a wrong address on a balance API
/// answers with somebody else's zero rather than an error. Better to know nothing than that.
/// </summary>
public sealed class GpuPoolService : IDisposable
{
    private const string Kryptex = "kryptex";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan PriceTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly Dictionary<string, (DateTimeOffset At, GpuPoolStats? Stats)> _cache = new();
    private readonly Dictionary<string, (DateTimeOffset At, double? Price)> _prices = new();

    /// <summary>
    /// Reads the pool and the coin out of settings the operator has already given the card.
    /// Returns null when this pool is not one we can read, which is not an error.
    /// </summary>
    public static GpuPoolTarget? TargetFor(GpuMinerConfig? gpu)
    {
        if (gpu?.Enabled != true) return null;
        if (string.IsNullOrWhiteSpace(gpu.PoolUrl) || string.IsNullOrWhiteSpace(gpu.User)) return null;

        var host = gpu.PoolUrl.Split(':')[0].Trim().ToLowerInvariant();
        var labels = host.Split('.');
        if (labels.Length < 2) return null;

        var provider = labels[1];
        if (provider != Kryptex) return null;

        var coin = labels[0];
        if (coin.Length == 0) return null;

        // Kryptex takes address/worker. A login with no slash is the whole address.
        var address = gpu.User.Split('/')[0].Trim();
        return address.Length == 0 ? null : new GpuPoolTarget(provider, coin, address);
    }

    public async Task<GpuPoolStats?> GetAsync(GpuPoolTarget target, CancellationToken ct)
    {
        var key = $"{target.Provider}/{target.Coin}/{target.Address}";
        if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < Ttl)
            return cached.Stats;

        var fetched = await FetchKryptexAsync(target, ct);

        // A failed read keeps whatever was last known rather than blanking a screen mid-refresh;
        // the same rule MarketService follows for the pool it reads.
        if (fetched is null && cached.Stats is not null) return cached.Stats;

        _cache[key] = (DateTimeOffset.UtcNow, fetched);
        return fetched;
    }

    private async Task<GpuPoolStats?> FetchKryptexAsync(GpuPoolTarget target, CancellationToken ct)
    {
        // The coin comes BEFORE /api/v1, which is the one thing about this API that is easy to get
        // wrong: every arrangement with the coin after it answers 404 rather than saying so.
        var root = $"https://pool.kryptex.com/{target.Coin}/api/v1/miner";

        var balance = await ReadJsonAsync($"{root}/balance/{target.Address}", ct);
        if (balance is null) return null;

        var payouts = await ReadJsonAsync($"{root}/payouts/{target.Address}", ct);

        return new GpuPoolStats(
            target,
            Number(balance.Value, "confirmed"),
            Number(balance.Value, "unconfirmed"),
            await PaidAsync(root, target, ct),
            Number(balance.Value, "threshold"),
            ParsePayouts(payouts));
    }

    private async Task<double?> PaidAsync(string root, GpuPoolTarget target, CancellationToken ct)
    {
        var stats = await ReadJsonAsync($"{root}/payouts/{target.Address}/stats", ct);
        return stats is null ? null : Number(stats.Value, "paid");
    }

    private static IReadOnlyList<GpuPayout> ParsePayouts(JsonElement? payouts)
    {
        if (payouts is not { } root
            || !root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<GpuPayout>();
        foreach (var item in results.EnumerateArray())
        {
            // Only money that actually left the pool. A payout still in flight is a promise.
            if (item.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && !string.Equals(status.GetString(), "FINISHED", StringComparison.OrdinalIgnoreCase))
                continue;

            // The date is a unix second count, and the pool sends it as a string.
            if (!item.TryGetProperty("date", out var date)) continue;
            var seconds = date.ValueKind switch
            {
                JsonValueKind.String when long.TryParse(date.GetString(), out var s) => s,
                JsonValueKind.Number => date.GetInt64(),
                _ => (long?)null,
            };
            if (seconds is null) continue;

            // "received" is what landed after the fee; "amount" is what left. The first is the one
            // the operator can spend.
            var amount = Number(item, "received") ?? Number(item, "amount");
            if (amount is null) continue;

            list.Add(new GpuPayout(DateTimeOffset.FromUnixTimeSeconds(seconds.Value), amount.Value));
        }

        return list;
    }

    /// <summary>
    /// CoinGecko's id for a pool's coin slug. Needed because the two disagree and the disagreement
    /// is silent: asking CoinGecko for <c>tari</c> returns <c>{}</c> with a 200, not an error, so a
    /// wrong id here reads as "this coin has no price" forever.
    /// </summary>
    private static string? PriceId(string coin) => coin.Split('-')[0] switch
    {
        "xtm" => "minotari",
        _ => null,
    };

    /// <summary>
    /// What one coin is worth in the fleet's currency, or null when no source carries it.
    ///
    /// Left blank rather than fetched in some other currency, for the reason the Monero side is:
    /// a number in the wrong units beside numbers in the right ones is worse than a gap.
    /// </summary>
    public async Task<double?> GetPriceAsync(GpuPoolTarget target, string currency, CancellationToken ct)
    {
        if (PriceId(target.Coin) is not { } id) return null;

        var vs = currency.ToLowerInvariant();
        var key = $"price/{id}/{vs}";
        if (_prices.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < PriceTtl)
            return cached.Price;

        var url = $"https://api.coingecko.com/api/v3/simple/price?ids={id}&vs_currencies={vs}";
        var json = await ReadJsonAsync(url, ct);
        var price = json is { } root && root.TryGetProperty(id, out var coin) ? Number(coin, vs) : null;

        // A free price feed rate-limits, and a 429 is not evidence that the currency is missing.
        // Keeping the last good number beats blanking the column every few refreshes.
        if (price is null && cached.Price is not null) return cached.Price;

        _prices[key] = (DateTimeOffset.UtcNow, price);
        return price;
    }

    private async Task<JsonElement?> ReadJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a number the pool may send as a JSON number or as a string.</summary>
    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    public void Dispose() => _http.Dispose();
}
