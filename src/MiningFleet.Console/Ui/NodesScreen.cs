using System.Globalization;
using Spectre.Console;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Ui;

/// <summary>Managing the node list: discover from the tailnet, add by hand, edit, remove.</summary>
public sealed class NodesScreen
{
    private readonly FleetConfig _config;
    private readonly FleetService _fleet;

    public NodesScreen(FleetConfig config, FleetService fleet)
    {
        _config = config;
        _fleet = fleet;
    }

    public async Task ShowAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UiHelpers.Header("Nodes");
            await RenderListAsync(ct);

            var choice = AnsiConsole.Prompt(UiHelpers.Menu("Action", "< back",
                    "Discover from tailnet",
                    "Add manually",
                    "Edit node",
                    "Test connection",
                    "Update agents",
                    "Enable / disable",
                    "Remove node",
                    "< back"));

            switch (choice)
            {
                case "Discover from tailnet": await DiscoverAsync(ct); break;
                case "Add manually": await AddManuallyAsync(ct); break;
                case "Edit node": await EditAsync(ct); break;
                case "Test connection": await TestAsync(ct); break;
                case "Update agents": await UpdateAgentsAsync(ct); break;
                case "Enable / disable": ToggleEnabled(); break;
                case "Remove node": Remove(); break;
                default: return;
            }
        }
    }

    private async Task RenderListAsync(CancellationToken ct)
    {
        if (_config.Nodes.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No nodes yet. Start with 'Discover from tailnet'.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        IReadOnlyList<NodeState> states = [];
        await AnsiConsole.Status().StartAsync("Querying node agents...", async _ =>
        {
            states = await Task.WhenAll(
                _config.Nodes.Select(n => _fleet.PollNodeAsync(n, ct, TimeSpan.FromSeconds(2.5))));
        });
        var stateByName = states.ToDictionary(s => s.Node.Name, StringComparer.OrdinalIgnoreCase);

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .AddColumn("Name").AddColumn("Endpoint").AddColumn("Enabled").AddColumn("Miner path")
            .AddColumn("Fallback W").AddColumn($"{UiHelpers.Escape(_config.Electricity.Currency)}/kWh");

        foreach (var node in _config.Nodes)
        {
            stateByName.TryGetValue(node.Name, out var state);
            var tariffCell = state?.Snapshot?.PricePerKwh is { } agentRate && double.IsFinite(agentRate) && agentRate >= 0
                ? $"{agentRate:0.00##}"
                : node.PricePerKwh is { } localRate && double.IsFinite(localRate) && localRate >= 0
                    ? $"{localRate:0.00##} [grey](local)[/]"
                    : $"[grey]{_config.Electricity.PricePerKwh:0.00##}[/]";

            table.AddRow(
                UiHelpers.Escape(node.Name),
                UiHelpers.Escape($"{node.Host}:{node.Port}"),
                node.Enabled ? "[green]yes[/]" : "[grey]no[/]",
                UiHelpers.Escape(node.MinerPath ?? "-"),
                node.PowerFallbackWatts is { } w ? $"{w:0}" : "[grey]-[/]",
                tariffCell);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private async Task DiscoverAsync(CancellationToken ct)
    {
        UiHelpers.Header("Discover nodes");

        IReadOnlyList<TailnetMachine> machines = [];
        await AnsiConsole.Status().StartAsync("Reading tailscale status...", async _ =>
        {
            machines = await TailscaleService.ListAsync(ct);
        });

        if (machines.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No tailnet machines found.[/] Is the tailscale CLI installed and logged in?");
            UiHelpers.Pause();
            return;
        }

        var selectable = machines.Where(m => !_config.Nodes.Any(n => m.MatchesHost(n.Host))).ToList();
        if (selectable.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Every tailnet machine is already in the fleet.[/]");
            UiHelpers.Pause();
            return;
        }

        AnsiConsole.MarkupLine("[grey]Remote nodes use MagicDNS when available; this machine uses 127.0.0.1.[/]");
        AnsiConsole.WriteLine();

        var picked = AnsiConsole.Prompt(new MultiSelectionPrompt<TailnetMachine>()
            .Title("Machines to add as mining nodes")
            .PageSize(15)
            .WrapAround(true)
            // Empty, which the caller below already reads as "chose nothing". The overload taking
            // an item would answer Escape by adding that machine to the fleet.
            .AddCancelResult()
            .NotRequired()
            .InstructionsText("[grey](space to toggle, enter to confirm)[/]")
            // Hostnames and the OS name come from tailscale; escaped because the prompt
            // renders choices as markup and "[windows]" would be read as a style tag.
            .UseConverter(m => UiHelpers.Escape(
                $"{m.Name} - {m.Address} [{m.Os}] {(m.Online ? "online" : "offline")}"))
            .AddChoices(selectable));

        if (picked.Count == 0) return;

        foreach (var machine in picked)
        {
            _config.Nodes.Add(new NodeConfig
            {
                Name = machine.Name,
                Host = machine.Host,
                Port = _config.AgentPort,
            });
        }

        _config.Save();
        AnsiConsole.MarkupLine($"[green]Added {picked.Count} node(s).[/] Agents still need to be installed and running on them.");
        UiHelpers.Pause();
    }

    private async Task AddManuallyAsync(CancellationToken ct)
    {
        UiHelpers.Header("Add node");

        var name = AnsiConsole.Prompt(UiHelpers.Text("Name:").Validate(v =>
            _config.FindNode(v) is null ? ValidationResult.Success() : ValidationResult.Error("[red]That name is taken[/]")));
        var host = AnsiConsole.Ask<string>("Tailscale IP or MagicDNS name:");
        var port = AnsiConsole.Prompt(new TextPrompt<int>("Agent port:").DefaultValue(_config.AgentPort));
        var watts = AnsiConsole.Prompt(new TextPrompt<double>("Fallback watts (used when no power sensor):").DefaultValue(0d));
        var tariff = PromptTariff(null);

        var node = new NodeConfig
        {
            Name = name,
            Host = host,
            Port = port,
            PowerFallbackWatts = watts > 0 ? watts : null,
            PricePerKwh = tariff,
        };
        _config.Nodes.Add(node);
        _config.Save();

        UiHelpers.Result(true, $"Added {name}.");
        if (AnsiConsole.Confirm("Push tariff and pool settings to the agent now?", defaultValue: true))
            await PushConfigAsync(node, tariff, ct);

        UiHelpers.Pause();
    }

    private async Task EditAsync(CancellationToken ct)
    {
        var node = UiHelpers.SelectNode(_config, "Node to edit");
        if (node is null) return;

        UiHelpers.Header($"Edit {node.Name}");

        double? agentTariff = null;
        try
        {
            using var probe = _fleet.CreateClient(node, TimeSpan.FromSeconds(3));
            agentTariff = (await probe.GetConfigAsync(ct))?.PricePerKwh;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
        }

        node.Host = AnsiConsole.Prompt(UiHelpers.Text("Host:").DefaultValue(node.Host));
        node.Port = AnsiConsole.Prompt(new TextPrompt<int>("Agent port:").DefaultValue(node.Port));
        node.MinerPath = NullIfBlank(AnsiConsole.Prompt(
            UiHelpers.Text("xmrig directory on that node:").AllowEmpty().DefaultValue(node.MinerPath ?? "")));

        var watts = AnsiConsole.Prompt(new TextPrompt<double>("Fallback watts (0 = none):").DefaultValue(node.PowerFallbackWatts ?? 0));
        node.PowerFallbackWatts = watts > 0 ? watts : null;

        var tariff = PromptTariff(agentTariff ?? node.PricePerKwh);
        node.PricePerKwh = tariff;

        node.Token = NullIfBlank(AnsiConsole.Prompt(
            UiHelpers.Text("Token override (blank uses the fleet token):").AllowEmpty().DefaultValue(node.Token ?? "")));

        _config.Save();

        // Push the settings the agent itself needs to know about (including the node tariff).
        if (AnsiConsole.Confirm("Push tariff and pool/wallet/path settings to the agent now?", defaultValue: true))
            await PushConfigAsync(node, tariff, ct);

        UiHelpers.Pause();
    }

    private double? PromptTariff(double? current)
    {
        var raw = AnsiConsole.Prompt(
            UiHelpers.Text(
                $"Electricity on this agent, {UiHelpers.Escape(_config.Electricity.Currency)}/kWh " +
                $"(blank = fleet default {_config.Electricity.PricePerKwh:0.00##}):")
            .AllowEmpty()
            .DefaultValue(current is { } c ? c.ToString("0.####", CultureInfo.InvariantCulture) : "")
            .Validate(v =>
            {
                if (string.IsNullOrWhiteSpace(v) || string.Equals(v.Trim(), "default", StringComparison.OrdinalIgnoreCase))
                    return ValidationResult.Success();
                return TryParseTariff(v, out _)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Enter a non-negative number (e.g. 6.8 or 0.01) or leave blank for default[/]");
            }));

        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), "default", StringComparison.OrdinalIgnoreCase))
            return null;

        return TryParseTariff(raw, out var parsed) ? parsed : null;
    }

    private static bool TryParseTariff(string raw, out double tariff)
    {
        var normalized = raw.Trim().Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out tariff)
            && double.IsFinite(tariff)
            && tariff >= 0)
        {
            return true;
        }
        tariff = 0;
        return false;
    }

    private async Task PushConfigAsync(NodeConfig node, double? tariff, CancellationToken ct)
    {
        using var client = _fleet.CreateClient(node, TimeSpan.FromSeconds(20));
        try
        {
            var pushed = await client.PutConfigAsync(new MinerConfigDto
            {
                ExecutablePath = node.MinerPath,
                PoolUrl = _config.Pool.Url,
                Wallet = _config.Pool.Wallet,
                WorkerName = node.Name,
                Password = _config.Pool.Password,
                PowerFallbackWatts = node.PowerFallbackWatts,
                PricePerKwh = tariff,
                ClearPricePerKwh = tariff is null ? true : null,
            }, ct);

            if (pushed is not null)
            {
                var storedOnAgent = tariff is null
                    ? pushed.PricePerKwh is null
                    : pushed.PricePerKwh is { } p && Math.Abs(p - tariff.Value) < 1e-6;
                if (storedOnAgent && node.PricePerKwh is not null)
                {
                    node.PricePerKwh = null;
                    _config.Save();
                }
            }

            UiHelpers.Result(pushed is not null, pushed is null
                ? "Agent returned no config."
                : $"Agent config updated (exe: {pushed.ExecutablePath ?? "not set"}, tariff: {(pushed.PricePerKwh is { } t ? $"{t:0.00##} {_config.Electricity.Currency}/kWh" : "fleet default")}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            UiHelpers.Result(false, $"{node.Name}: {ex.Message} (tariff kept locally until agent is reachable)");
        }
    }

    private async Task TestAsync(CancellationToken ct)
    {
        UiHelpers.Header("Test connections");
        var states = await AnsiConsole.Status().StartAsync("Polling agents...", async _ => await _fleet.PollAsync(ct));

        foreach (var state in states)
        {
            if (state.Online)
            {
                var info = state.Snapshot!.Agent;
                var elevated = info.IsElevated ? "elevated" : "[yellow]not elevated - some sensors will be blank[/]";
                UiHelpers.Result(true, $"{state.Node.Name}: agent {info.AgentVersion} on {info.Hostname}, {elevated}");
                if (info.ApiVersion != ApiVersion.Current)
                    AnsiConsole.MarkupLine($"  [yellow]API version mismatch: agent {UiHelpers.Escape(info.ApiVersion)}, console {ApiVersion.Current}[/]");
                if (state.Snapshot.Hardware.SensorNotice is { Length: > 0 } notice)
                    AnsiConsole.MarkupLine($"  [yellow]{UiHelpers.Escape(notice)}[/]");
            }
            else
            {
                UiHelpers.Result(false, $"{state.Node.Name}: {state.Error}");
            }
        }

        UiHelpers.Pause();
    }

    private void ToggleEnabled()
    {
        var node = UiHelpers.SelectNode(_config, "Node to enable/disable");
        if (node is null) return;

        node.Enabled = !node.Enabled;
        _config.Save();
        UiHelpers.Result(true, $"{node.Name} is now {(node.Enabled ? "enabled" : "disabled")}.");
        UiHelpers.Pause();
    }

    private void Remove()
    {
        var node = UiHelpers.SelectNode(_config, "Node to remove");
        if (node is null) return;

        if (!AnsiConsole.Confirm($"Remove {UiHelpers.Escape(node.Name)} from the fleet?", defaultValue: false)) return;

        _config.Nodes.Remove(node);
        _config.Save();
        UiHelpers.Result(true, $"Removed {node.Name}. The agent on that machine is untouched.");
        UiHelpers.Pause();
    }

    private async Task UpdateAgentsAsync(CancellationToken ct)
    {
        UiHelpers.Header("Update agents");
        var nodes = UiHelpers.SelectNodes(_config, "Update which nodes?");
        if (nodes.Count == 0) return;
        var ver = AnsiConsole.Prompt(UiHelpers.Text("Release tag (latest / v1.17.11):").DefaultValue("latest"));
        if (string.IsNullOrWhiteSpace(ver)) ver = "latest";
        var force = AnsiConsole.Confirm("Force reinstall even if same version?", defaultValue: false);
        AnsiConsole.MarkupLine($"[grey]Updating {nodes.Count} node(s) to {UiHelpers.Escape(ver)}...[/]");
        AnsiConsole.WriteLine();
        var failures = 0;
        foreach (var node in nodes.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            using var client = _fleet.CreateClient(node);
            AgentInfoDto? before;
            try { before = await client.GetInfoAsync(ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                UiHelpers.Result(false, $"{node.Name}: unreachable, skipped ({ex.Message})");
                failures++; continue;
            }
            AnsiConsole.MarkupLine($"[grey]{UiHelpers.Escape(node.Name)}[/] agent {UiHelpers.Escape(before?.AgentVersion ?? "?")} -> updating...");
            try
            {
                var res = await client.UpdateAgentAsync(new AgentUpdateRequestDto { Version = ver == "latest" ? null : ver, Force = force }, ct);
                if (res is null) { UiHelpers.Result(false, $"{node.Name}: no result"); failures++; continue; }
                UiHelpers.Result(res.Ok, $"{node.Name}: {res.Message}");
                if (!res.Ok) { failures++; continue; }
                if (!res.Restarting) continue;
                var after = await WaitForAgentAsync(node, before, ct);
                if (after is not null) AnsiConsole.MarkupLine($"  [green]back up[/] on {UiHelpers.Escape(after.AgentVersion)}");
                else { AnsiConsole.MarkupLine("  [yellow]did not come back within 120s[/]"); failures++; }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                AnsiConsole.MarkupLine("  [grey]connection closed during swap, waiting...[/]");
                var after = await WaitForAgentAsync(node, before, ct);
                if (after is not null) AnsiConsole.MarkupLine($"  [green]back up[/] on {UiHelpers.Escape(after.AgentVersion)}");
                else { UiHelpers.Result(false, $"{node.Name}: did not come back ({ex.Message})"); failures++; }
            }
        }
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(failures == 0 ? "[green]All agents updated.[/]" : $"[yellow]{failures} node(s) need attention.[/]");
        UiHelpers.Pause();
    }

    private async Task<AgentInfoDto?> WaitForAgentAsync(NodeConfig node, AgentInfoDto? before, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(120))
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            try
            {
                using var c = _fleet.CreateClient(node, TimeSpan.FromSeconds(5));
                if (await c.GetInfoAsync(ct) is not { } info) continue;
                var younger = info.AgentUptimeSeconds < (DateTime.UtcNow - started).TotalSeconds + 5;
                var moved = before is not null && info.AgentVersion != before.AgentVersion;
                if (younger || moved) return info;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        }
        return null;
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
