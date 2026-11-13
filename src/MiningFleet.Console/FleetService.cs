using System.Globalization;
using MiningFleet.Contracts;

namespace MiningFleet.Console;

/// <summary>What the console knows about one node right now.</summary>
public sealed record NodeState(
    NodeConfig Node,
    NodeSnapshotDto? Snapshot,
    string? Error,
    DateTimeOffset PolledAt,
    bool Pending = false)
{
    public bool Online => Snapshot is not null;
    public bool Mining => Snapshot?.Miner.Running == true;

    /// <summary>Fleet activity includes either miner; Mining remains CPU-only for XMR economics.</summary>
    public bool AnyMining => Mining || GpuMining;

    /// <summary>Preferred hashrate window: 60s is steady enough to read but still current.</summary>
    public double Hashrate => Snapshot?.Miner.Hashrate60s ?? Snapshot?.Miner.Hashrate10s ?? 0;

    /// <summary>
    /// A real sensor reading wins. Otherwise the operator-configured figure beats the
    /// agent's guess, because it usually comes from an actual wall meter.
    /// </summary>
    public double PowerWatts => Pending
        ? 0
        : Snapshot?.Hardware is { PowerIsMeasured: true, EstimatedPowerWatts: { } measured }
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

public sealed record FleetConfigSyncReport(
    string? PulledFrom,
    IReadOnlyList<string> PushedTo,
    IReadOnlyList<string> UpToDate,
    IReadOnlyList<string> Unsupported,
    IReadOnlyList<string> Offline)
{
    public bool AnyChanged => PulledFrom is not null || PushedTo.Count > 0;
}

/// <summary>Fans requests out to every enabled node and keeps the latest answer for each.</summary>
public sealed class FleetService
{
    private readonly FleetConfig _config;
    private readonly Func<NodeConfig, TimeSpan?, AgentClient>? _clientFactory;
    private readonly SemaphoreSlim _fleetSyncLock = new(1, 1);

    public FleetService(FleetConfig config, Func<NodeConfig, TimeSpan?, AgentClient>? clientFactory = null)
    {
        _config = config;
        _clientFactory = clientFactory;
    }

    public IReadOnlyList<NodeConfig> EnabledNodes => _config.Nodes.Where(n => n.Enabled).ToList();

    public AgentClient CreateClient(NodeConfig node, TimeSpan? timeout = null) =>
        _clientFactory?.Invoke(node, timeout) ?? new AgentClient(node, _config.TokenFor(node), timeout);

    public async Task<IReadOnlyList<NodeState>> PollAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        var nodes = EnabledNodes;
        if (nodes.Count == 0) return [];

        var tasks = nodes.Select(node => PollNodeAsync(node, ct, timeout));
        return await Task.WhenAll(tasks);
    }

    public async Task<NodeState> PollNodeAsync(NodeConfig node, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var client = CreateClient(node, timeout);
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
                    ThrottleWhile = _config.ThrottleWhileFor(node),
                    GpuMiner = _config.GpuMinerFor(node),
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

    /// <summary>
    /// Reads, sets, or clears one node's electricity tariff on the agent, verifying the read-back
    /// so an older agent that drops <see cref="MinerConfigDto.PricePerKwh"/> is caught immediately.
    /// </summary>
    public static Func<AgentClient, CancellationToken, Task<CommandResultDto?>> TariffAction(
        double? set,
        bool clear,
        string currency,
        double fleetDefault) =>
        async (client, token) =>
        {
            if (clear)
            {
                var cleared = await client.PutConfigAsync(new MinerConfigDto { ClearPricePerKwh = true }, token);
                if (cleared is null) return CommandResultDto.Failure("empty response");
                return cleared.PricePerKwh is null
                    ? CommandResultDto.Success(string.Create(CultureInfo.InvariantCulture, $"cleared - uses fleet default ({fleetDefault:0.00##} {currency}/kWh)"))
                    : CommandResultDto.Failure("this agent did not clear its tariff; run upgrade-agents");
            }

            if (set is not { } wanted)
            {
                var current = await client.GetConfigAsync(token);
                return current is null
                    ? CommandResultDto.Failure("empty response")
                    : CommandResultDto.Success(DescribeTariff(current.PricePerKwh, currency, fleetDefault));
            }

            var saved = await client.PutConfigAsync(new MinerConfigDto { PricePerKwh = wanted }, token);
            if (saved is null) return CommandResultDto.Failure("empty response");

            return saved.PricePerKwh is { } actual && Math.Abs(actual - wanted) < 1e-6
                ? CommandResultDto.Success(DescribeTariff(actual, currency, fleetDefault))
                : CommandResultDto.Failure("this agent is too old to store a tariff; run upgrade-agents");
        };

    /// <summary>
    /// Pushes local <see cref="NodeConfig.PricePerKwh"/> overrides from <c>fleet.json</c> onto the
    /// agents themselves so every console querying those nodes sees the same tariff.
    /// </summary>
    public Task<IReadOnlyList<(NodeConfig Node, CommandResultDto Result)>> SyncTariffsAsync(
        IEnumerable<NodeConfig> nodes,
        CancellationToken ct) =>
        ForEachAsync(nodes, (node, client, token) =>
        {
            var currency = _config.Electricity.Currency;
            var def = _config.Electricity.PricePerKwh;
            var own = node.PricePerKwh is { } v && double.IsFinite(v) && v >= 0 ? v : (double?)null;
            return TariffAction(own, false, currency, def)(client, token);
        }, ct);

    public static string DescribeTariff(double? pricePerKwh, string currency, double fleetDefault) =>
        pricePerKwh is { } v && double.IsFinite(v) && v >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{v:0.00##} {currency}/kWh stored on agent")
            : string.Create(CultureInfo.InvariantCulture, $"unset on agent - uses fleet default ({fleetDefault:0.00##} {currency}/kWh)");

    private static string Describe(ThrottleSettingsDto settings)
    {
        if (settings.Enabled != true) return "throttling off";
        if (settings.ManualLevel is { } pinned) return $"pinned at {pinned}%, automation off";

        var floor = settings.FloorLevel ?? 0;
        var ramp = settings.RampUpSeconds ?? 120;
        return $"throttling on, floor {floor}%, back up after {ramp}s of quiet";
    }

    /// <summary>
    /// Asks every reachable enabled agent whose <c>fleet.json</c> is newer, pulls the newest
    /// copy onto this console when a peer is ahead, and pushes the newest copy to any node that
    /// is behind or has none yet.
    /// </summary>
    public async Task<FleetConfigSyncReport> SyncFleetConfigAsync(
        bool forcePushLocal = false,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        await _fleetSyncLock.WaitAsync(ct);
        try
        {
            _config.RefreshFromDiskIfChanged();
            var nodes = EnabledNodes;
            if (nodes.Count == 0)
                return new FleetConfigSyncReport(null, [], [], [], []);

            var probeTimeout = timeout ?? TimeSpan.FromSeconds(4);
            var probes = await Task.WhenAll(nodes.Select(async node =>
            {
                using var client = CreateClient(node, probeTimeout);
                try
                {
                    var (supported, meta) = await client.GetFleetConfigMetaAsync(ct);
                    return (Node: node, Online: true, Supported: supported, Meta: meta);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    return (Node: node, Online: false, Supported: false, Meta: (FleetConfigMetaDto?)null);
                }
            }));

            string? pulledFrom = null;
            if (!forcePushLocal)
            {
                var newerCandidates = probes
                    .Where(p => p.Online && p.Supported && p.Meta is not null && FleetConfigSync.IsNewer(p.Meta, _config.SyncMeta))
                    .OrderByDescending(p => p.Meta!.UpdatedAtUtc)
                    .ThenByDescending(p => p.Meta!.Sha256, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var winner in newerCandidates)
                {
                    using var client = CreateClient(winner.Node, probeTimeout);
                    try
                    {
                        var remote = await client.GetFleetConfigAsync(ct);
                        if (remote is not null && _config.ApplySynced(remote.Json, remote.UpdatedAtUtc, remote.OriginHost ?? winner.Node.Name))
                        {
                            pulledFrom = winner.Node.Name;
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                    {
                    }
                }
            }

            var envelope = _config.ToSyncEnvelope();
            var currentMeta = new FleetConfigMetaDto(envelope.UpdatedAtUtc, envelope.Sha256, envelope.OriginHost);

            var pushedTo = new List<string>();
            var upToDate = new List<string>();
            var unsupported = new List<string>();
            var offline = new List<string>();

            var probedByEndpoint = probes.ToDictionary(p => p.Node.Endpoint, StringComparer.OrdinalIgnoreCase);
            var currentNodes = EnabledNodes;
            var pushTargets = new List<NodeConfig>();

            foreach (var node in currentNodes)
            {
                if (!probedByEndpoint.TryGetValue(node.Endpoint, out var probe))
                {
                    using var client = CreateClient(node, probeTimeout);
                    try
                    {
                        var (supported, meta) = await client.GetFleetConfigMetaAsync(ct);
                        probe = (node, true, supported, meta);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                    {
                        probe = (node, false, false, null);
                    }
                }

                if (!probe.Online)
                {
                    offline.Add(node.Name);
                    continue;
                }

                if (!probe.Supported)
                {
                    unsupported.Add(node.Name);
                    continue;
                }

                var shouldPush = probe.Meta is null
                                 || FleetConfigSync.IsNewer(currentMeta, probe.Meta)
                                 || (forcePushLocal && !string.Equals(probe.Meta.Sha256, currentMeta.Sha256, StringComparison.OrdinalIgnoreCase));

                if (shouldPush)
                    pushTargets.Add(node);
                else
                    upToDate.Add(node.Name);
            }

            if (pushTargets.Count > 0)
            {
                var pushResults = await Task.WhenAll(pushTargets.Select(async node =>
                {
                    using var client = CreateClient(node, probeTimeout);
                    try
                    {
                        var res = await client.PutFleetConfigAsync(envelope, ct);
                        return (Node: node, Result: res);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                    {
                        return (Node: node, Result: (FleetConfigPushResultDto?)null);
                    }
                }));

                foreach (var (node, result) in pushResults)
                {
                    if (result is { Accepted: true, Status: "updated" })
                        pushedTo.Add(node.Name);
                    else if (result is { Accepted: true, Status: "unchanged" })
                        upToDate.Add(node.Name);
                    else if (result is null)
                        offline.Add(node.Name);
                }
            }

            return new FleetConfigSyncReport(pulledFrom, pushedTo, upToDate, unsupported, offline);
        }
        finally
        {
            _fleetSyncLock.Release();
        }
    }

    /// <summary>
    /// Inspects <c>fleet.json</c> metadata already returned in <see cref="NodeSnapshotDto"/> during
    /// dashboard polling and runs a full sync only when any machine is ahead or behind.
    /// </summary>
    public async Task<FleetConfigSyncReport?> ObserveSnapshotsForSyncAsync(IReadOnlyList<NodeState> states, CancellationToken ct)
    {
        var localEdited = _config.RefreshFromDiskIfChanged();
        var localMeta = _config.SyncMeta;

        var needsSync = localEdited;
        if (!needsSync)
        {
            foreach (var state in states)
            {
                var remoteMeta = state.Snapshot?.FleetConfig;
                if (remoteMeta is null) continue;
                if (FleetConfigSync.IsNewer(remoteMeta, localMeta) || FleetConfigSync.IsNewer(localMeta, remoteMeta))
                {
                    needsSync = true;
                    break;
                }
            }
        }

        if (!needsSync) return null;
        return await SyncFleetConfigAsync(forcePushLocal: false, ct, TimeSpan.FromSeconds(4));
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "timeout",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "unauthorized (check the token)",
        HttpRequestException http => http.StatusCode is { } code ? $"HTTP {(int)code}" : "unreachable",
        _ => ex.Message,
    };
}
