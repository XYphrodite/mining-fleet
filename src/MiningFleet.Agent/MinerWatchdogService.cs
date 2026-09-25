using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>
/// Restarts a wanted miner that is not running, on both CPU and GPU.
///
/// Wanted means a start that worked and no stop since — whatever asked for either. A crash
/// changes nothing, so a killed process comes back; an operator's stop, a pause and a
/// throttle to zero all say otherwise, and each owns its own flag, which this service
/// checks first and never touches. That ordering is the whole contract: the watchdog
/// brings back what died, never what was put down.
///
/// A running miner that reports no hashrate for five minutes is restarted too: a pool
/// reconnect and a fresh start both read zero briefly, but nothing healthy stays silent
/// that long. A miner whose API does not answer is left alone — restarting blind could
/// kill one started by hand.
/// </summary>
public sealed class MinerWatchdogService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    private readonly MinerConfigStore _config;
    private readonly MinerService _miner;
    private readonly GpuMinerService _gpu;
    private readonly ThrottleService _throttle;
    private readonly ILogger<MinerWatchdogService> _log;

    private int _cpuFailures;
    private DateTimeOffset? _cpuLastAttempt;
    private DateTimeOffset? _cpuZeroSince;
    private int _gpuFailures;
    private DateTimeOffset? _gpuLastAttempt;
    private DateTimeOffset? _gpuZeroSince;

    /// <summary>Why a wanted miner is still down, for the status DTOs. Null when nothing is owed.</summary>
    public string? CpuNotice { get; private set; }

    /// <summary>Why a wanted card is still idle, for the status DTOs. Null when nothing is owed.</summary>
    public string? GpuNotice { get; private set; }

    public MinerWatchdogService(
        MinerConfigStore config,
        MinerService miner,
        GpuMinerService gpu,
        ThrottleService throttle,
        ILogger<MinerWatchdogService> log)
    {
        _config = config;
        _miner = miner;
        _gpu = gpu;
        _throttle = throttle;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first tick runs at once, not after the interval: a rebooted node with a wanted
        // miner must go back to work with the agent, not ten seconds later still idle.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Same rule as the throttle and the pause services: this one exists to keep
                // miners running. It must never be the reason a node stops answering.
                _log.LogDebug(ex, "Miner watchdog tick failed");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await WatchCpuAsync(now, ct);
        await WatchGpuAsync(now, ct);
    }

    private async Task WatchCpuAsync(DateTimeOffset now, CancellationToken ct)
    {
        var config = _config.Current;
        if (config.MinerStoppedByThrottle == true || config.MinerStoppedByPause == true ||
            config.MinerWanted != true || CpuHeld(config))
        {
            CpuNotice = null;
            _cpuZeroSince = null;
            return;
        }

        if (_miner.RunningPid() is null)
        {
            _cpuZeroSince = null;
            if (CpuHeld(config)) return;
            await StartCpuAsync(now, ct);
            return;
        }

        CpuNotice = null;

        MinerStatusDto status;
        try
        {
            status = await _miner.GetStatusAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreadable status is not a stuck miner. Hold rather than guess.
            _log.LogDebug(ex, "Miner watchdog could not read xmrig status");
            _cpuZeroSince = null;
            return;
        }

        _cpuZeroSince = WatchdogPolicy.NextZeroSince(
            running: true,
            apiOk: status.ApiError is null,
            hashrate: status.Hashrate60s ?? status.Hashrate10s,
            zeroSince: _cpuZeroSince,
            now: now);

        if (!WatchdogPolicy.IsStuck(_cpuZeroSince, now))
        {
            _cpuFailures = 0;
            _cpuLastAttempt = null;
            return;
        }
        if (CpuHeld(config)) return;

        if (!WatchdogPolicy.IsDue(_cpuFailures + 1, _cpuLastAttempt, now))
        {
            CpuNotice = WatchdogPolicy.DescribeStuck(now - _cpuZeroSince!.Value);
            return;
        }

        var result = await _miner.RestartAsync(ct);
        _cpuLastAttempt = DateTimeOffset.UtcNow;
        if (result.Ok)
        {
            _cpuZeroSince = null;
            _log.LogInformation("Watchdog restarted stuck xmrig: {Message}", result.Message);
            return;
        }

        _cpuFailures++;
        CpuNotice = WatchdogPolicy.Describe(_cpuFailures, _cpuLastAttempt.Value + WatchdogPolicy.Delay(_cpuFailures + 1), result.Message);
        _log.LogWarning("Watchdog could not restart stuck xmrig: {Message}", result.Message);
    }

    /// <summary>Held by someone else's rule: a busy machine or a throttle at zero.</summary>
    private bool CpuHeld(MinerConfigDto config)
    {
        // Somebody is at the machine: starting now would buy one flap before the pause
        // service stops it again, at the cost of a RandomX dataset build each time. An
        // unreadable observation holds the start rather than guessing, like the pause
        // service does with its own decision.
        if (config.PauseWhile is { } rule && rule.NamesACondition
            && UsageProbe.Observe(rule) is not { Busy: false })
            return true;

        // The throttle at zero owns the miner as well; its flag lands a tick later at most.
        return _throttle.Status() is { Enabled: true, Level: 0 };
    }

    private async Task StartCpuAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!WatchdogPolicy.IsDue(_cpuFailures + 1, _cpuLastAttempt, now))
            return;

        var result = await _miner.StartAsync(ct);
        _cpuLastAttempt = DateTimeOffset.UtcNow;
        if (result.Ok)
        {
            _cpuFailures = 0;
            CpuNotice = null;
            _log.LogInformation("Watchdog restarted xmrig: {Message}", result.Message);
            return;
        }

        _cpuFailures++;
        CpuNotice = WatchdogPolicy.Describe(_cpuFailures, _cpuLastAttempt.Value + WatchdogPolicy.Delay(_cpuFailures + 1), result.Message);
        _log.LogWarning("Watchdog could not restart xmrig: {Message}", result.Message);
    }

    private async Task WatchGpuAsync(DateTimeOffset now, CancellationToken ct)
    {
        var config = _config.Current;
        if (config.GpuStoppedByPause == true || config.GpuWanted != true || GpuHeld(config))
        {
            GpuNotice = null;
            _gpuZeroSince = null;
            return;
        }

        if (_gpu.RunningPid() is null)
        {
            _gpuZeroSince = null;
            if (GpuHeld(config)) return;
            await StartGpuAsync(now, ct);
            return;
        }

        GpuNotice = null;

        GpuMinerStatusDto status;
        try
        {
            status = await _gpu.GetStatusAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Miner watchdog could not read lolMiner status");
            _gpuZeroSince = null;
            return;
        }

        // A readable hashrate is the sight: blind means hold, zero accrues, anything else
        // clears. A fresh card reads nothing while it builds its dataset, which is far
        // shorter than the stuck window.
        _gpuZeroSince = WatchdogPolicy.NextZeroSince(
            running: true,
            apiOk: status.Hashrate is not null,
            hashrate: status.Hashrate,
            zeroSince: _gpuZeroSince,
            now: now);

        if (!WatchdogPolicy.IsStuck(_gpuZeroSince, now))
        {
            _gpuFailures = 0;
            _gpuLastAttempt = null;
            return;
        }
        if (GpuHeld(config)) return;

        if (!WatchdogPolicy.IsDue(_gpuFailures + 1, _gpuLastAttempt, now))
        {
            GpuNotice = WatchdogPolicy.DescribeStuck(now - _gpuZeroSince!.Value);
            return;
        }

        var result = await _gpu.RestartAsync(ct);
        _gpuLastAttempt = DateTimeOffset.UtcNow;
        if (result.Ok)
        {
            _gpuZeroSince = null;
            _log.LogInformation("Watchdog restarted the stuck GPU miner: {Message}", result.Message);
            return;
        }

        _gpuFailures++;
        GpuNotice = WatchdogPolicy.Describe(_gpuFailures, _gpuLastAttempt.Value + WatchdogPolicy.Delay(_gpuFailures + 1), result.Message);
        _log.LogWarning("Watchdog could not restart the stuck GPU miner: {Message}", result.Message);
    }

    private bool GpuHeld(MinerConfigDto config)
    {
        var pause = config.GpuMiner?.PauseWhile;
        return pause is not null && pause.NamesACondition
            && UsageProbe.Observe(pause) is not { Busy: false };
    }

    private async Task StartGpuAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!WatchdogPolicy.IsDue(_gpuFailures + 1, _gpuLastAttempt, now))
            return;

        var result = await _gpu.StartAsync(ct);
        _gpuLastAttempt = DateTimeOffset.UtcNow;
        if (result.Ok)
        {
            _gpuFailures = 0;
            GpuNotice = null;
            _log.LogInformation("Watchdog restarted the GPU miner: {Message}", result.Message);
            return;
        }

        _gpuFailures++;
        GpuNotice = WatchdogPolicy.Describe(_gpuFailures, _gpuLastAttempt.Value + WatchdogPolicy.Delay(_gpuFailures + 1), result.Message);
        _log.LogWarning("Watchdog could not restart the GPU miner: {Message}", result.Message);
    }
}
