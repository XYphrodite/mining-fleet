using System.Globalization;
using System.Text.Json;

namespace MiningFleet.Console;

/// <summary>A pool account, not a card. Worker suffixes must never multiply its income.</summary>
public sealed record XtmAccount(string Host, string Address)
{
    public static XtmAccount? From(string? pool, string? login)
    {
        if (string.IsNullOrWhiteSpace(pool) || string.IsNullOrWhiteSpace(login)) return null;
        if (!Uri.TryCreate(pool.Contains("://") ? pool : "stratum://" + pool,
                UriKind.Absolute, out var uri)) return null;
        // Regional stratum endpoints share the same C29 accounting API. Other algorithms
        // and lookalike domains must not accidentally query this wallet on the wrong pool.
        if (uri.Host is not ("taric29.luckypool.io" or "taric29-ca.luckypool.io"
            or "taric29-sg.luckypool.io")) return null;
        var address = login.Split('/', '.')[0].Trim();
        return address.Length == 0 ? null : new("taric29.luckypool.io", address);
    }
}

public sealed record XtmRewardWindow(int Hours, double Amount, DateTimeOffset End);

public sealed record XtmIncome(
    DateTimeOffset FetchedAt,
    DateTimeOffset HistorySince,
    IReadOnlyList<XtmRewardWindow> Windows)
{
    public XtmRewardWindow? Day => Windows.FirstOrDefault(w => w.Hours == 24);
    public bool IsStale(DateTimeOffset now) => Day is not { } day
        || now - day.End > TimeSpan.FromMinutes(10)
        || now - FetchedAt > TimeSpan.FromMinutes(10);

    /// <summary>
    /// A trailing daily mean, assuming the same future duty cycle and pool conditions.
    /// It includes downtime and never scales a short session up to a full day. No use is
    /// made of the pool's disabled profit calculator or the card's instantaneous speed.
    /// </summary>
    public XtmRewardWindow? ForecastBasis(DateTimeOffset now) => IsStale(now) ? null
        : Windows.Where(w => w.Hours >= 24
                && w.End - HistorySince >= TimeSpan.FromHours(w.Hours)
                && now - w.End <= TimeSpan.FromMinutes(10))
            .OrderByDescending(w => w.Hours).FirstOrDefault();
}

public sealed record XtmIncomeRow(string Nodes, XtmAccount? Account, XtmIncome? Income,
    bool ConfigUnverified = false);

/// <summary>Read-only LuckyPool reward accounting. Cached failures retain the original age.</summary>
public sealed class XtmIncomeService : IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly Dictionary<XtmAccount, (DateTimeOffset Attempt, XtmIncome? Income)> _cache = new();
    private readonly Dictionary<XtmAccount, DateTimeOffset> _history = new();

    public XtmIncomeService(HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(12);
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<XtmIncome?> GetAsync(XtmAccount account, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(account, out var cached)
            && now - cached.Attempt < TimeSpan.FromMinutes(2)) return cached.Income;

        XtmIncome? income = null;
        try
        {
            var root = $"https://{account.Host}/api";
            // Read the divisor from the pool instead of assuming all coins use micro-units.
            using var pool = await ReadAsync(root + "/stats", ct);
            using var wallet = await ReadAsync(root + "/stats_address?address="
                + Uri.EscapeDataString(account.Address), ct);
            income = Parse(pool.RootElement, wallet.RootElement, now,
                _history.GetValueOrDefault(account, now));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // A failed API read is not evidence of zero earnings.
        }

        if (income is not null) _history[account] = income.HistorySince;
        _cache[account] = (now, income ?? cached.Income);
        return income ?? cached.Income;
    }

    private async Task<JsonDocument> ReadAsync(string uri, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    public static XtmIncome? Parse(JsonElement pool, JsonElement wallet,
        DateTimeOffset now, DateTimeOffset observedSince)
    {
        if (!Object(pool, "config", out var config)
            || !config.TryGetProperty("symbol", out var symbol)
            || symbol.ValueKind != JsonValueKind.String || symbol.GetString() != "XTM"
            || Number(config, "coinUnits") is not { } units || units <= 0
            || wallet.ValueKind != JsonValueKind.Object
            || !wallet.TryGetProperty("rewardStats", out var rewards)
            || rewards.ValueKind != JsonValueKind.Array) return null;

        var windows = new List<XtmRewardWindow>();
        foreach (var reward in rewards.EnumerateArray())
        {
            if (reward.ValueKind != JsonValueKind.Object
                || !reward.TryGetProperty("period", out var period)
                || period.ValueKind != JsonValueKind.String) continue;
            var hours = period.GetString() switch { "24h" => 24, "48h" => 48, "72h" => 72, _ => 0 };
            if (hours == 0 || Number(reward, "amount") is not { } amount || amount < 0
                || Milliseconds(reward, "startTime") is not { } start
                || Milliseconds(reward, "endTime") is not { } end
                || end > now.AddMinutes(2)
                || Math.Abs((end - start).TotalHours - hours) > 1.0 / 3600
                || windows.Any(w => w.Hours == hours)) continue;
            var coins = amount / units;
            if (double.IsFinite(coins)) windows.Add(new(hours, coins, end));
        }
        // A broken/missing daily field must not turn into a confident zero or refresh old data.
        if (!windows.Any(w => w.Hours == 24)) return null;

        var since = observedSince < now ? observedSince : now;
        if (wallet.TryGetProperty("rewards", out var history) && history.ValueKind == JsonValueKind.Array)
            foreach (var reward in history.EnumerateArray())
                if (Milliseconds(reward, "timestamp") is { } at && at < since
                    && Number(reward, "minerReward") is > 0) since = at;

        return new(now, since, windows);
    }

    private static bool Object(JsonElement value, string key, out JsonElement result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out result)
            && result.ValueKind == JsonValueKind.Object;
    }

    private static double? Number(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var number)) return null;
        double result;
        var valid = number.ValueKind == JsonValueKind.Number ? number.TryGetDouble(out result)
            : double.TryParse(number.ValueKind == JsonValueKind.String ? number.GetString() : null,
                NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        return valid && double.IsFinite(result) ? result : null;
    }

    private static DateTimeOffset? Milliseconds(JsonElement value, string key) =>
        Number(value, key) is { } number && number >= 0 && number <= 253402300799999
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)number) : null;

    public void Dispose() => _http.Dispose();
}
