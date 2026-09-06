using System.Net.NetworkInformation;
using System.Diagnostics;
using XmrigFleet.Contracts;

namespace XmrigFleet.Agent;

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

    private readonly MinerConfigStore _config;
    private readonly GpuMinerService _miner;
    private readonly ILogger<GpuPauseService> _log;
    private readonly GpuPauseRule _rule = new();

    private bool _wasEnabled;

    public GpuPauseService(MinerConfigStore config, GpuMinerService miner, ILogger<GpuPauseService> log)
    {
        _config = config;
        _miner = miner;
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

        var busy = IsBusy(rule);
        if (busy is null)
        {
            // An unreadable observation is not a quiet one. Resuming on a failed read would put
            // the miner back onto a card somebody is waiting for, so the decision simply holds -
            // the same three-way shape SystemLoadReader uses for a load sample it cannot trust.
            return;
        }

        if (!_rule.Update(busy.Value.Busy, busy.Value.Description, rule.QuietSeconds ?? 300, DateTimeOffset.UtcNow))
        {
            _miner.Notice = _rule.Paused ? _rule.Reason : null;
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

    /// <summary>Gives the card back for good, for a node whose pause rule was removed.</summary>
    private async Task StandDownAsync(CancellationToken ct)
    {
        _wasEnabled = false;
        _rule.Clear(DateTimeOffset.UtcNow);

        if (_config.Current.GpuStoppedByPause == true)
        {
            var result = await _miner.StartAsync(ct);
            if (result.Ok) _config.Update(new MinerConfigDto { GpuStoppedByPause = false });
        }

        _miner.Notice = null;
    }

    /// <summary>
    /// Whether anything the rule watches is in use. Null means the observation could not be made
    /// and the caller must hold its decision rather than read the silence as quiet.
    ///
    /// Every condition is evaluated and any one of them stands the card down. This used to return
    /// on the first condition it found, which made a rule naming both a port and a process watch
    /// only the port - so a node that gave its card to a language model went on mining through a
    /// game, with a rule on disk that said otherwise.
    /// </summary>
    private (bool Busy, string Description)? IsBusy(GpuPauseRuleDto rule)
    {
        try
        {
            return GpuPauseRule.Evaluate(rule, OpenConnections(rule.TcpPort), RunningAmong(rule.Processes));
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.LogDebug(ex, "Could not read the pause condition");
            return null;
        }
    }

    /// <summary>
    /// Established connections to the watched port, or zero when no port is watched.
    ///
    /// Read straight from the TCP table rather than through a cmdlet or WMI: measured on
    /// mks68i7rtx, Get-NetTCPConnection returns nothing at all from a service context.
    ///
    /// Matched on the local port across every address, deliberately not on loopback. Ollama binds
    /// the node's tailnet address, and a watchdog watching 127.0.0.1 sees nothing and reports a
    /// mining duty cycle of 100% forever.
    /// </summary>
    private static int OpenConnections(int? port) =>
        port is not { } watched
            ? 0
            : IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpConnections()
                .Count(c => c.LocalEndPoint.Port == watched && c.State == TcpState.Established);

    /// <summary>
    /// Which of the watched process names are running, from one snapshot of the process table.
    ///
    /// Taken once rather than per name because this runs every second: GetProcessesByName walks
    /// every process on the machine for each call, so a rule naming four games would walk it four
    /// times over to answer one question.
    /// </summary>
    private static IReadOnlyCollection<string> RunningAmong(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return [];

        var snapshot = Process.GetProcesses();
        try
        {
            return names
                .Where(name => snapshot.Any(p => string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
        finally
        {
            foreach (var process in snapshot) process.Dispose();
        }
    }
}
