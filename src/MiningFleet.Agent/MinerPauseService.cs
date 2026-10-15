using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>
/// Stops CPU mining while somebody is using the machine, and starts it again once they are done.
///
/// The CPU counterpart of <see cref="GpuPauseService"/>, sharing its rule, its observations and its
/// asymmetry — down on the next tick, up only after a long quiet stretch.
///
/// Stopping rather than throttling is the whole point, and it is a conclusion rather than a
/// preference. Every partial measure this fleet has measured is a poor trade: a job-object cap
/// keeps 27.6% of the hashrate at half throttle, lowering priority costs 85% on a hybrid CPU, and
/// reserving cores by affinity leaves the miner a thread on every physical core, so a game still
/// runs on hyperthread siblings of saturated ones. A stopped miner also hands back its RandomX
/// dataset — 2.3 GB of huge pages — which on a 16 GB node is most of what makes the machine feel
/// slow.
///
/// Separate from <see cref="ThrottleService"/> because the trigger is different in kind. The
/// throttle reads CPU load, and load is exactly what a starved program cannot generate: while a
/// game froze on one node the journal read `other avg=8.4%` throughout. Naming the program is the
/// only signal that works.
/// </summary>
public sealed class MinerPauseService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private const int LightThrottleLevel = 45;

    private readonly MinerConfigStore _config;
    private readonly MinerService _miner;
    private readonly MinerCpuLimit _limit;
    private readonly ThrottleLog _journal;
    private readonly ILogger<MinerPauseService> _log;
    private readonly GpuPauseRule _rule = new();
    private readonly GpuPauseRule _lightRule = new();

    private bool _wasEnabled;
    private bool _lightThrottled;

    public MinerPauseService(MinerConfigStore config, MinerService miner, MinerCpuLimit limit, ThrottleLog journal, ILogger<MinerPauseService> log)
    {
        _config = config;
        _miner = miner;
        _limit = limit;
        _journal = journal;
        _log = log;
    }

    /// <summary>Why the miner is stopped, or null when nothing is holding it. Shown by the console.</summary>
    public string? Notice { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Miner pause tick failed");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var config = _config.Current;
        var rule = config.PauseWhile;
        var lightRule = config.ThrottleWhile;

        // Heavy pause (cs2/dota2/deadlock) — full stop, always wins over light throttle.
        if (rule is null || !rule.NamesACondition)
        {
            if (_wasEnabled || config.MinerStoppedByPause == true) await StandDownAsync(ct);
        }
        else
        {
            _wasEnabled = true;
            var busy = UsageProbe.Observe(rule);
            if (busy is null) { /* hold */ }
            else if (!_rule.Update(busy.Value.Busy, busy.Value.Description, rule.QuietSeconds ?? 300, DateTimeOffset.UtcNow))
            {
                Notice = _rule.Paused ? _rule.Reason : null;
                // Still need to evaluate light throttle when not paused, but don't overwrite notice if paused.
                if (_rule.Paused) { await HandleLightThrottleAsync(null, ct); return; }
            }
            else
            {
                if (_rule.Paused) { await PauseAsync(ct); return; }
                else await ResumeAsync(ct);
            }
        }

        // Light throttle (DST) — reduce to 45% while running, restore after quiet.
        await HandleLightThrottleAsync(lightRule, ct);
    }

    private async Task HandleLightThrottleAsync(GpuPauseRuleDto? lightRule, CancellationToken ct)
    {
        if (lightRule is null || !lightRule.NamesACondition)
        {
            if (_lightThrottled) await RestoreLightThrottleAsync(ct);
            if (!_rule.Paused) Notice = null;
            return;
        }

        var busy = UsageProbe.Observe(lightRule);
        if (busy is null) return;

        if (!_lightRule.Update(busy.Value.Busy, busy.Value.Description, lightRule.QuietSeconds ?? 120, DateTimeOffset.UtcNow))
        {
            // No state change — keep current throttling.
            if (_lightRule.Paused && !_rule.Paused) Notice = $"throttled to {LightThrottleLevel}% for {_lightRule.Reason}";
            return;
        }

        if (_lightRule.Paused)
        {
            if (_rule.Paused) return; // heavy pause wins
            await ApplyLightThrottleAsync(ct);
        }
        else await RestoreLightThrottleAsync(ct);
    }

    private async Task ApplyLightThrottleAsync(CancellationToken ct)
    {
        var pid = _miner.RunningPid();
        if (pid is null) { Notice = _lightRule.Reason; return; }
        // Full share approximate: use Environment.ProcessorCount vs thread count could be done via ThrottleService,
        // here we apply as machine share via MinerCpuLimit with estimated full share.
        var fullShare = 50.0; // fallback; ThrottleService measures exact, this is "немного резать" not exact science
        try
        {
            var status = await _miner.GetStatusAsync(ct);
            if (status.MiningThreads is >0) fullShare = Math.Clamp(100.0 * status.MiningThreads.Value / Environment.ProcessorCount, 1, 100);
        }
        catch { }

        if (_limit.Apply(pid.Value, LightThrottleLevel, fullShare, out var detail))
        {
            _lightThrottled = true;
            Notice = $"throttled to {LightThrottleLevel}% for {_lightRule.Reason}";
            _log.LogInformation("CPU throttled to {Level}% for {Reason}: {Detail}", LightThrottleLevel, _lightRule.Reason, detail);
        }
    }

    private async Task RestoreLightThrottleAsync(CancellationToken ct)
    {
        if (!_lightThrottled) return;
        var pid = _miner.RunningPid();
        if (pid is not null)
        {
            _limit.Apply(pid.Value, 100, 100, out _);
            _log.LogInformation("CPU throttle lifted after DST quiet");
        }
        _lightThrottled = false;
        _lightRule.Clear(DateTimeOffset.UtcNow);
        if (!_rule.Paused) Notice = null;
    }

    private async Task PauseAsync(CancellationToken ct)
    {
        Notice = _rule.Reason;
        if (_miner.RunningPid() is null) return;

        // Written before the stop is attempted, exactly as the throttle and the GPU pause do: an
        // agent that dies between killing the miner and saving the flag must come back knowing it
        // was the one that stopped it, or the node never mines again.
        _config.Update(new MinerConfigDto { MinerStoppedByPause = true });

        var result = await _miner.StopAsync(ct);
        if (!result.Ok)
        {
            _log.LogWarning("Could not pause CPU mining: {Message}", result.Message);
            return;
        }

        _journal.RecordPause(true, _rule.Reason);
        _log.LogInformation("CPU mining paused: {Reason}", _rule.Reason);
    }

    private async Task ResumeAsync(CancellationToken ct)
    {
        var config = _config.Current;

        // Only a miner this service stopped is started again. An operator who stopped mining by
        // hand must not find it running because the machine happened to go quiet.
        if (config.MinerStoppedByPause != true)
        {
            Notice = null;
            return;
        }

        // And never over the throttle's head. If it took the miner to zero as well, the machine is
        // still busy by its reckoning, and it owns the decision to bring it back.
        if (config.MinerStoppedByThrottle == true)
        {
            Notice = "quiet, but the throttle is still holding the miner down";
            return;
        }

        var result = await _miner.StartAsync(ct);
        if (!result.Ok)
        {
            // The flag is cleared only after a start that worked, so a failed start is retried on
            // the next tick rather than leaving a node stopped and marked as resumed.
            Notice = $"could not resume CPU mining: {result.Message}";
            _log.LogWarning("Could not resume CPU mining: {Message}", result.Message);
            return;
        }

        _config.Update(new MinerConfigDto { MinerStoppedByPause = false });
        Notice = null;
        _journal.RecordPause(false, _rule.Reason);
        _log.LogInformation("CPU mining resumed: {Reason}", _rule.Reason);
    }

    /// <summary>Gives the miner back for good, for a node whose rule was removed.</summary>
    private async Task StandDownAsync(CancellationToken ct)
    {
        _wasEnabled = false;
        _rule.Clear(DateTimeOffset.UtcNow);
        Notice = null;

        if (_config.Current.MinerStoppedByPause != true) return;
        if (_config.Current.MinerStoppedByThrottle == true)
        {
            // Not ours to start any more, but the debt is settled: the throttle now owns it.
            _config.Update(new MinerConfigDto { MinerStoppedByPause = false });
            return;
        }

        var result = await _miner.StartAsync(ct);
        if (result.Ok) _config.Update(new MinerConfigDto { MinerStoppedByPause = false });
    }
}
