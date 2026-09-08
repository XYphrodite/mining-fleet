using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>
/// Keeps the miner out of the way of whoever is using the machine.
///
/// Two mechanisms, because one rung needs something the others do not:
///
///   25-100%  a CPU cap on the miner process (<see cref="MinerCpuLimit"/>). Instant, and the
///            RandomX dataset and huge pages stay exactly where they are.
///   0%       the miner is stopped outright. A capped miner still holds ~2.3 GB for its dataset,
///            and on a 16 GB node that memory is the thing that makes the machine feel slow -
///            measured three times on the Xeon, where freeing about 2 GB moved the hashrate 3.3x.
///            Nothing short of stopping gives it back.
///
/// Off unless a node is told otherwise. Throttling a rig nobody sits at only loses money.
/// </summary>
public sealed class ThrottleService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly MinerConfigStore _config;
    private readonly MinerService _miner;
    private readonly MinerCpuLimit _limit;
    private readonly ThrottleLog _decisions;
    private readonly ILogger<ThrottleService> _log;
    private readonly SystemLoadReader _loadReader = new();
    private readonly ThrottleLadder _ladder = new();
    private readonly LoadJournal _journal = new();
    private readonly object _gate = new();

    /// <summary>
    /// How much of the whole machine the miner takes at full speed, and the process it was worked
    /// out for. See <see cref="FullShareAsync"/>.
    ///
    /// The ladder is a share of the miner; a job object's limit is a share of the machine. They
    /// are not the same number - six mining threads on twelve logical CPUs come to about 50% - and
    /// without the conversion "hold it to 50%" is a cap the miner never reaches. Measured on a
    /// 12-thread node: pinning 50% changed the hashrate by nothing at all.
    /// </summary>
    private double? _minerFullShare;
    private int _fullSharePid;
    /// <summary>Set only for a guessed share, so a miner whose API comes back is asked again.</summary>
    private DateTimeOffset? _shareExpiresAt;

    private SystemLoad _lastLoad;
    private bool _wasEnabled;
    private DateTimeOffset _lastStartComplaint = DateTimeOffset.MinValue;
    private DateTimeOffset _lastLimitComplaint = DateTimeOffset.MinValue;

    public ThrottleService(
        MinerConfigStore config,
        MinerService miner,
        MinerCpuLimit limit,
        ThrottleLog decisions,
        ILogger<ThrottleService> log)
    {
        _config = config;
        _miner = miner;
        _limit = limit;
        _decisions = decisions;
        _log = log;
    }

    /// <summary>What the console shows: the rung, the cause, and the readings behind it.</summary>
    public ThrottleStatusDto Status()
    {
        lock (_gate)
        {
            var settings = _config.Current.Throttle;
            return new ThrottleStatusDto
            {
                Enabled = settings?.Enabled == true,
                Level = _ladder.Current,
                Reason = _ladder.Reason,
                Manual = settings?.ManualLevel is not null,
                OtherCpuPercent = _wasEnabled ? _lastLoad.OtherCpuPercent : null,
                MemoryUsedPercent = _wasEnabled ? _lastLoad.MemoryUsedPercent : null,
                SecondsAtLevel = (DateTimeOffset.UtcNow - _ladder.ChangedAt).TotalSeconds,
            };
        }
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
                // Same rule as the session monitor: this service exists to make a machine pleasant
                // to use. It must never be the reason a node stops answering the console.
                _log.LogDebug(ex, "Throttle tick failed");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var config = _config.Current;
        var settings = config.Throttle;

        // Read before deciding whether to act on it. The journal is the reason: a node with
        // throttling off used to leave this method here and record nothing, so the only machine
        // state anybody could look at afterwards was the one the remedy happened to produce.
        // Sampling is two kernel calls and a process handle - cheap enough to do unconditionally.
        var load = _loadReader.Read();
        var now = DateTimeOffset.UtcNow;

        if (settings?.Enabled != true)
        {
            RecordMinute(load, level: null, now);

            // The second half of that condition is not redundant. A node whose miner this service
            // stopped, that then restarted into a build with throttling switched off, would never
            // see _wasEnabled true and would sit there not mining with nothing to explain it.
            if (_wasEnabled || config.MinerStoppedByThrottle == true) await StandDownAsync(ct);
            return;
        }

        // No Reset when throttling is switched on any more, and none is needed: the reader is now
        // called every second either way, so the previous sample is always one tick old rather
        // than however long ago the feature was last enabled.
        _wasEnabled = true;

        int previous, level;
        string reason;

        lock (_gate)
        {
            if (load.Usable) _lastLoad = load;
            previous = _ladder.Current;

            if (settings.ManualLevel is { } pinned)
            {
                _ladder.Force(Math.Clamp(pinned, 0, 100), "pinned by the operator; automation is standing down", now);
            }
            else if (load.Usable)
            {
                _ladder.Update(
                    load.OtherCpuPercent,
                    settings.Steps is { Count: > 0 } steps ? steps : ThrottleSettingsDto.DefaultSteps,
                    Math.Clamp(settings.FloorLevel ?? 0, 0, 100),
                    Math.Max(1, settings.RampUpSeconds ?? 120),
                    now);
            }
            // An unusable sample holds the current rung rather than guessing at one. Guessing low
            // stops a miner for nothing; guessing high hands the machine back to a miner while
            // somebody is working on it. Neither is worth doing on no information.

            level = _ladder.Current;
            reason = _ladder.Reason;
        }

        RecordMinute(load, level, now);

        await ApplyAsync(level, reason, ct);

        if (level != previous)
        {
            _decisions.Record(previous, level, reason, load);
            _log.LogInformation("Throttle {From}% -> {To}% ({Reason})", previous, level, reason);
        }
    }

    /// <summary>Hands the sample to the journal, and writes the line out when a minute closes.</summary>
    private void RecordMinute(SystemLoad load, int? level, DateTimeOffset now)
    {
        if (_journal.Add(load, level, now) is { } minute) _decisions.Record(minute);
    }

    /// <summary>
    /// How much of the machine the miner takes at full speed, as a percentage.
    ///
    /// Taken from its thread count rather than from watching it, because watching does not work
    /// here: the only samples that show a miner's appetite are the ones taken while it is
    /// uncapped, and once a cap is on there are none - the reading is then the cap. A service
    /// that put a cap on early would be stuck with whatever it had guessed by then, for as long
    /// as the miner kept running.
    ///
    /// A RandomX thread sits at a full logical CPU, so threads over logical CPUs is what the
    /// miner asks for: six threads on twelve CPUs measured at almost exactly the 50% this returns.
    /// Asked of the miner once per process, over loopback.
    /// </summary>
    private async Task<double> FullShareAsync(int pid, CancellationToken ct)
    {
        if (_fullSharePid == pid && _minerFullShare is { } cached
            && (_shareExpiresAt is null || _shareExpiresAt > DateTimeOffset.UtcNow))
            return cached;

        var threads = (await _miner.GetStatusAsync(ct)).MiningThreads;
        var cores = Environment.ProcessorCount;

        if (threads is not > 0 || cores <= 0)
        {
            // The miner's API is unreachable - a miner this agent did not start holds its own
            // token, and the fleet already reports that as "mining (no api)". Capping as a share
            // of the machine is wrong, but wrong towards limiting too hard, and this service
            // exists to get out of somebody's way.
            //
            // Cached only briefly. Not caching at all would put a three-second HTTP timeout in
            // every one-second tick; caching for good would never notice the miner coming back.
            (_fullSharePid, _minerFullShare) = (pid, 100);
            _shareExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30);
            _log.LogDebug("Throttle: the miner did not report its thread count; limiting by machine share");
            return 100;
        }

        var share = Math.Clamp(100.0 * threads.Value / cores, 1, 100);
        (_fullSharePid, _minerFullShare, _shareExpiresAt) = (pid, share, null);
        _log.LogInformation("Throttle: the miner takes {Share:0}% of this machine at full speed ({Threads} threads on {Cores} CPUs)",
            share, threads.Value, cores);

        return share;
    }

    private async Task ApplyAsync(int level, string reason, CancellationToken ct)
    {
        var stoppedByUs = _config.Current.MinerStoppedByThrottle == true;
        var pid = _miner.RunningPid();

        if (level <= 0)
        {
            if (pid is null) return;

            _log.LogWarning("Throttle: stopping the miner to release its memory ({Reason})", reason);
            var result = await _miner.StopAsync(ct);
            _limit.Forget();
            // Recorded before checking success: a stop that half-worked still means this service,
            // not the operator, is why the miner is down.
            _config.Update(new MinerConfigDto { MinerStoppedByThrottle = true });
            if (!result.Ok) _log.LogWarning("Throttle could not stop the miner: {Message}", result.Message);
            return;
        }

        if (pid is null)
        {
            // Only a miner this service stopped is started again. An operator who stopped mining
            // deliberately must not find it running because the machine went quiet.
            if (!stoppedByUs) return;
            if (!await ResumeAsync(reason, ct)) return;

            pid = _miner.RunningPid();
            if (pid is null) return;
        }
        else if (stoppedByUs)
        {
            // Somebody started it by hand while the throttle had it down. Their call now.
            _config.Update(new MinerConfigDto { MinerStoppedByThrottle = false });
        }

        if (_limit.AppliedLevel == level) return;

        if (_limit.Apply(pid.Value, level, await FullShareAsync(pid.Value, ct), out var detail)) return;

        // Retried every tick rather than given up on, because the usual cause - the miner having
        // just restarted under a new pid - fixes itself. The complaint is rate limited so a node
        // that really cannot be capped says so without writing a line a second forever.
        if (DateTimeOffset.UtcNow - _lastLimitComplaint <= TimeSpan.FromMinutes(1)) return;
        _lastLimitComplaint = DateTimeOffset.UtcNow;
        _log.LogWarning("Throttle could not hold the miner at {Level}%: {Detail}", level, detail);
    }

    /// <summary>
    /// Lifts the cap on the way out, so stopping the agent does not leave a rig limited.
    ///
    /// The job object outlives this process on purpose, which means a cap left behind would keep
    /// holding the miner down with nothing running to explain it. Not reached when the agent
    /// exits abruptly - a self-update does exactly that - and that case is covered instead by the
    /// next agent re-opening the named job and taking control of the limit it inherited.
    /// </summary>
    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);

        try
        {
            if (_miner.RunningPid() is { } pid && _limit.Apply(pid, 100, 100, out var detail))
                _log.LogInformation("Throttle: {Detail} on the way out", detail);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not lift the CPU limit while stopping");
        }
    }

    /// <summary>Undoes everything this service did, so switching it off really switches it off.</summary>
    private async Task StandDownAsync(CancellationToken ct)
    {
        _wasEnabled = false;

        if (_miner.RunningPid() is { } pid) _limit.Apply(pid, 100, 100, out _);
        _ladder.Force(100, "throttling is off", DateTimeOffset.UtcNow);

        if (_config.Current.MinerStoppedByThrottle == true) await ResumeAsync("throttling is off", ct);
    }

    /// <summary>
    /// Starts the miner this service had stopped, and only then forgets that it stopped it.
    ///
    /// The order matters. Clearing the flag first and finding out afterwards that the start failed
    /// leaves a node not mining with nothing left to say who stopped it or that anything should
    /// start it again - the miner would simply be off until somebody noticed. Keeping the flag
    /// until the miner is really running costs a retry a second, which the throttled log below
    /// keeps from filling the disk.
    /// </summary>
    private async Task<bool> ResumeAsync(string reason, CancellationToken ct)
    {
        var result = await _miner.StartAsync(ct);

        if (result.Ok)
        {
            _config.Update(new MinerConfigDto { MinerStoppedByThrottle = false });
            // The miner's accumulated CPU time restarts from zero with the new process; without
            // this the next sample counts its own spin-up as somebody else's load.
            _loadReader.Reset();
            // A new process may have a different thread count, so its appetite is measured afresh.
            (_minerFullShare, _fullSharePid, _shareExpiresAt) = (null, 0, null);
            _log.LogInformation("Throttle: started the miner again ({Reason})", reason);
            return true;
        }

        if (DateTimeOffset.UtcNow - _lastStartComplaint > TimeSpan.FromMinutes(1))
        {
            _lastStartComplaint = DateTimeOffset.UtcNow;
            _log.LogWarning("Throttle cannot start the miner it stopped: {Message}", result.Message);
        }

        return false;
    }
}
