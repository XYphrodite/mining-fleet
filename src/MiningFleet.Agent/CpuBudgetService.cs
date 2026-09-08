namespace MiningFleet.Agent;

/// <summary>
/// Holds the miner to the operator's two ceilings — how much of the node it may use, and how hot
/// the CPU may get — by changing how many RandomX threads it runs.
///
/// Both settings drive one actuator on purpose. A percentage that meant one thing and a
/// temperature that meant another would need a rule for what happens when they disagree; as two
/// bounds on the same number the answer is simply the lower of them, and there is nothing to
/// resolve.
///
/// Separate from <see cref="ThrottleService"/> because they answer different questions with
/// different urgency. The throttle asks whether somebody is using the machine right now and reacts
/// in a second; this asks what the machine may sustain, and reacts over minutes. Sharing a loop
/// would force one of those cadences onto the other.
/// </summary>
public sealed class CpuBudgetService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private readonly MinerConfigStore _config;
    private readonly MinerService _miner;
    private readonly HardwareService _hardware;
    private readonly ThrottleLog _log;
    private readonly ILogger<CpuBudgetService> _logger;
    private readonly ThermalGovernor _governor = new();

    private readonly IReadOnlyList<int> _miningCpus;
    private readonly int _fullThreads;

    public CpuBudgetService(
        MinerConfigStore config,
        MinerService miner,
        HardwareService hardware,
        ThrottleLog log,
        ILogger<CpuBudgetService> logger)
    {
        _config = config;
        _miner = miner;
        _hardware = hardware;
        _log = log;
        _logger = logger;

        var cores = CpuReservation.PhysicalCores();
        _miningCpus = CpuReservation.MiningCpus(cores);
        _fullThreads = CpuReservation.FullThreadCount(cores.Count, CpuReservation.L3CacheBytes());
    }

    /// <summary>What this node runs at when nothing is holding it back. Zero when unknown.</summary>
    public int FullThreads => _fullThreads;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_fullThreads <= 0 || _miningCpus.Count == 0)
        {
            _logger.LogInformation(
                "CPU budget: this machine did not report a usable core topology, so neither the load ceiling nor the temperature ceiling can be applied.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // The rule the throttle, the GPU pause and the reservation all follow: a service
                // that exists to make a machine behave must never be why a node stops answering.
                _logger.LogDebug(ex, "CPU budget tick failed");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var config = _config.Current;
        if (config.MaxCpuPercent is null && config.MaxCpuTemperatureC is null) return;

        var status = await _miner.GetStatusAsync(ct);
        if (!status.Running || status.MiningThreads is not { } current || current <= 0) return;

        var ceiling = Math.Min(CpuReservation.ThreadsFor(_fullThreads, config.MaxCpuPercent), _miningCpus.Count);

        // Read only when there is a temperature rule to apply it to. LibreHardwareMonitor is not
        // free, and a node with only a percentage ceiling has nothing to learn from a sensor.
        double? temperature = null;
        if (config.MaxCpuTemperatureC is not null)
            temperature = (await _hardware.ReadAsync(ct)).CpuTemperatureC;

        var wanted = Math.Clamp(
            _governor.Decide(current, ceiling, temperature, config.MaxCpuTemperatureC, DateTimeOffset.UtcNow),
            1,
            ceiling);

        if (wanted == current) return;

        var failure = await _miner.ApplyThreadsAsync(wanted, _miningCpus, ct);
        if (failure is not null)
        {
            _logger.LogWarning("CPU budget: could not move the miner to {Threads} thread(s): {Reason}", wanted, failure);
            return;
        }

        _log.RecordThreads(current, wanted, temperature, _governor.Reason);
        _logger.LogInformation(
            "CPU budget: miner moved from {From} to {To} thread(s) of {Full}. {Reason}",
            current, wanted, _fullThreads, _governor.Reason);
    }
}
