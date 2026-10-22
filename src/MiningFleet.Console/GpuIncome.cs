using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MiningFleet.Console;

/// <summary>One reading of everything a pool wallet has ever earned: pending plus already paid.</summary>
public sealed record GpuIncomeSample(DateTimeOffset At, double Earned);

/// <summary>An amount the wallet accrued over a stretch of time of known length.</summary>
public sealed record GpuIncomeWindow(double Hours, double Amount);

/// <summary>
/// What a card's wallet has accrued, read from the pool's balance rather than from payouts.
///
/// Payouts are the wrong thing to measure a new coin by: Kryptex pays Pearl at one whole PRL, so a
/// card can mine for days with nothing to count. The pool does say how much it holds for the
/// address, though, and "pending + paid" only ever grows, so two readings of it are an exact
/// amount for the time between them.
///
/// The counter is cumulative, which is why gaps in the history cost nothing: a console that was
/// closed overnight still knows what the wallet earned in that time, and a window whose start
/// falls between two readings is interpolated between them.
/// </summary>
public sealed record GpuIncome(GpuPoolStats Stats, IReadOnlyList<GpuIncomeSample> History)
{
    /// <summary>Longest first, so the forecast prefers the steadiest window it can fully observe.</summary>
    private static readonly double[] WindowHours = [24, 12, 6, 3, 1];

    /// <summary>The newest gap between readings across which an interpolated baseline is trusted.</summary>
    private static readonly TimeSpan MaxInterpolationGap = TimeSpan.FromHours(12);

    /// <summary>Pending plus paid. Null unless the pool answered both, so the sum is never half of one.</summary>
    public static double? EarnedOf(GpuPoolStats stats) =>
        stats.Pending is { } pending && stats.Paid is { } paid ? pending + paid : null;

    public GpuIncomeSample? Latest => History.Count == 0 ? null : History[^1];

    /// <summary>When the pool last answered, which is the age of every number here.</summary>
    public DateTimeOffset? ReadAt => Stats.FetchedAt ?? Latest?.At;

    public bool IsStale(DateTimeOffset now) => ReadAt is not { } at || now - at > TimeSpan.FromMinutes(10);

    /// <summary>How long this wallet has been watched, from the first reading to the newest.</summary>
    public TimeSpan Observed => History.Count < 2 ? TimeSpan.Zero : History[^1].At - History[0].At;

    /// <summary>
    /// What the wallet accrued in the trailing <paramref name="hours"/>, or null when history does not
    /// reach back that far - never a short stretch passed off as the full one.
    /// </summary>
    public GpuIncomeWindow? Accrued(double hours)
    {
        if (Latest is not { } latest) return null;
        if (EarnedAt(latest.At - TimeSpan.FromHours(hours)) is not { } baseline) return null;

        var amount = latest.Earned - baseline;
        // A drop means a fee came out of a payout or the pool restated itself. Neither is income.
        return amount < 0 || !double.IsFinite(amount) ? null : new GpuIncomeWindow(hours, amount);
    }

    /// <summary>The trailing day, which is what the XTM panel calls "last 24h".</summary>
    public GpuIncomeWindow? Day => Accrued(24);

    /// <summary>Everything accrued since this wallet was first read, for before there is a day of history.</summary>
    public GpuIncomeWindow? SinceStart
    {
        get
        {
            if (History.Count < 2 || Observed < TimeSpan.FromMinutes(10)) return null;
            var amount = History[^1].Earned - History[0].Earned;
            return amount < 0 ? null : new GpuIncomeWindow(Observed.TotalHours, amount);
        }
    }

    /// <summary>The longest window that history fully covers. A trailing mean, downtime included.</summary>
    public GpuIncomeWindow? ForecastBasis => WindowHours.Select(Accrued).FirstOrDefault(w => w is not null);

    public double? ForecastPerDay => ForecastBasis is { } w ? w.Amount * 24 / w.Hours : null;

    private double? EarnedAt(DateTimeOffset when)
    {
        if (History.Count == 0 || when < History[0].At) return null;

        for (var i = 0; i < History.Count; i++)
        {
            var next = History[i];
            if (next.At < when) continue;

            if (next.At == when || i == 0) return next.Earned;

            var before = History[i - 1];
            var gap = next.At - before.At;
            if (gap > MaxInterpolationGap) return null;

            var fraction = (when - before.At) / gap;
            return before.Earned + (next.Earned - before.Earned) * fraction;
        }

        // The window starts after the newest reading, which cannot happen for a trailing window.
        return null;
    }
}

/// <summary>
/// The readings behind <see cref="GpuIncome"/>, kept beside fleet.json so a forecast does not have to
/// wait out a day of the console staying open every time it is started.
///
/// Wallet addresses are not written here. Keys are a hash, so this file can be looked at without
/// being a second copy of what fleet.json guards.
/// </summary>
public sealed class GpuIncomeLedger
{
    private static readonly ConcurrentDictionary<string, GpuIncomeLedger> Shared = new();

    /// <summary>Readings closer together than this add nothing but file size.</summary>
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan Retention = TimeSpan.FromDays(8);

    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<GpuIncomeSample>> _samples;

    /// <param name="path">Null keeps the history in memory only, which is what a test wants.</param>
    public GpuIncomeLedger(string? path = null)
    {
        _path = path;
        _samples = Load(path);
    }

    /// <summary>One ledger per file, so every screen in the process reads and writes the same history.</summary>
    public static GpuIncomeLedger For(FleetConfig config)
    {
        var directory = string.IsNullOrWhiteSpace(config.Path) ? null : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(config.Path));
        if (directory is null) return new GpuIncomeLedger();
        return Shared.GetOrAdd(directory, d => new GpuIncomeLedger(System.IO.Path.Combine(d, "gpu-income.json")));
    }

    public static string KeyOf(GpuPoolTarget target)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{target.Provider}/{target.Coin}/{target.Address}"));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    /// <summary>Adds a reading and returns the history for that wallet, oldest first.</summary>
    public IReadOnlyList<GpuIncomeSample> Record(GpuPoolTarget target, DateTimeOffset at, double? earned)
    {
        var key = KeyOf(target);
        lock (_gate)
        {
            if (!_samples.TryGetValue(key, out var list)) _samples[key] = list = [];

            if (earned is { } value && double.IsFinite(value) && (list.Count == 0 || at - list[^1].At >= MinSpacing))
            {
                list.Add(new GpuIncomeSample(at, value));
                list.RemoveAll(s => at - s.At > Retention);
                Save();
            }

            return list.ToList();
        }
    }

    public IReadOnlyList<GpuIncomeSample> History(GpuPoolTarget target)
    {
        lock (_gate) return _samples.TryGetValue(KeyOf(target), out var list) ? list.ToList() : [];
    }

    private static Dictionary<string, List<GpuIncomeSample>> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, List<GpuIncomeSample>>>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    foreach (var list in loaded.Values) list.Sort((a, b) => a.At.CompareTo(b.At));
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged history file costs the forecast its past, not the console its start.
        }

        return new();
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_samples));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only install directory: the history just stays in memory for this run.
        }
    }
}

/// <summary>Reads a card's wallet from the pool and keeps the reading, so income can be measured over time.</summary>
public sealed class GpuIncomeService : IDisposable
{
    private readonly GpuPoolService _pool;
    private readonly GpuIncomeLedger _ledger;

    public GpuIncomeService(GpuPoolService pool, GpuIncomeLedger ledger)
    {
        _pool = pool;
        _ledger = ledger;
    }

    public async Task<GpuIncome?> GetAsync(GpuPoolTarget target, CancellationToken ct)
    {
        var stats = await _pool.GetAsync(target, ct);
        if (stats is null) return null;

        // The time of the reading is the time the pool was asked, not the time of this call: the
        // pool client hands back what it last knew when the pool is down.
        var history = _ledger.Record(target, stats.FetchedAt ?? DateTimeOffset.UtcNow, GpuIncome.EarnedOf(stats));
        return new GpuIncome(stats, history);
    }

    public Task<double?> GetPriceAsync(GpuPoolTarget target, string currency, CancellationToken ct) =>
        _pool.GetPriceAsync(target, currency, ct);

    public void Dispose() => _pool.Dispose();
}
