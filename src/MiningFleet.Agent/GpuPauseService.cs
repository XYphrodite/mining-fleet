using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>
/// Hands the graphics card back to whoever is using the machine, and takes it again when they are
/// done. The GPU counterpart of <see cref="ThrottleService"/>, with one deliberate simplification:
/// there are no rungs. A job object can hold a CPU miner to a quarter of its speed, but nothing
/// equivalent exists for a card — it is either mining or it is not.
///
/// The rule watches a port or a process, not an application. A local model was the case that
/// prompted it, but a game and a render want the card for the same reason and deserve the same
/// answer.
///
/// Off unless a node is told otherwise, like the throttle and for the same reason.
/// </summary>
public sealed class GpuPauseService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private const double HeavyLoadThresholdPercent = 80.0;
    private const int ThrottledPowerLimitWatts = 100;

    private readonly MinerConfigStore _config;
    private readonly GpuMinerService _miner;
    private readonly HardwareService _hardware;
    private readonly ILogger<GpuPauseService> _log;
    private readonly GpuPauseRule _rule = new();

    private bool _wasEnabled;
    private bool _throttled;
    private int? _restorePowerLimitWatts;

    public GpuPauseService(MinerConfigStore config, GpuMinerService miner, HardwareService hardware, ILogger<GpuPauseService> log)
    {
        _config = config;
        _miner = miner;
        _hardware = hardware;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Same rule as the throttle and the session monitor: this service exists to make a
                // machine pleasant to use. It must never be the reason a node stops answering.
                _log.LogDebug(ex, "GPU pause tick failed");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var config = _config.Current;
        var rule = config.GpuMiner?.PauseWhile;

        if (rule is null || !rule.NamesACondition)
        {
            // The second half is not redundant, for the same reason it is not in the throttle: a
            // node whose miner this service paused, restarted into a config with no rule, would
            // otherwise sit there not mining with nothing to explain it.
            if (_wasEnabled || config.GpuStoppedByPause == true) await StandDownAsync(ct);
            return;
        }

        _wasEnabled = true;

        var busy = UsageProbe.Observe(rule);
        if (busy is null)
        {
            // An unreadable observation is not a quiet one. Resuming on a failed read would put
            // the miner back onto a card somebody is waiting for, so the decision simply holds -
            // the same three-way shape SystemLoadReader uses for a load sample it cannot trust.
            return;
        }

        if (!_rule.Update(busy.Value.Busy, busy.Value.Description, rule.QuietSeconds ?? 300, DateTimeOffset.UtcNow))
        {
            // While busy but light load — throttle instead of full stop, keep mining at reduced power.
            // This is the DST case: game is running but GPU load <80% means it is not heavy.
            if (busy.Value.Busy)
            {
                await MaybeThrottleAsync(ct);
            }
            else if (_throttled)
            {
                await RestoreThrottleAsync(ct);
            }
            _miner.Notice = _rule.Paused ? _rule.Reason : _throttled ? $"GPU throttled to {ThrottledPowerLimitWatts}W (light load while {_rule.Reason})" : null;
            return;
        }

        if (_rule.Paused) await PauseAsync(ct);
        else await ResumeAsync(ct);
    }

    private async Task PauseAsync(CancellationToken ct)
    {
        if (_miner.RunningPid() is null)
        {
            _miner.Notice = _rule.Reason;
            return;
        }

        // Recorded before the stop is attempted, exactly as the throttle does: an agent that dies
        // between killing the miner and writing the flag must come back knowing it was the one
        // that stopped it, or the card never goes back to work.
        _config.Update(new MinerConfigDto { GpuStoppedByPause = true });

        var result = await _miner.StopAsync(ct);
        _miner.Notice = _rule.Reason;
        if (!result.Ok) _log.LogWarning("Could not pause GPU mining: {Message}", result.Message);
        else _log.LogInformation("GPU mining paused: {Reason}", _rule.Reason);
    }

    private async Task ResumeAsync(CancellationToken ct)
    {
        // Only a miner this service stopped is restarted. An operator who stopped GPU mining by
        // hand must not find it running again because the machine happened to go quiet.
        if (_config.Current.GpuStoppedByPause != true)
        {
            _miner.Notice = null;
            return;
        }

        var result = await _miner.StartAsync(ct);
        if (!result.Ok)
        {
            // The flag is cleared only after a start that worked, so a failed restart is retried
            // on the next tick instead of leaving the node stopped and marked as resumed.
            _miner.Notice = $"could not resume GPU mining: {result.Message}";
            _log.LogWarning("Could not resume GPU mining: {Message}", result.Message);
            return;
        }

        _config.Update(new MinerConfigDto { GpuStoppedByPause = false });
        _miner.Notice = null;
        _log.LogInformation("GPU mining resumed: {Reason}", _rule.Reason);
    }

    private async Task MaybeThrottleAsync(CancellationToken ct)
    {
        if (_miner.RunningPid() is null || _throttled) return;
        double? load = null;
        try
        {
            var hw = await _hardware.ReadAsync(ct);
            load = hw.Gpus.FirstOrDefault()?.LoadPercent;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GPU load read failed, falling back to full pause");
            return;
        }
        if (load is null || load >= HeavyLoadThresholdPercent) return;

        var before = await TryGetPowerLimitAsync(ct);
        if (before is not null) _restorePowerLimitWatts = before;

        if (await TrySetPowerLimitAsync(ThrottledPowerLimitWatts, ct))
        {
            _throttled = true;
            _log.LogInformation("GPU throttled to {Limit}W (load {Load}% < {Threshold}% while {Reason})", ThrottledPowerLimitWatts, load, HeavyLoadThresholdPercent, _rule.Reason);
        }
    }

    private async Task RestoreThrottleAsync(CancellationToken ct)
    {
        if (!_throttled) return;
        var limit = _restorePowerLimitWatts ?? 0;
        // 0 means remove limit / restore default — nvidia-smi needs no -pl or a high value; try 0 then fallback to 170.
        var restored = limit > 0 ? await TrySetPowerLimitAsync(limit, ct) : await TryResetPowerLimitAsync(ct);
        if (!restored) restored = await TrySetPowerLimitAsync(170, ct);
        _throttled = false;
        _restorePowerLimitWatts = null;
        if (restored) _log.LogInformation("GPU power limit restored after light load pause");
    }

    private static async Task<int?> TryGetPowerLimitAsync(CancellationToken ct)
    {
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=power.max_limit --format=csv,noheader,nounits",
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                },
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (double.TryParse(output.Trim().Split('\n')[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var watts))
                return (int)Math.Round(watts);
        }
        catch { }
        return null;
    }

    private static async Task<bool> TrySetPowerLimitAsync(int watts, CancellationToken ct)
    {
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = $"-i 0 -pl {watts}",
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                },
            };
            proc.Start();
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<bool> TryResetPowerLimitAsync(CancellationToken ct)
    {
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "-i 0 -rgc",
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                },
            };
            proc.Start();
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Gives the card back for good, for a node whose pause rule was removed.</summary>
    private async Task StandDownAsync(CancellationToken ct)
    {
        _wasEnabled = false;
        _rule.Clear(DateTimeOffset.UtcNow);

        if (_throttled)
            await RestoreThrottleAsync(ct);

        if (_config.Current.GpuStoppedByPause == true)
        {
            var result = await _miner.StartAsync(ct);
            if (result.Ok) _config.Update(new MinerConfigDto { GpuStoppedByPause = false });
        }

        _miner.Notice = null;
    }

}
