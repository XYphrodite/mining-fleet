using System.Diagnostics;
using System.Text.Json;
using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>
/// Monotonic per-node accumulator of CPU hashes, GPU hashrate-days and mining kWh.
/// Pure and clock-injected so sleep-clamping, bucket merging and crash-safe persistence
/// can be verified deterministically in unit tests.
/// </summary>
public sealed class LifetimeTracker
{
    public const string FileName = "lifetime.json";
    public const double MaxSampleSeconds = 60.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string? _path;
    private readonly object _gate = new();
    private DateTimeOffset _startedAt;
    private DateTimeOffset _updatedAt;
    private double _activeMiningSeconds;
    private double _cpuMiningSeconds;
    private double _gpuMiningSeconds;
    private double _totalCpuHashes;
    private double _totalEnergyKwh;
    private double _poweredMiningSeconds;
    private readonly List<GpuBucketState> _gpuBuckets = [];
    private bool _dirty;

    public LifetimeTracker(string? basePath = null, DateTimeOffset? now = null)
    {
        _path = string.IsNullOrWhiteSpace(basePath) ? null : Path.Combine(basePath, FileName);
        var initialNow = now ?? DateTimeOffset.UtcNow;
        if (!TryLoad(_path, out var loaded))
        {
            _startedAt = initialNow;
            _updatedAt = initialNow;
            return;
        }

        _startedAt = loaded.StartedAt == default ? initialNow : loaded.StartedAt;
        _updatedAt = loaded.UpdatedAt == default ? _startedAt : loaded.UpdatedAt;
        _activeMiningSeconds = Clean(loaded.ActiveMiningSeconds);
        _cpuMiningSeconds = Clean(loaded.CpuMiningSeconds);
        _gpuMiningSeconds = Clean(loaded.GpuMiningSeconds);
        _totalCpuHashes = Clean(loaded.TotalCpuHashes);
        _totalEnergyKwh = Clean(loaded.TotalEnergyKwh);
        _poweredMiningSeconds = Math.Min(_activeMiningSeconds, Clean(loaded.PoweredMiningSeconds));

        foreach (var b in loaded.GpuBuckets ?? [])
        {
            var seconds = Clean(b.MiningSeconds);
            var work = Clean(b.TotalHashrateDays);
            if (seconds <= 0 && work <= 0) continue;
            MergeBucket(SanitizePool(b.Pool), CleanText(b.Algorithm), CleanUnit(b.HashrateUnit), seconds, work);
        }
    }

    public NodeLifetimeDto Snapshot()
    {
        lock (_gate)
        {
            return BuildLocked();
        }
    }

    public void Sample(
        bool cpuRunning,
        double? cpuHashrate,
        bool gpuRunning,
        string? gpuPool,
        string? gpuAlgorithm,
        string? gpuUnit,
        double? gpuHashrate,
        double? powerWatts,
        double elapsedSeconds,
        DateTimeOffset now)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return;

        // Clamp sleep/hibernate/debugger gaps so waking after 8 hours never invents 8 hours of hashes.
        var dt = Math.Min(elapsedSeconds, MaxSampleSeconds);

        lock (_gate)
        {
            _updatedAt = now;
            var anyMining = cpuRunning || gpuRunning;
            if (!anyMining) return;

            _activeMiningSeconds += dt;
            if (powerWatts is { } watts && double.IsFinite(watts) && watts > 0)
            {
                _totalEnergyKwh += watts / 1000.0 * (dt / 3600.0);
                _poweredMiningSeconds += dt;
            }

            if (cpuRunning)
            {
                _cpuMiningSeconds += dt;
                if (cpuHashrate is { } ch && double.IsFinite(ch) && ch > 0)
                    _totalCpuHashes += ch * dt;
            }

            if (gpuRunning)
            {
                _gpuMiningSeconds += dt;
                if (gpuHashrate is { } gh && double.IsFinite(gh) && gh > 0)
                {
                    MergeBucket(
                        SanitizePool(gpuPool),
                        CleanText(gpuAlgorithm),
                        CleanUnit(gpuUnit),
                        dt,
                        gh * (dt / 86400.0));
                }
            }

            _dirty = true;
        }
    }

    public void Flush(bool force = false)
    {
        if (_path is null) return;

        NodeLifetimeDto? toWrite;
        lock (_gate)
        {
            if (!_dirty && !force) return;
            toWrite = BuildLocked();
            _dirty = false;
        }

        var tmp = _path + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(toWrite, JsonOptions);
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate) _dirty = true;
        }
    }

    /// <summary>Keeps only <c>host</c> or <c>host:port</c> so logins/wallets can never enter lifetime.json.</summary>
    public static string SanitizePool(string? pool)
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

    private void MergeBucket(string pool, string algorithm, string? unit, double seconds, double workDays)
    {
        var existing = _gpuBuckets.FirstOrDefault(b =>
            string.Equals(b.Pool, pool, StringComparison.OrdinalIgnoreCase)
            && string.Equals(b.Algorithm, algorithm, StringComparison.OrdinalIgnoreCase)
            && string.Equals(b.Unit ?? "", unit ?? "", StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            _gpuBuckets.Add(new GpuBucketState
            {
                Pool = pool,
                Algorithm = algorithm,
                Unit = unit,
                MiningSeconds = seconds,
                TotalHashrateDays = workDays,
            });
            return;
        }

        existing.MiningSeconds += seconds;
        existing.TotalHashrateDays += workDays;
    }

    private NodeLifetimeDto BuildLocked() => new()
    {
        StartedAt = _startedAt,
        UpdatedAt = _updatedAt,
        ActiveMiningSeconds = _activeMiningSeconds,
        CpuMiningSeconds = _cpuMiningSeconds,
        GpuMiningSeconds = _gpuMiningSeconds,
        TotalCpuHashes = _totalCpuHashes,
        TotalEnergyKwh = _totalEnergyKwh,
        PoweredMiningSeconds = _poweredMiningSeconds,
        GpuBuckets = _gpuBuckets
            .Select(b => new GpuLifetimeBucketDto(b.Pool, b.Algorithm, b.Unit, b.MiningSeconds, b.TotalHashrateDays))
            .ToArray(),
    };

    private static bool TryLoad(string? path, out NodeLifetimeDto dto)
    {
        dto = new NodeLifetimeDto();
        if (path is null || !File.Exists(path)) return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<NodeLifetimeDto>(File.ReadAllText(path), JsonOptions);
            if (parsed is null) return false;
            dto = parsed;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static double Clean(double value) => double.IsFinite(value) && value > 0 ? value : 0.0;
    private static string CleanText(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    private static string? CleanUnit(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class GpuBucketState
    {
        public required string Pool { get; init; }
        public required string Algorithm { get; init; }
        public string? Unit { get; init; }
        public double MiningSeconds { get; set; }
        public double TotalHashrateDays { get; set; }
    }
}

/// <summary>
/// Background sampler that integrates live CPU/GPU work and energy every 15 seconds on the node
/// and flushes atomically to <c>lifetime.json</c> once a minute and on shutdown.
/// </summary>
public sealed class LifetimeTrackerService : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);

    private readonly LifetimeTracker _tracker;
    private readonly MinerService _miner;
    private readonly GpuMinerService _gpu;
    private readonly HardwareService _hardware;
    private readonly MinerConfigStore _config;
    private readonly ILogger<LifetimeTrackerService> _log;

    public LifetimeTrackerService(
        LifetimeTracker tracker,
        MinerService miner,
        GpuMinerService gpu,
        HardwareService hardware,
        MinerConfigStore config,
        ILogger<LifetimeTrackerService> log)
    {
        _tracker = tracker;
        _miner = miner;
        _gpu = gpu;
        _hardware = hardware;
        _config = config;
        _log = log;
    }

    public NodeLifetimeDto Snapshot() => _tracker.Snapshot();

    public void Flush(bool force = false) => _tracker.Flush(force);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Persist initial StartedAt immediately so a new node reports a stable epoch right away.
        _tracker.Flush(force: true);

        var lastSample = Stopwatch.GetTimestamp();
        var lastFlush = Stopwatch.GetTimestamp();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(SampleInterval, stoppingToken);

                var elapsed = Stopwatch.GetElapsedTime(lastSample).TotalSeconds;
                lastSample = Stopwatch.GetTimestamp();

                await SampleOnceAsync(elapsed, stoppingToken);

                if (Stopwatch.GetElapsedTime(lastFlush) >= FlushInterval)
                {
                    _tracker.Flush();
                    lastFlush = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _tracker.Flush();
        }
    }

    private async Task SampleOnceAsync(double elapsedSeconds, CancellationToken ct)
    {
        try
        {
            var cpuAlive = _miner.RunningPid() is not null;
            var gpuAlive = _gpu.RunningPid() is not null;
            if (!cpuAlive && !gpuAlive)
            {
                _tracker.Sample(false, null, false, null, null, null, null, null, elapsedSeconds, DateTimeOffset.UtcNow);
                return;
            }

            var hwTask = _hardware.ReadAsync(ct);
            MinerStatusDto? cpu = null;
            GpuMinerStatusDto? gpu = null;
            if (cpuAlive && gpuAlive)
            {
                var cpuTask = _miner.GetStatusAsync(ct);
                var gpuTask = _gpu.GetStatusAsync(ct);
                await Task.WhenAll(cpuTask, gpuTask, hwTask);
                cpu = cpuTask.Result;
                gpu = gpuTask.Result;
            }
            else if (cpuAlive)
            {
                var cpuTask = _miner.GetStatusAsync(ct);
                await Task.WhenAll(cpuTask, hwTask);
                cpu = cpuTask.Result;
            }
            else
            {
                var gpuTask = _gpu.GetStatusAsync(ct);
                await Task.WhenAll(gpuTask, hwTask);
                gpu = gpuTask.Result;
            }

            var hw = hwTask.Result;
            var cfg = _config.Current;

            _tracker.Sample(
                cpu?.Running == true,
                cpu?.Hashrate10s ?? cpu?.Hashrate60s,
                gpu?.Running == true,
                gpu?.Pool ?? cfg.GpuMiner?.PoolUrl,
                gpu?.Algorithm ?? cfg.GpuMiner?.Algorithm,
                gpu?.HashrateUnit,
                gpu?.Hashrate,
                hw.EstimatedPowerWatts,
                elapsedSeconds,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "Lifetime sample skipped");
        }
    }
}
