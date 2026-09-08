using XmrigFleet.Contracts;

namespace XmrigFleet.Console;

/// <summary>What the console knows about one node right now.</summary>
public sealed record NodeState(NodeConfig Node, NodeSnapshotDto? Snapshot, string? Error, DateTimeOffset PolledAt)
{
    public bool Online => Snapshot is not null;
    public bool Mining => Snapshot?.Miner.Running == true;

    /// <summary>Preferred hashrate window: 60s is steady enough to read but still current.</summary>
    public double Hashrate => Snapshot?.Miner.Hashrate60s ?? Snapshot?.Miner.Hashrate10s ?? 0;

    /// <summary>
    /// A real sensor reading wins. Otherwise the operator-configured figure beats the
    /// agent's guess, because it usually comes from an actual wall meter.
    /// </summary>
    public double PowerWatts =>
        Snapshot?.Hardware is { PowerIsMeasured: true, EstimatedPowerWatts: { } measured }
            ? measured
            : Node.PowerFallbackWatts ?? Snapshot?.Hardware.EstimatedPowerWatts ?? 0;

    public bool PowerIsMeasured => Snapshot?.Hardware.PowerIsMeasured == true;

    /// <summary>Null on an agent too old to throttle, which is why the column can be blank.</summary>
    public ThrottleStatusDto? Throttle => Snapshot?.Throttle;

    /// <summary>Null on an agent too old to mine on the GPU, which is why the columns can be absent.</summary>
    public GpuMinerStatusDto? Gpu => Snapshot?.GpuMiner;

    public bool GpuMining => Snapshot?.GpuMiner?.Running == true;

    /// <summary>
    /// As the card's own miner reports it. Deliberately never summed across nodes: 4.5 g/s of
    /// Cuckaroo29 and 62 Mh/s of NexaPoW are not the same quantity, and adding them would produce
    /// a number that means nothing.
    /// </summary>
    public double GpuHashrate => Snapshot?.GpuMiner?.Hashrate ?? 0;
}

/// <summary>Fans requests out to every enabled node and keeps the latest answer for each.</summary>
public sealed class FleetService
{
    private readonly FleetConfig _config;

    public FleetService(FleetConfig config) => _config = config;

    public IReadOnlyList<NodeConfig> EnabledNodes => _config.Nodes.Where(n => n.Enabled).ToList();

    public AgentClient CreateClient(NodeConfig node, TimeSpan? timeout = null) =>
        new(node, _config.TokenFor(node), timeout);

    public async Task<IReadOnlyList<NodeState>> PollAsync(CancellationToken ct)
    {
        var nodes = EnabledNodes;
        if (nodes.Count == 0) return [];

        var tasks = nodes.Select(node => PollOneAsync(node, ct));
        return await Task.WhenAll(tasks);
    }

    private async Task<NodeState> PollOneAsync(NodeConfig node, CancellationToken ct)
    {
        using var client = CreateClient(node);
        try
        {
            var snapshot = await client.GetStatusAsync(ct);
            return new NodeState(node, snapshot, snapshot is null ? "empty response" : null, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new NodeState(node, null, Describe(ex), DateTimeOffset.Now);
        }
    }

    /// <summary>Runs one action against every enabled node in parallel and reports each result.</summary>
    public Task<IReadOnlyList<(NodeConfig Node, CommandResultDto Result)>> ForEachAsync(
        IEnumerable<NodeConfig> nodes,
        Func<AgentClient, CancellationToken, Task<CommandResultDto?>> action,
        CancellationToken ct) =>
        ForEachAsync(nodes, (_, client, token) => action(client, token), ct);

    /// <summary>
    /// The same, for actions that need to know which node they are talking to - pushing throttle
    /// rules, say, where every node gets an answer resolved from its own overrides.
    /// </summary>
    public async Task<IReadOnlyList<(NodeConfig Node, CommandResultDto Result)>> ForEachAsync(
        IEnumerable<NodeConfig> nodes,
        Func<NodeConfig, AgentClient, CancellationToken, Task<CommandResultDto?>> action,
        CancellationToken ct)
    {
        var tasks = nodes.Select(async node =>
        {
            using var client = CreateClient(node, TimeSpan.FromSeconds(30));
            try
            {
                var result = await action(node, client, ct) ?? CommandResultDto.Failure("empty response");
                return (node, result);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return (node, CommandResultDto.Failure(Describe(ex)));
            }
        });

        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Sends each node the throttle rules resolved for it, optionally pinning a level by hand.
    ///
    /// A pinned level switches the automation off on that node until it is cleared, which is what
    /// makes a measurement possible: an A/B that the automation can move underneath it measures
    /// nothing.
    /// </summary>
    public Task<IReadOnlyList<(NodeConfig Node, CommandResultDto Result)>> PushThrottleAsync(
        IEnumerable<NodeConfig> nodes,
        int? manualLevel,
        bool clearManual,
        CancellationToken ct) =>
        ForEachAsync(nodes, async (node, client, token) =>
        {
            var settings = _config.ThrottleFor(node) with
            {
                ManualLevel = manualLevel,
                ClearManualLevel = clearManual ? true : null,
            };

            // The reserved cores ride along with the throttle: both answer "how should this miner
            // behave around the person at the machine", and an operator who syncs one and not the
            // other would find a node half-configured with nothing saying which half.
            var reserved = _config.ReservedCoresFor(node);
            var (maxCpu, maxTemp) = _config.CpuBudgetFor(node);
            var saved = await client.PutConfigAsync(
                new MinerConfigDto
                {
                    Throttle = settings,
                    ReservedCores = reserved,
                    MaxCpuPercent = maxCpu,
                    MaxCpuTemperatureC = maxTemp,
                    PauseWhile = _config.MinerPauseFor(node),
                }, token);
            if (saved is null) return CommandResultDto.Failure("empty response");

            var applied = saved.Throttle;
            if (applied is null)
                return CommandResultDto.Failure("this agent is too old to throttle; run upgrade-agents");

            // Read back rather than echoed, like every other setting: an agent too old to reserve
            // cores drops the field, and reporting the request would call that a success.
            var cores = saved.ReservedCores ?? 0;
            var note = reserved switch
            {
                0 when cores == 0 => "",
                _ when cores == reserved => $", {cores} core(s) reserved",
                _ => $", but this agent did not take the core reservation; run upgrade-agents",
            };

            // Read back, never echoed. An agent too old for these drops them, and reporting what
            // was asked for would call a node unprotected against heat a node protected against it.
            if (maxCpu is not null || maxTemp is not null)
            {
                note += (saved.MaxCpuPercent, saved.MaxCpuTemperatureC) == (maxCpu, maxTemp)
                    ? $", ceilings {Ceiling(maxCpu, maxTemp)}"
                    : ", but this agent did not take the CPU ceilings; run upgrade-agents";
            }

            if (_config.MinerPauseFor(node) is { } pause)
            {
                note += saved.PauseWhile is { } storedPause && storedPause.NamesACondition
                    ? $", stopping while {DescribeConditions(storedPause)}"
                    : ", but this agent did not take the pause rule; run upgrade-agents";

                // Named rather than assumed equal: an agent that stored a different rule from the
                // one sent is a node behaving differently from the file the operator is reading.
                if (saved.PauseWhile is { } stored && DescribeConditions(stored) != DescribeConditions(pause))
                    note += " (which is not what was sent)";
            }

            return CommandResultDto.Success(Describe(applied) + note);
        }, ct);

    /// <summary>
    /// Sends each node what its graphics card should mine: the fleet's answer with that node's
    /// exceptions already laid over it.
    ///
    /// Resolved on this machine rather than the node, like the throttle, and it matters more here.
    /// The algorithm belongs to the card - a 4 GB RX 6500 XT cannot run the Cuckaroo29 an RTX 4060
    /// earns on - so a fleet-wide instruction is only ever half an answer.
    /// </summary>
    public Task<IReadOnlyList<(NodeConfig Node, CommandResultDto Result)>> PushGpuMinerAsync(
        IEnumerable<NodeConfig> nodes,
        CancellationToken ct) =>
        ForEachAsync(nodes, async (node, client, token) =>
        {
            var settings = _config.GpuMinerFor(node);

            var saved = await client.PutConfigAsync(new MinerConfigDto { GpuMiner = settings }, token);
            if (saved is null) return CommandResultDto.Failure("empty response");

            // The read-back is the point, exactly as it is for autostart: an agent too old to know
            // the field accepts the push and drops it, and an operator told "pushed" would only
            // find out when the card sat idle.
            var applied = saved.GpuMiner;
            if (applied is null)
                return CommandResultDto.Failure("this agent is too old to mine on the GPU; run upgrade-agents");

            return CommandResultDto.Success(DescribeGpuSettings(applied));
        }, ct);

    /// <summary>
    /// Everything a stand-down rule watches, in one phrase. Every condition is named, not just the
    /// first: a node reported as "stopping while port 11434 is busy" while it also stands down for
    /// three games tells the operator nothing about the game they are asking about.
    /// </summary>
    public static string DescribeConditions(GpuPauseRuleDto rule)
    {
        var parts = new List<string>();
        if (rule.TcpPort is { } port) parts.Add($"port {port} is busy");
        parts.AddRange(rule.Processes.Select(p => $"{p} runs"));
        return parts.Count == 0 ? "nothing" : string.Join(" or ", parts);
    }

    /// <summary>The two CPU ceilings in one phrase, naming only the ones that are set.</summary>
    public static string Ceiling(int? maxCpuPercent, double? maxCpuTemperatureC)
    {
        var parts = new List<string>();
        if (maxCpuPercent is { } pct) parts.Add($"{pct}% of full speed");
        if (maxCpuTemperatureC is { } temp) parts.Add($"{temp:0.#}C");
        return parts.Count == 0 ? "none" : string.Join(" and ", parts);
    }

    /// <summary>What a node was actually left set to, in the operator's terms.</summary>
    public static string DescribeGpuSettings(GpuMinerSettingsDto settings)
    {
        var what = settings.Enabled == true
            ? $"{settings.Algorithm ?? "no algorithm"} on {settings.PoolUrl ?? "no pool"}"
            : "off";

        return settings.PauseWhile is { } pause && pause.NamesACondition
            ? $"{what}, pausing while {DescribeConditions(pause)}"
            : what;
    }

    /// <summary>
    /// Reads or sets one node's autostart, for either fan-out to run. Null reports and changes
    /// nothing; true or false sets it.
    ///
    /// A setter reports what the node actually stored rather than what it was asked for, and
    /// that read-back is the point: an agent too old to know the field accepts the push and
    /// drops it on the floor. An operator told "on" over that finds out at the next outage -
    /// the one moment the setting existed for.
    /// </summary>
    public static Func<AgentClient, CancellationToken, Task<CommandResultDto?>> AutoStartAction(bool? set) =>
        async (client, token) =>
        {
            if (set is not { } enable)
            {
                var current = await client.GetConfigAsync(token);
                return current is null
                    ? CommandResultDto.Failure("empty response")
                    : CommandResultDto.Success(DescribeAutoStart(current.AutoStartMiner));
            }

            var saved = await client.PutConfigAsync(new MinerConfigDto { AutoStartMiner = enable }, token);
            if (saved is null) return CommandResultDto.Failure("empty response");

            return saved.AutoStartMiner == enable
                ? CommandResultDto.Success(DescribeAutoStart(saved.AutoStartMiner))
                : CommandResultDto.Failure("this agent is too old to autostart; run upgrade-agents");
        };

    /// <summary>
    /// How a node's autostart setting reads to an operator. Null is an answer in its own right
    /// and not a synonym for off: nobody has told that node either way, so it is still doing
    /// whatever its own appsettings.json was installed with.
    /// </summary>
    public static string DescribeAutoStart(bool? autoStart) => autoStart switch
    {
        true => "starts mining at boot",
        false => "stays idle at boot",
        null => "unset - follows the node's appsettings.json",
    };

    private static string Describe(ThrottleSettingsDto settings)
    {
        if (settings.Enabled != true) return "throttling off";
        if (settings.ManualLevel is { } pinned) return $"pinned at {pinned}%, automation off";

        var floor = settings.FloorLevel ?? 0;
        var ramp = settings.RampUpSeconds ?? 120;
        return $"throttling on, floor {floor}%, back up after {ramp}s of quiet";
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "timeout",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "unauthorized (check the token)",
        HttpRequestException http => http.StatusCode is { } code ? $"HTTP {(int)code}" : "unreachable",
        _ => ex.Message,
    };
}
