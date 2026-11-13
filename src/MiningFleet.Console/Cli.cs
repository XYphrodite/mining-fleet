using System.Globalization;
using Spectre.Console;
using MiningFleet.Console.Ui;
using MiningFleet.Contracts;

namespace MiningFleet.Console;

/// <summary>
/// One-shot commands for scripts and scheduled tasks. Everything here renders once and
/// exits, so it works fine with redirected output where the interactive menu cannot run.
/// </summary>
public static class Cli
{
    public const string Usage = """
        mining-fleet [command] [node ...]

          (no command)   interactive console
          dashboard      open the live fleet dashboard directly
          status         print the fleet table once
          start          start mining
          stop           stop mining
          restart        restart mining
          economics      cost, income and profit summary
          pool           pool and wallet balance
          update         download and install a newer mining-fleet
          upgrade-agents update the agent on the nodes themselves
          throttle       show, push or pin the mining power limit
          autostart      show or set whether a node mines as soon as it boots
          tariff         show, set or clear the electricity tariff on each node
          sync           sync fleet.json with peer agents (pulls newest, pushes to lagging)
          version        print the running version
          help           this text

        Commands that take nodes act on every enabled node when none are named.

        throttle [node ...] [--set=N | --auto | --sync] [--log]
          no options   report the rung each node is on and why
          --sync       push the rules from fleet.json, keeping the automation in charge
          --set=N      pin N percent (0-100) and stand the automation down
          --auto       hand control back to the automation
          --log        print the node's own record: one line a minute of what the CPU was
                       doing, whether or not throttling is on, plus every rung change

        autostart [node ...] [--on | --off]
          no options   report what each node does when it boots
          --on         mine as soon as the agent starts, with nobody signed in
          --off        wait to be told, which is how a fresh node behaves

        gpu [node ...] [--sync | --start | --stop]
          no options   report what each card is mining, and its share quality
          --sync       push the algorithm, pool and pause rule from fleet.json
          --start      start GPU mining
          --stop       stop it, without touching the CPU miner

        tariff [node ...] [--set=N | --default | --sync]
          no options   query each node's agent and report its electricity tariff
          --set=N      store N (price per kWh) on the node's agent
          --default    clear the node's stored tariff so it uses the fleet default
          --sync       push local per-node tariffs from fleet.json onto the agents
        """;

    public static bool TryRunConfigFree(string[] args, out int exitCode)
    {
        if (args.Length == 0)
        {
            exitCode = 0;
            return false;
        }

        var command = args[0].ToLowerInvariant().TrimStart('-', '/');
        switch (command)
        {
            case "version" or "v":
                AnsiConsole.WriteLine($"mining-fleet {UpdateService.CurrentVersion}");
                exitCode = 0;
                return true;

            case "help" or "h" or "?":
                AnsiConsole.WriteLine(Usage);
                exitCode = 0;
                return true;

            default:
                exitCode = 0;
                return false;
        }
    }

    public static async Task<int> RunAsync(string[] args, FleetConfig config, FleetService fleet, MarketService market, CancellationToken ct)
    {
        var command = args[0].ToLowerInvariant().TrimStart('-');
        var names = args.Skip(1).ToArray();

        switch (command)
        {
            case "dashboard" or "live":
                if (System.Console.IsOutputRedirected || System.Console.IsInputRedirected)
                    return await StatusAsync(config, fleet, ct);
                using (var liveDashboard = new Dashboard(config, fleet, market))
                    await liveDashboard.ShowAsync(ct);
                return 0;

            case "status":
                return await StatusAsync(config, fleet, ct);

            case "start":
                return await ControlAsync(config, fleet, names, (c, t) => c.StartAsync(t), ct);

            case "stop":
                return await ControlAsync(config, fleet, names, (c, t) => c.StopAsync(t), ct);

            case "restart":
                return await ControlAsync(config, fleet, names, (c, t) => c.RestartAsync(t), ct);

            case "economics":
                await new EconomicsScreenReport(config, fleet, market).WriteAsync(ct);
                return 0;

            case "pool":
                await new PoolReport(config, market).WriteAsync(ct);
                return 0;

            case "update":
                return await Updater.RunAsync(config, names.Contains("--check") || names.Contains("check"), ct);

            case "upgrade-agents" or "upgrade-agent":
                return await UpgradeAgentsAsync(config, fleet, names, ct);

            case "throttle":
                return await ThrottleAsync(config, fleet, names, ct);

            case "autostart":
                return await AutoStartAsync(config, fleet, names, ct);

            case "tariff" or "tariffs":
                return await TariffAsync(config, fleet, names, ct);

            case "gpu":
                return await GpuAsync(config, fleet, names, ct);

            case "sync":
                return await SyncFleetConfigCliAsync(fleet, names, ct);

            case "version" or "--version" or "v":
                AnsiConsole.WriteLine($"mining-fleet {UpdateService.CurrentVersion}");
                return 0;

            case "help" or "h" or "?":
                AnsiConsole.WriteLine(Usage);
                return 0;

            default:
                AnsiConsole.MarkupLine($"[red]Unknown command '{Markup.Escape(command)}'.[/]");
                AnsiConsole.WriteLine(Usage);
                return 2;
        }
    }

    private static async Task<int> StatusAsync(FleetConfig config, FleetService fleet, CancellationToken ct)
    {
        var states = await fleet.PollAsync(ct, TimeSpan.FromSeconds(4));
        if (states.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No enabled nodes configured.[/]");
            return 1;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Node").AddColumn("State")
            .AddColumn(new TableColumn("Hashrate").RightAligned())
            .AddColumn(new TableColumn("Pages").RightAligned())
            .AddColumn(new TableColumn("Temp").RightAligned())
            .AddColumn(new TableColumn("Watts").RightAligned())
            .AddColumn(new TableColumn("Uptime").RightAligned());

        foreach (var state in states.OrderBy(s => s.Node.Name, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(
                new Markup(UiHelpers.Escape(state.Node.Name)),
                new Markup(UiHelpers.StatusBadge(state)),
                new Markup(state.Hashrate > 0 ? Economics.FormatHashrate(state.Hashrate) : "[grey]-[/]"),
                new Markup(UiHelpers.HugePages(state.Snapshot?.Miner)),
                new Markup(UiHelpers.TemperaturePair(state.Snapshot?.Hardware)),
                new Markup(state.PowerWatts > 0 ? $"{state.PowerWatts:0}" : "[grey]-[/]"),
                new Markup(state.Snapshot?.Miner is { Running: true } m ? Economics.FormatDuration(m.UptimeSeconds) : "[grey]-[/]"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[grey]{states.Count(s => s.AnyMining)} mining, {states.Count(s => s.Online)} online, {states.Count} total, " +
            $"{Economics.FormatHashrate(states.Where(s => s.Mining).Sum(s => s.Hashrate))} total[/]");

        // Non-zero exit when something is wrong, so a scheduled task can alert on it.
        return states.Any(s => !s.Online) ? 1 : 0;
    }

    /// <summary>
    /// Rolls the agent binary out to the nodes. Deliberately sequential and deliberately
    /// verify-first: a node that is already unreachable must be reported, not written to, and
    /// taking the fleet's agents down in one shot would leave nothing to diagnose from if the
    /// release turned out to be broken. Miners keep hashing throughout - the agent restart does
    /// not touch them.
    /// </summary>
    private static async Task<int> SyncFleetConfigCliAsync(FleetService fleet, string[] args, CancellationToken ct)
    {
        var forcePush = args.Any(a => a.Equals("--push", StringComparison.OrdinalIgnoreCase));
        var report = await fleet.SyncFleetConfigAsync(forcePushLocal: forcePush, ct);

        if (report.PulledFrom is not null)
            UiHelpers.Result(true, $"pulled newer fleet.json from {report.PulledFrom}");
        if (report.PushedTo.Count > 0)
            UiHelpers.Result(true, $"pushed newest fleet.json to {string.Join(", ", report.PushedTo)}");
        if (report.UpToDate.Count > 0)
            AnsiConsole.MarkupLine($"[grey]up to date: {UiHelpers.Escape(string.Join(", ", report.UpToDate))}[/]");
        if (report.Unsupported.Count > 0)
            AnsiConsole.MarkupLine($"[yellow]needs agent upgrade (no sync): {UiHelpers.Escape(string.Join(", ", report.Unsupported))}[/]");
        if (report.Offline.Count > 0)
            AnsiConsole.MarkupLine($"[grey]offline: {UiHelpers.Escape(string.Join(", ", report.Offline))}[/]");

        return report.Offline.Count > 0 ? 1 : 0;
    }

    private static async Task<int> UpgradeAgentsAsync(FleetConfig config, FleetService fleet, string[] names, CancellationToken ct)
    {
        var version = names.FirstOrDefault(n => n.StartsWith("--version=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1];
        var force = names.Contains("--force");
        var wanted = names.Where(n => !n.StartsWith("--", StringComparison.Ordinal)).ToArray();

        var targets = wanted.Length == 0
            ? fleet.EnabledNodes
            : wanted.Select(config.FindNode).OfType<NodeConfig>().ToList();

        foreach (var name in wanted.Where(n => config.FindNode(n) is null))
            AnsiConsole.MarkupLine($"[red]No node named '{Markup.Escape(name)}'.[/]");

        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to do.[/]");
            return 1;
        }

        var failures = 0;

        foreach (var node in targets.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            using var client = fleet.CreateClient(node);

            AgentInfoDto? before;
            try
            {
                before = await client.GetInfoAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Never write to a node that is not answering: if the token is wrong or the
                // service is down, an update cannot fix it and may make the state harder to read.
                UiHelpers.Result(false, $"{node.Name}: unreachable, skipped ({ex.Message})");
                failures++;
                continue;
            }

            AnsiConsole.MarkupLine($"[grey]{UiHelpers.Escape(node.Name)}[/] agent {UiHelpers.Escape(before?.AgentVersion ?? "?")} -> updating...");

            try
            {
                var result = await client.UpdateAgentAsync(new AgentUpdateRequestDto { Version = version, Force = force }, ct);
                if (result is null)
                {
                    UiHelpers.Result(false, $"{node.Name}: the agent returned no result");
                    failures++;
                    continue;
                }

                UiHelpers.Result(result.Ok, $"{node.Name}: {result.Message}");
                if (!result.Ok) { failures++; continue; }
                if (!result.Restarting) continue;

                if (await WaitForAgentAsync(fleet, node, before, ct) is { } after)
                    AnsiConsole.MarkupLine($"  [green]back up[/] on {UiHelpers.Escape(after.AgentVersion)}");
                else
                {
                    AnsiConsole.MarkupLine("  [yellow]did not come back within 90s - check the service on the node[/]");
                    failures++;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                // The agent can drop the connection while swapping itself; that is not a failure
                // on its own, so fall through to the same wait-and-verify path.
                AnsiConsole.MarkupLine($"  [grey]connection closed during the swap, waiting for the restart...[/]");
                if (await WaitForAgentAsync(fleet, node, before, ct) is { } after)
                    AnsiConsole.MarkupLine($"  [green]back up[/] on {UiHelpers.Escape(after.AgentVersion)}");
                else
                {
                    UiHelpers.Result(false, $"{node.Name}: did not come back ({ex.Message})");
                    failures++;
                }
            }
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Waits for the node to come back on the <em>new</em> build. An answer alone proves nothing:
    /// the outgoing process keeps serving for a couple of seconds after it has written the new
    /// files, so a naive wait reports the old version as "back up" and a successful roll-out looks
    /// like a failed one. A restarted agent gives itself away by its uptime resetting.
    /// </summary>
    private static async Task<AgentInfoDto?> WaitForAgentAsync(FleetService fleet, NodeConfig node, AgentInfoDto? before, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(120))
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            try
            {
                using var client = fleet.CreateClient(node, TimeSpan.FromSeconds(5));
                if (await client.GetInfoAsync(ct) is not { } info) continue;

                var youngerThanThisWait = info.AgentUptimeSeconds < (DateTime.UtcNow - started).TotalSeconds + 5;
                var versionMoved = before is not null && info.AgentVersion != before.AgentVersion;
                if (youngerThanThisWait || versionMoved) return info;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Still restarting.
            }
        }
        return null;
    }

    /// <summary>
    /// Reports, pushes or pins the mining power limit.
    ///
    /// Reporting is the default because that is what an operator wants when a node's hashrate
    /// looks wrong: the rung and the reason answer "is it throttled, or is it broken" in one line,
    /// and those two have very different remedies.
    /// </summary>
    /// <summary>
    /// Reports or drives GPU mining. With no flag it reports, which is the safe default for a
    /// command that can otherwise take a card away from whoever is using the machine.
    /// </summary>
    private static async Task<int> GpuAsync(FleetConfig config, FleetService fleet, string[] args, CancellationToken ct)
    {
        var names = args.Where(a => !a.StartsWith('-')).ToArray();
        var start = args.Contains("--start");
        var stop = args.Contains("--stop");
        var sync = args.Contains("--sync");

        if (start && stop)
        {
            AnsiConsole.MarkupLine("[red]--start and --stop ask for opposite things.[/]");
            return 2;
        }

        var targets = names.Length == 0
            ? fleet.EnabledNodes
            : names.Select(config.FindNode).OfType<NodeConfig>().ToList();

        foreach (var name in names.Where(n => config.FindNode(n) is null))
            AnsiConsole.MarkupLine($"[red]No node named '{Markup.Escape(name)}'.[/]");

        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to do.[/]");
            return 1;
        }

        if (sync)
        {
            var pushed = await fleet.PushGpuMinerAsync(targets, ct);
            foreach (var (node, result) in pushed.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
                UiHelpers.Result(result.Ok, $"{node.Name}: {result.Message}");

            return pushed.All(r => r.Result.Ok) ? 0 : 1;
        }

        if (start || stop)
        {
            var acted = await fleet.ForEachAsync(
                targets,
                (_, client, token) => start ? client.GpuStartAsync(token) : client.GpuStopAsync(token),
                ct);
            foreach (var (node, result) in acted.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
                UiHelpers.Result(result.Ok, $"{node.Name}: {result.Message}");

            return acted.All(r => r.Result.Ok) ? 0 : 1;
        }

        var states = (await fleet.PollAsync(ct))
            .Where(s => targets.Any(t => t.Name == s.Node.Name))
            .ToList();
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .AddColumn("Node")
            .AddColumn("GPU")
            .AddColumn(new TableColumn("Shares").RightAligned())
            .AddColumn("Algorithm")
            .AddColumn("Pool");

        foreach (var state in states.OrderBy(s => s.Node.Name, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(
                new Markup(UiHelpers.Escape(state.Node.Name)),
                new Markup(UiHelpers.GpuBadge(state.Gpu)),
                new Markup(UiHelpers.GpuShares(state.Gpu)),
                new Markup(UiHelpers.Escape(state.Gpu?.Algorithm ?? "-")),
                new Markup(UiHelpers.Escape(state.Gpu?.Pool ?? "-")));
        }

        AnsiConsole.Write(table);

        // A node whose agent predates GPU mining is not an error, but it is not a success either:
        // the operator asked about a card and got no answer for it.
        return states.Any(s => s.Online && s.Gpu is null) ? 1 : 0;
    }

    private static async Task<int> ThrottleAsync(FleetConfig config, FleetService fleet, string[] args, CancellationToken ct)
    {
        var names = args.Where(a => !a.StartsWith('-')).ToArray();
        var wantLog = args.Contains("--log");
        var auto = args.Contains("--auto");
        var sync = args.Contains("--sync");

        int? pinned = null;
        if (args.FirstOrDefault(a => a.StartsWith("--set=", StringComparison.OrdinalIgnoreCase)) is { } setArg)
        {
            if (!int.TryParse(setArg[6..], out var level) || level is < 0 or > 100)
            {
                AnsiConsole.MarkupLine("[red]--set needs a whole number from 0 to 100.[/]");
                return 2;
            }
            pinned = level;
        }

        if (pinned is not null && auto)
        {
            AnsiConsole.MarkupLine("[red]--set and --auto ask for opposite things.[/]");
            return 2;
        }

        var targets = names.Length == 0
            ? fleet.EnabledNodes
            : names.Select(config.FindNode).OfType<NodeConfig>().ToList();

        var missing = names.Where(n => config.FindNode(n) is null).ToList();
        foreach (var name in missing)
            AnsiConsole.MarkupLine($"[red]No node named '{Markup.Escape(name)}'.[/]");

        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to do.[/]");
            return 1;
        }

        if (pinned is not null || auto || sync)
        {
            var results = await fleet.PushThrottleAsync(targets, pinned, auto, ct);
            foreach (var (node, result) in results.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
                UiHelpers.Result(result.Ok, $"{node.Name}: {result.Message}");

            if (!results.All(r => r.Result.Ok)) return 1;
        }

        if (wantLog) return await ThrottleLogAsync(fleet, targets, ct);

        var states = await fleet.PollAsync(ct, TimeSpan.FromSeconds(4));
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Node")
            .AddColumn(new TableColumn("Power").RightAligned())
            .AddColumn(new TableColumn("Other CPU").RightAligned())
            .AddColumn(new TableColumn("Hashrate").RightAligned())
            .AddColumn("Reason");

        foreach (var state in states.Where(s => targets.Any(t => t.Name == s.Node.Name))
                                    .OrderBy(s => s.Node.Name, StringComparer.OrdinalIgnoreCase))
        {
            var throttle = state.Throttle;
            table.AddRow(
                new Markup($"[bold]{UiHelpers.Escape(state.Node.Name)}[/]"),
                new Markup(UiHelpers.ThrottleBadge(throttle)),
                new Markup(throttle?.OtherCpuPercent is { } cpu ? $"{cpu:0.#}%" : "[grey]-[/]"),
                new Markup(state.Hashrate > 0 ? $"[aqua]{Economics.FormatHashrate(state.Hashrate)}[/]" : "[grey]-[/]"),
                new Markup(throttle is null
                    ? "[grey]agent predates throttling; run upgrade-agents[/]"
                    : UiHelpers.Escape(throttle.Reason)));
        }

        AnsiConsole.Write(table);
        return missing.Count == 0 ? 0 : 1;
    }

    /// <summary>Prints each node's decision log, which is where the thresholds get corrected from.</summary>
    private static async Task<int> ThrottleLogAsync(FleetService fleet, IReadOnlyList<NodeConfig> targets, CancellationToken ct)
    {
        var ok = true;

        foreach (var node in targets.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            using var client = fleet.CreateClient(node, TimeSpan.FromSeconds(30));
            AnsiConsole.MarkupLine($"[bold]{UiHelpers.Escape(node.Name)}[/]");

            try
            {
                var log = await client.GetThrottleLogAsync(200, ct);
                if (log is null)
                {
                    AnsiConsole.MarkupLine("  [grey]agent predates throttling; run upgrade-agents[/]");
                    ok = false;
                }
                else if (log.Lines.Count == 0)
                {
                    AnsiConsole.MarkupLine("  [grey]no decisions recorded yet[/]");
                }
                else
                {
                    foreach (var line in log.Lines)
                        AnsiConsole.MarkupLine($"  {UiHelpers.Escape(line)}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                AnsiConsole.MarkupLine($"  [red]{UiHelpers.Escape(ex.Message)}[/]");
                ok = false;
            }

            AnsiConsole.WriteLine();
        }

        return ok ? 0 : 1;
    }

    /// <summary>
    /// Reports or sets autostart per node. Reporting is what it does with no flags, because the
    /// answer matters most right after an unattended reboot - exactly when nobody is watching
    /// and a scheduled task is all there is to notice a rig came back idle.
    /// </summary>
    private static async Task<int> AutoStartAsync(FleetConfig config, FleetService fleet, string[] args, CancellationToken ct)
    {
        var names = args.Where(a => !a.StartsWith('-')).ToArray();
        var on = args.Contains("--on");
        var off = args.Contains("--off");

        if (on && off)
        {
            AnsiConsole.MarkupLine("[red]--on and --off ask for opposite things.[/]");
            return 2;
        }

        var set = on ? true : off ? false : (bool?)null;
        return await ControlAsync(config, fleet, names, FleetService.AutoStartAction(set), ct);
    }

    /// <summary>
    /// Queries, sets, clears or syncs the per-node electricity tariff stored on each agent.
    /// </summary>
    public static async Task<int> TariffAsync(FleetConfig config, FleetService fleet, string[] args, CancellationToken ct)
    {
        var names = args.Where(a => !a.StartsWith('-')).ToArray();
        var clear = args.Contains("--default") || args.Contains("--clear");
        var sync = args.Contains("--sync");

        double? setRate = null;
        if (args.FirstOrDefault(a => a.StartsWith("--set=", StringComparison.OrdinalIgnoreCase)) is { } setArg)
        {
            var raw = setArg[6..].Trim().Replace(',', '.');
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                || !double.IsFinite(parsed)
                || parsed < 0)
            {
                AnsiConsole.MarkupLine("[red]--set needs a non-negative number (e.g. --set=6.8 or --set=0.01).[/]");
                return 2;
            }
            setRate = parsed;
        }

        var flagCount = (setRate is not null ? 1 : 0) + (clear ? 1 : 0) + (sync ? 1 : 0);
        if (flagCount > 1)
        {
            AnsiConsole.MarkupLine("[red]Choose only one of --set, --default, or --sync.[/]");
            return 2;
        }

        var targets = names.Length == 0
            ? fleet.EnabledNodes
            : names.Select(config.FindNode).OfType<NodeConfig>().ToList();

        var missing = names.Where(n => config.FindNode(n) is null).ToList();
        foreach (var name in missing)
            AnsiConsole.MarkupLine($"[red]No node named '{Markup.Escape(name)}'.[/]");

        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to do.[/]");
            return 1;
        }

        var currency = config.Electricity.Currency;
        var fleetDefault = config.Electricity.PricePerKwh;

        if (setRate is not null || clear)
        {
            var results = await fleet.ForEachAsync(
                targets,
                FleetService.TariffAction(setRate, clear, currency, fleetDefault),
                ct);
            foreach (var (node, result) in results.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
                UiHelpers.Result(result.Ok, $"{node.Name}: {result.Message}");

            return results.All(r => r.Result.Ok) && missing.Count == 0 ? 0 : 1;
        }

        if (sync)
        {
            var results = await fleet.SyncTariffsAsync(targets, ct);
            foreach (var (node, result) in results.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
                UiHelpers.Result(result.Ok, $"{node.Name}: {result.Message}");

            return results.All(r => r.Result.Ok) && missing.Count == 0 ? 0 : 1;
        }

        var states = await Task.WhenAll(
            targets.Select(n => fleet.PollNodeAsync(n, ct, TimeSpan.FromSeconds(4))));

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .AddColumn("Node")
            .AddColumn("State")
            .AddColumn(new TableColumn($"Agent ({UiHelpers.Escape(currency)}/kWh)").RightAligned())
            .AddColumn(new TableColumn($"Effective ({UiHelpers.Escape(currency)}/kWh)").RightAligned())
            .AddColumn("Source");

        foreach (var state in states.OrderBy(s => s.Node.Name, StringComparer.OrdinalIgnoreCase))
        {
            var agentRate = state.Snapshot?.PricePerKwh is { } ar && double.IsFinite(ar) && ar >= 0 ? ar : (double?)null;
            var localRate = state.Node.PricePerKwh is { } lr && double.IsFinite(lr) && lr >= 0 ? lr : (double?)null;
            var effective = config.PricePerKwhFor(state);

            var agentCell = !state.Online
                ? "[grey]offline[/]"
                : agentRate is { } a
                    ? $"[aqua]{a:0.00##}[/]"
                    : "[grey]-[/]";

            var source = agentRate is not null
                ? "[green]agent[/]"
                : localRate is not null
                    ? (state.Online ? "[yellow]local fleet.json (run tariff --sync)[/]" : "[grey]local fallback[/]")
                    : "[grey]fleet default[/]";

            table.AddRow(
                new Markup($"[bold]{UiHelpers.Escape(state.Node.Name)}[/]"),
                new Markup(UiHelpers.StatusBadge(state)),
                new Markup(agentCell),
                new Markup($"{effective:0.00##}"),
                new Markup(source));
        }

        AnsiConsole.Write(table);
        return missing.Count == 0 ? 0 : 1;
    }

    private static async Task<int> ControlAsync(
        FleetConfig config,
        FleetService fleet,
        string[] names,
        Func<AgentClient, CancellationToken, Task<CommandResultDto?>> action,
        CancellationToken ct)
    {
        var targets = names.Length == 0
            ? fleet.EnabledNodes
            : names.Select(config.FindNode).OfType<NodeConfig>().ToList();

        var missing = names.Where(n => config.FindNode(n) is null).ToList();
        foreach (var name in missing)
            AnsiConsole.MarkupLine($"[red]No node named '{Markup.Escape(name)}'.[/]");

        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to do.[/]");
            return 1;
        }

        var results = await fleet.ForEachAsync(targets, action, ct);
        foreach (var (node, result) in results.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
            UiHelpers.Result(result.Ok, $"{node.Name}: {result.Message}");

        return results.All(r => r.Result.Ok) && missing.Count == 0 ? 0 : 1;
    }
}

/// <summary>Plain-output variant of the economics screen, without prompts.</summary>
internal sealed class EconomicsScreenReport(FleetConfig config, FleetService fleet, MarketService market)
{
    public async Task WriteAsync(CancellationToken ct)
    {
        using var gpuPool = new GpuPoolService();
        var pollTask = fleet.PollAsync(ct);
        var networkTask = market.GetNetworkStatsAsync(ct);
        var walletTask = market.GetWalletStatsAsync(ct);
        var priceTask = market.GetPriceAsync(ct);
        await Task.WhenAll(pollTask, networkTask, walletTask, priceTask);

        var states = pollTask.Result;
        var lifetimeLedger = LifetimeIncomeLedger.For(config);
        var gpuQuotesTask = gpuPool.ResolveQuotesAsync(states, config, fleet, ct);
        var coinQuotesTask = gpuPool.ResolveCoinQuotesAsync(
            lifetimeLedger.KnownGpuCoinSlugs(states, config),
            config.Electricity.Currency,
            ct);
        var moneyTask = market.GetMoneyFormatAsync(ct);
        await Task.WhenAll(gpuQuotesTask, coinQuotesTask, moneyTask);

        var economics = Economics.Calculate(states, config, networkTask.Result, priceTask.Result, gpuQuotesTask.Result);
        var lifetime = lifetimeLedger.UpdateAndCalculate(
            states, config, networkTask.Result, priceTask.Result, coinQuotesTask.Result, gpuQuotesTask.Result);
        var currency = economics.Currency;
        var credited = walletTask.Result?.CreditedTodayXmr;
        var money = moneyTask.Result;

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Metric").AddColumn(new TableColumn("Value").RightAligned());
        table.AddRow("CPU hashrate", Economics.FormatHashrate(economics.TotalHashrate));
        if (economics.TotalGpuHashrate > 0)
        {
            table.AddRow("GPU rate", UiHelpers.Escape(Economics.FormatGpuFleetRate(states)));
        }
        table.AddRow("Power draw", $"{economics.TotalWatts:0} W");
        table.AddRow("Electricity per day", money.Markup(economics.CostPerDay));
        table.AddRow("CPU income per day (XMR)", economics.XmrPerDay is { } x ? $"{x:0.000000} XMR" : "-");
        table.AddRow($"CPU income per day, {UiHelpers.Escape(currency)}", money.Markup(economics.RevenuePerDay));
        if (economics.TotalGpuHashrate > 0)
        {
            table.AddRow($"GPU income per day, {UiHelpers.Escape(currency)}", economics.GpuRevenuePerDay is { } g
                ? money.Markup(g)
                : "[yellow]needs GPU pool/price[/]");
        }
        table.AddRow($"Total income per day, {UiHelpers.Escape(currency)}", Dashboard.FormatFleetTotalIncomeMarkup(economics, money));
        table.AddRow("Credited by XMR pool, 24h", credited is { } c ? $"{c:0.000000} XMR" : "-");
        table.AddRow("Actual vs XMR estimate", economics.XmrPerDay is > 0 && credited is { } c2
            ? $"{c2 / economics.XmrPerDay.Value:P0}"
            : "-");
        table.AddRow("Net profit per day", money.Signed(economics.TotalProfitPerDay));
        table.AddRow("Cost per XMR", money.Markup(economics.CostPerXmr));
        if (lifetime.AnyTracked)
        {
            table.AddRow($"All-time income, {UiHelpers.Escape(currency)}", EconomicsScreen.FormatLifetimeIncomeMarkup(lifetime, money));
            table.AddRow("All-time electricity", $"{lifetime.TotalEnergyKwh:N2} kWh  ({money.Markup(lifetime.TotalElectricityCost)})");
            table.AddRow("All-time net profit", lifetime.TotalNetProfit is { } anp ? money.Signed(anp) : "[grey]-[/]");
        }
        AnsiConsole.Write(table);

        if (states.Any(s => s.AnyMining))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(EconomicsScreen.BuildNodeBreakdownTable(economics, config, money));
            if (FleetCharts.BuildEconomicsCharts(economics) is { } charts)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(charts);
            }
        }

        if (lifetime.Nodes.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(EconomicsScreen.BuildLifetimeTable(lifetime, money));
        }
    }
}

/// <summary>Plain-output variant of the pool screen.</summary>
internal sealed class PoolReport(FleetConfig config, MarketService market)
{
    public async Task WriteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Pool.Wallet))
        {
            AnsiConsole.MarkupLine("[yellow]No wallet configured.[/]");
            return;
        }

        var walletTask = market.GetWalletStatsAsync(ct);
        var networkTask = market.GetNetworkStatsAsync(ct);
        var priceTask = market.GetPriceAsync(ct);
        await Task.WhenAll(walletTask, networkTask, priceTask);

        var wallet = walletTask.Result;
        var network = networkTask.Result;
        var price = priceTask.Result;
        var money = await market.GetMoneyFormatAsync(ct);

        if (wallet is null)
        {
            AnsiConsole.MarkupLine("[red]The pool API did not answer.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Metric").AddColumn(new TableColumn("Value").RightAligned());
        table.AddRow("Pool hashrate", wallet.HashrateNow is { } h ? Economics.FormatHashrate(h) : "-");
        table.AddRow("Valid shares", wallet.ValidShares?.ToString("N0") ?? "-");
        table.AddRow("Confirmed balance", wallet.ConfirmedBalanceXmr is { } d ? $"{d:0.000000} XMR" : "-");
        table.AddRow($"Confirmed balance, {config.Electricity.Currency}", wallet.ConfirmedBalanceXmr is { } d2 && price is { } p
            ? money.Markup(d2 * p) : "-");
        table.AddRow("Unconfirmed", wallet.UnconfirmedBalanceXmr is { } u ? $"{u:0.000000} XMR" : "-");
        table.AddRow("Payout threshold", wallet.PayoutThresholdXmr is { } t ? $"{t:0.000000} XMR" : "-");
        table.AddRow("Paid out", wallet.TotalPaidXmr is { } paid ? $"{paid:0.000000} XMR" : "-");
        table.AddRow("Network hashrate", network?.NetworkHashrate is { } nh ? Economics.FormatHashrate(nh) : "-");
        table.AddRow("XMR price", money.Format(price));
        AnsiConsole.Write(table);
    }
}
