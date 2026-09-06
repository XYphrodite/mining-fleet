using System.Text.Json.Serialization;

namespace XmrigFleet.Contracts;

/// <summary>Contract version, sent by the agent so the console can warn about mismatches.</summary>
public static class ApiVersion
{
    public const string Current = "1";
}

public sealed record AgentInfoDto(
    string Hostname,
    string OperatingSystem,
    string AgentVersion,
    string ApiVersion,
    double AgentUptimeSeconds,
    bool IsElevated);

public sealed record MinerStatusDto
{
    public bool Installed { get; init; }
    public bool Running { get; init; }
    public int? Pid { get; init; }
    public string? ExecutablePath { get; init; }
    public string? Version { get; init; }
    public string? Algorithm { get; init; }
    public string? PoolUrl { get; init; }
    public string? Wallet { get; init; }
    public string? WorkerName { get; init; }
    public double UptimeSeconds { get; init; }
    /// <summary>Hashrate in H/s over the last 10s / 60s / 15m windows, as reported by the xmrig HTTP API.</summary>
    public double? Hashrate10s { get; init; }
    public double? Hashrate60s { get; init; }
    public double? Hashrate15m { get; init; }
    public double? HashrateHighest { get; init; }
    public long SharesGood { get; init; }
    public long SharesTotal { get; init; }
    public long PoolDifficulty { get; init; }
    public double? PingMs { get; init; }
    /// <summary>Set when the agent could not reach the xmrig HTTP API even though the process is alive.</summary>
    public string? ApiError { get; init; }

    // RandomX throughput is decided almost entirely by these, not by CPU model: a node that
    // fails to get its huge pages runs several times slower with no other symptom. Reporting
    // them turns "why is this node slow" from guesswork into a reading.

    /// <summary>Huge pages xmrig actually obtained for the RandomX dataset and caches.</summary>
    public int? HugePagesAllocated { get; init; }
    /// <summary>Huge pages xmrig asked for. Equal to <see cref="HugePagesAllocated"/> on a healthy node.</summary>
    public int? HugePagesTotal { get; init; }
    /// <summary>Threads the CPU backend is actually mining with (not the logical CPU count).</summary>
    public int? MiningThreads { get; init; }
    /// <summary>xmrig's MSR mod state, e.g. "intel", "ryzen_19h", or null when it could not be applied.</summary>
    public string? MsrMod { get; init; }
    /// <summary>Assembly optimisation xmrig selected, e.g. "intel", "ryzen".</summary>
    public string? Assembly { get; init; }

    /// <summary>Fraction of requested huge pages that were granted, or null when unknown.</summary>
    public double? HugePagesPercent =>
        HugePagesTotal is > 0 && HugePagesAllocated is { } got ? (double)got / HugePagesTotal.Value : null;
}

public sealed record SensorDto(string Component, string Name, string Kind, double Value, string Unit);

public sealed record GpuDto(string Name, double? TemperatureC, double? LoadPercent, double? PowerWatts, double? MemoryUsedMb, double? MemoryTotalMb);

public sealed record HardwareDto
{
    public string CpuName { get; init; } = "unknown";
    public int PhysicalCores { get; init; }
    public int LogicalCores { get; init; }
    public double? CpuTemperatureC { get; init; }
    public double? CpuLoadPercent { get; init; }
    public double? CpuPowerWatts { get; init; }
    public double MemoryTotalGb { get; init; }
    public double MemoryUsedGb { get; init; }
    public string? MotherBoard { get; init; }
    public IReadOnlyList<GpuDto> Gpus { get; init; } = [];
    public IReadOnlyList<SensorDto> Sensors { get; init; } = [];
    /// <summary>Best-effort whole-machine draw. Falls back to the node's configured estimate when no sensor exists.</summary>
    public double? EstimatedPowerWatts { get; init; }
    public bool PowerIsMeasured { get; init; }

    /// <summary>Explains why sensors are missing when the agent can tell, e.g. a blocked ring0 driver.</summary>
    public string? SensorNotice { get; init; }
}

public sealed record NodeSnapshotDto(AgentInfoDto Agent, MinerStatusDto Miner, HardwareDto Hardware)
{
    /// <summary>Null on an agent too old to throttle, which is how the console tells them apart.</summary>
    public ThrottleStatusDto? Throttle { get; init; }

    /// <summary>
    /// What the session monitor last did, in the operator's terms — which window is open, or why
    /// none is. Present for the same reason as <see cref="HardwareDto.SensorNotice"/>: the console
    /// used to report "session monitor on" whatever had actually happened on the node, so a rig
    /// could sit at 60% of its hashrate with the setting reading on and nothing to say otherwise.
    /// </summary>
    public string? MonitorNotice { get; init; }

    /// <summary>Null on an agent too old to mine on the GPU, which is how the console tells them apart.</summary>
    public GpuMinerStatusDto? GpuMiner { get; init; }
}

public sealed record CommandResultDto(bool Ok, string Message)
{
    public static CommandResultDto Success(string message) => new(true, message);
    public static CommandResultDto Failure(string message) => new(false, message);
}

public sealed record MinerConfigDto
{
    public string? ExecutablePath { get; init; }
    public string? ConfigPath { get; init; }
    public string? PoolUrl { get; init; }
    public string? Wallet { get; init; }
    public string? WorkerName { get; init; }
    public string? Password { get; init; }
    public string[]? ExtraArgs { get; init; }
    /// <summary>Watts to assume for this machine when no power sensor is available.</summary>
    public double? PowerFallbackWatts { get; init; }

    /// <summary>
    /// Keep a monitor window - Resource Monitor by preference - running hidden in the node's
    /// interactive session.
    ///
    /// This is a remedy without a diagnosis, and it is worth saying so plainly: on an
    /// i7-12700KF the miner runs at 4,380 H/s with nothing watching and 7,092 H/s with Task
    /// Manager open - a 62% difference that survived eleven attempts to explain it away.
    /// Whatever Windows does differently, it needs a monitor window in a logged-on session;
    /// polling the same counters from the agent's own service achieved nothing.
    ///
    /// Null leaves the node's current setting alone, which is what a pool-settings push sends.
    /// </summary>
    public bool? KeepMonitorOpen { get; init; }

    /// <summary>
    /// Start mining as soon as the agent starts, so a node that came back on its own - after a
    /// mains failure, after a bugcheck, after a service restart - returns to work without
    /// anybody signing in to tell it to.
    ///
    /// Null leaves the node's current setting alone, which is what a pool-settings push sends.
    /// A node nobody has ever told falls back to <c>Agent:AutoStartMiner</c> in its own
    /// appsettings.json, so a fresh install still behaves the way it was installed.
    /// </summary>
    public bool? AutoStartMiner { get; init; }

    /// <summary>
    /// How hard the miner may run while the machine is in use. Null leaves the node alone.
    /// </summary>
    public ThrottleSettingsDto? Throttle { get; init; }

    /// <summary>
    /// Physical cores kept out of the miner's hands for whoever is sitting at the machine.
    ///
    /// Unlike the throttle this is not a response to load, and deliberately so: the load a person
    /// generates is the thing being suppressed. Measured on `mks68i7rtx` while a game froze — the
    /// miner held one thread of every P-core and all four E-cores, and the node's own journal read
    /// `other avg=8.4%`, because the game could not get scheduled enough to consume more. A ladder
    /// watching that figure would never have moved.
    ///
    /// 0 or null reserves nothing, which is right for a rig nobody sits at. Two cores cost 7% of
    /// the hashrate on that node and stopped the stutter.
    /// </summary>
    public int? ReservedCores { get; init; }

    /// <summary>
    /// True when the miner is stopped because the throttle took it to zero, rather than because
    /// an operator stopped it.
    ///
    /// Persisted because the agent restarts often - every self-update does - and the distinction
    /// cannot be recovered afterwards. Without it a node stopped by the throttle either stays
    /// down forever, or the agent starts mining that nobody asked for. Neither is acceptable.
    /// </summary>
    public bool? MinerStoppedByThrottle { get; init; }

    /// <summary>
    /// What this node's graphics card mines, if anything. Null leaves the node alone.
    ///
    /// Separate from the fields above rather than folded into them, because the two miners share
    /// nothing: a different executable, a different pool, usually a different coin, and an
    /// algorithm that is a property of the card rather than of the fleet.
    /// </summary>
    public GpuMinerSettingsDto? GpuMiner { get; init; }

    /// <summary>
    /// True when the GPU miner is stopped because the pause rule took the card, rather than
    /// because an operator stopped it.
    ///
    /// Persisted for the same reason as <see cref="MinerStoppedByThrottle"/>: the agent restarts
    /// often and the distinction cannot be recovered afterwards. Without it a paused node either
    /// never mines again, or starts mining over somebody's shoulder.
    /// </summary>
    public bool? GpuStoppedByPause { get; init; }
}

/// <summary>
/// What the node's graphics card mines, and under what conditions it gives the card back.
///
/// Every field is per-node in practice even though the console resolves a fleet-wide default
/// first, because the right algorithm is a property of the card: an RTX 4060 earns 49 ₽/day on
/// Cuckaroo29 while a 4 GB RX 6500 XT cannot run that algorithm at all and settles for NexaPoW at
/// about a rouble and a half.
/// </summary>
public sealed record GpuMinerSettingsDto
{
    /// <summary>Off by default: a node only mines on its GPU once an operator asks it to.</summary>
    public bool? Enabled { get; init; }

    /// <summary>lolMiner's algorithm name, e.g. <c>CR29</c> or <c>NEXA</c>. Case is passed through.</summary>
    public string? Algorithm { get; init; }

    /// <summary>host:port of the mining pool.</summary>
    public string? PoolUrl { get; init; }

    /// <summary>
    /// The pool login, whole and already formatted, because pools disagree about its shape:
    /// unMineable wants <c>XMR:address.worker</c> and Kryptex wants <c>address/worker</c>.
    /// Building it here would mean teaching the agent every pool's dialect.
    /// </summary>
    public string? User { get; init; }

    public string? Password { get; init; }

    /// <summary>Where lolMiner lives on the node. Written by the installer; null means not installed.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Loopback port for lolMiner's own HTTP API, which is where hashrate and share counts come
    /// from. Deliberately not the xmrig API port: two miners answering on one port is a bug that
    /// only shows up when both happen to be running.
    /// </summary>
    public int? ApiPort { get; init; }

    /// <summary>
    /// Launch the miner into the node's logged-on session instead of the agent's own session 0.
    ///
    /// Needed on some machines and not others, and the difference is not explained. On
    /// mks68i7rtx lolMiner started from session 0 initialises both its backends and then stops
    /// dead, never reaching worker-thread init; the identical command in the logged-on session
    /// mines normally. On desktop-ib88isg session 0 is fine. Left null the agent uses session 0,
    /// which is the cheaper path and works everywhere it works.
    /// </summary>
    public bool? RunInInteractiveSession { get; init; }

    /// <summary>
    /// When to give the card back to whoever is using the machine. Null means never.
    /// </summary>
    public GpuPauseRuleDto? PauseWhile { get; init; }
}

/// <summary>
/// The conditions under which GPU mining stands down, and how long it waits before returning.
///
/// Deliberately expressed as ports and processes rather than as "Ollama", which is only the case
/// that prompted it. A local model, a game and a render all want the same thing from the miner.
///
/// Every condition named is watched, and any one of them is enough: a node that hands the card to
/// a language model must not thereby stop handing it to a game. The rule was once one condition
/// or the other, which read as a choice and behaved as a silent one - a rule naming a port never
/// looked at its own process name at all.
/// </summary>
public sealed record GpuPauseRuleDto
{
    /// <summary>
    /// Stand down while anything holds an established TCP connection to this local port.
    ///
    /// A connection, not a loaded model: a language model sits in video memory for twenty minutes
    /// after one question and costs no GPU time while it does, so pausing on residency would give
    /// up most of the day's mining for nothing. A request holds its connection open for exactly as
    /// long as it needs the card.
    /// </summary>
    public int? TcpPort { get; init; }

    /// <summary>
    /// Stand down while a process of this name is running. No extension, as Windows reports it.
    ///
    /// Kept beside <see cref="ProcessNames"/> rather than replaced by it because it is already
    /// written into nodes' miner.json and operators' fleet.json, and a field that quietly stops
    /// being read is how a rig ends up mining through a game with nothing to say why.
    /// </summary>
    public string? ProcessName { get; init; }

    /// <summary>
    /// Stand down while any process on this list is running. No extensions, as Windows reports
    /// the names.
    ///
    /// A list because "somebody is using this card" is inherently plural: today's game is not
    /// tomorrow's, and naming one at a time means editing a node's config per title.
    /// </summary>
    public IReadOnlyList<string>? ProcessNames { get; init; }

    /// <summary>
    /// Seconds of quiet before mining resumes. Standing down is immediate.
    ///
    /// The asymmetry matches the CPU throttle's, and for the same reason: a conversation is a
    /// burst of requests with pauses in it, and restarting the miner between two questions both
    /// wastes the restart and slows the next answer.
    /// </summary>
    public int? QuietSeconds { get; init; }

    /// <summary>
    /// Every process this rule watches, with the singular field folded in, blanks dropped and
    /// duplicates removed. Computed in the contract so the agent that reads the rule and the
    /// console that writes it cannot disagree about what a rule containing both fields means.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Processes =>
        (ProcessNames ?? [])
            .Append(ProcessName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Whether the rule has anything at all to watch. A rule with only a quiet time in it would
    /// stand a node down with nothing able to wake it, so both sides check before storing one.
    /// </summary>
    [JsonIgnore]
    public bool NamesACondition => TcpPort is not null || Processes.Count > 0;
}

/// <summary>What the GPU miner is doing right now, so an idle card is never a mystery.</summary>
public sealed record GpuMinerStatusDto
{
    public bool Running { get; init; }
    public string? Algorithm { get; init; }
    public string? Pool { get; init; }

    /// <summary>
    /// As the miner reports it, with its own unit alongside: algorithms are not comparable by
    /// this number. 4.5 g/s of Cuckaroo29 out-earns 62 Mh/s of NexaPoW twelve times over.
    /// </summary>
    public double? Hashrate { get; init; }
    public string? HashrateUnit { get; init; }

    public int? AcceptedShares { get; init; }

    /// <summary>
    /// Shares the pool received too late to pay for. Worth a column of its own: a stale rate of
    /// 18% was how a mis-set process priority announced itself, and nothing else showed it.
    /// </summary>
    public int? StaleShares { get; init; }
    public int? RejectedShares { get; init; }

    public IReadOnlyList<GpuDeviceStatusDto> Devices { get; init; } = [];

    /// <summary>
    /// Why the miner is not running, when it is not. "paused - port 11434 busy" and "stopped by
    /// the operator" are different answers and the console must not print one for the other.
    /// </summary>
    public string? Notice { get; init; }
}

/// <summary>One card as the GPU miner sees it, which is not always what the sensors see.</summary>
public sealed record GpuDeviceStatusDto(
    string Name,
    double? Hashrate,
    double? TemperatureC,
    double? FanPercent,
    double? CoreClockMhz,
    double? MemoryClockMhz);

/// <summary>
/// One rung of the throttle ladder: at or above <see cref="OtherCpuPercent"/> of CPU used by
/// everything except the miner, the miner is held to <see cref="Level"/> percent.
/// </summary>
public sealed record ThrottleStepDto(double OtherCpuPercent, int Level);

/// <summary>
/// How hard the miner may run while somebody is using the machine.
///
/// The ladder is deliberately coarse. A continuous curve tracks every twitch of background
/// activity and spends its life re-applying a limit nobody asked for; five rungs are enough to
/// keep a machine responsive and are legible in the console and in the decision log.
/// </summary>
public sealed record ThrottleSettingsDto
{
    /// <summary>Off by default: a node only throttles once an operator asks it to.</summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// The ladder, lowest threshold first. Null keeps the node's current ladder; the agent
    /// falls back to <see cref="DefaultSteps"/> when it has never been given one.
    /// </summary>
    public IReadOnlyList<ThrottleStepDto>? Steps { get; init; }

    /// <summary>Never drop below this level, whatever the ladder says. 0 allows a full stop.</summary>
    public int? FloorLevel { get; init; }

    /// <summary>
    /// Seconds of quiet before the miner is allowed back up a rung. Coming down is immediate.
    ///
    /// The asymmetry is the whole point: interrupting somebody costs more than two minutes of
    /// hashing, and it also stops a single burst - opening a folder, a browser tab - from
    /// rocking the miner up and down.
    /// </summary>
    public int? RampUpSeconds { get; init; }

    /// <summary>
    /// A level the operator pinned by hand, which switches the automation off until cleared.
    /// Use <see cref="ClearManualLevel"/> to hand control back; null here means "leave as is".
    /// </summary>
    public int? ManualLevel { get; init; }

    /// <summary>Hands control back to the automation, clearing <see cref="ManualLevel"/>.</summary>
    public bool? ClearManualLevel { get; init; }

    /// <summary>
    /// Where the ladder starts before anybody tunes it. These thresholds are a guess and are
    /// meant to be corrected from the node's own decision log, not defended.
    /// </summary>
    public static IReadOnlyList<ThrottleStepDto> DefaultSteps =>
    [
        new(0, 100),
        new(10, 75),
        new(25, 50),
        new(45, 25),
        new(70, 0),
    ];
}

/// <summary>What the throttle is doing right now and why, so a slow node is never a mystery.</summary>
public sealed record ThrottleStatusDto
{
    public bool Enabled { get; init; }
    /// <summary>0-100. Below 100 the miner is capped; at 0 it is stopped and its memory released.</summary>
    public int Level { get; init; }
    /// <summary>Plain-language cause, e.g. "other processes at 52% CPU". Shown in the console.</summary>
    public string Reason { get; init; } = "";
    /// <summary>True when an operator pinned the level and the automation is standing down.</summary>
    public bool Manual { get; init; }
    /// <summary>CPU used by everything except the miner, which is what the ladder is read against.</summary>
    public double? OtherCpuPercent { get; init; }
    public double? MemoryUsedPercent { get; init; }
    /// <summary>Seconds the miner has been held at <see cref="Level"/>.</summary>
    public double SecondsAtLevel { get; init; }
}

/// <summary>Install or update xmrig. Either give an explicit <see cref="DownloadUrl"/> or a GitHub release tag.</summary>
public sealed record InstallRequestDto
{
    /// <summary>Directory xmrig should be installed into, e.g. C:\mining\xmrig.</summary>
    public string TargetPath { get; init; } = "";
    /// <summary>GitHub release tag such as "v6.22.2", or null/"latest" for the newest release.</summary>
    public string? Version { get; init; }
    /// <summary>Overrides <see cref="Version"/> when set: a direct .zip / .tar.gz URL.</summary>
    public string? DownloadUrl { get; init; }
    /// <summary>Stop a running miner, install, then start it again.</summary>
    public bool RestartAfterInstall { get; init; } = true;
}

public sealed record InstallResultDto(bool Ok, string Message, string? InstalledVersion, string? ExecutablePath);

/// <summary>Update the agent itself from a published xmrig-fleet release.</summary>
public sealed record AgentUpdateRequestDto
{
    /// <summary>Release tag such as "v1.4.0", or null/"latest" for the newest.</summary>
    public string? Version { get; init; }
    /// <summary>Overrides <see cref="Version"/> when set: a direct .zip URL.</summary>
    public string? DownloadUrl { get; init; }
    /// <summary>Reinstall even when the node already runs that version.</summary>
    public bool Force { get; init; }
}

public sealed record AgentUpdateResultDto(
    bool Ok,
    string Message,
    string? FromVersion,
    string? ToVersion,
    /// <summary>True when the agent swapped itself and is about to exit for the service manager to restart it.</summary>
    bool Restarting);

public sealed record LogTailDto(string Source, IReadOnlyList<string> Lines);
