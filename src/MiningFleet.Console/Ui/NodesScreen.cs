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
            RenderList();

            var choice = AnsiConsole.Prompt(UiHelpers.Menu("Action", "< back",
                    "Discover from tailnet",
                    "Add manually",
                    "Edit node",
                    "Test connection",
                    "Enable / disable",
                    "Remove node",
                    "< back"));

            switch (choice)
            {
                case "Discover from tailnet": await DiscoverAsync(ct); break;
                case "Add manually": AddManually(); break;
                case "Edit node": await EditAsync(ct); break;
                case "Test connection": await TestAsync(ct); break;
                case "Enable / disable": ToggleEnabled(); break;
                case "Remove node": Remove(); break;
                default: return;
            }
        }
    }

    private void RenderList()
    {
        if (_config.Nodes.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No nodes yet. Start with 'Discover from tailnet'.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .AddColumn("Name").AddColumn("Endpoint").AddColumn("Enabled").AddColumn("Miner path")
            .AddColumn("Fallback W").AddColumn($"{UiHelpers.Escape(_config.Electricity.Currency)}/kWh");

        foreach (var node in _config.Nodes)
        {
            table.AddRow(
                UiHelpers.Escape(node.Name),
                UiHelpers.Escape($"{node.Host}:{node.Port}"),
                node.Enabled ? "[green]yes[/]" : "[grey]no[/]",
                UiHelpers.Escape(node.MinerPath ?? "-"),
                node.PowerFallbackWatts is { } w ? $"{w:0}" : "[grey]-[/]",
                node.PricePerKwh is { } rate ? $"{rate:N2}" : $"[grey]{_config.Electricity.PricePerKwh:N2}[/]");
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

        var known = _config.Nodes.Select(n => n.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectable = machines.Where(m => !known.Contains(m.Address) && !known.Contains(m.Host)).ToList();
        if (selectable.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Every tailnet machine is already in the fleet.[/]");
            UiHelpers.Pause();
            return;
        }

        // Say which form is about to be stored: a MagicDNS name keeps working when a node's
        // tailnet address changes, and the address is all that is left on a machine that does
        // not resolve those names.
        AnsiConsole.MarkupLine(selectable.Any(m => m.DnsName is not null)
            ? "[grey]MagicDNS resolves here - nodes are added by name.[/]"
            : "[grey]MagicDNS does not resolve here - nodes are added by address.[/]");
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

    private void AddManually()
    {
        UiHelpers.Header("Add node");

        var name = AnsiConsole.Prompt(UiHelpers.Text("Name:").Validate(v =>
            _config.FindNode(v) is null ? ValidationResult.Success() : ValidationResult.Error("[red]That name is taken[/]")));
        var host = AnsiConsole.Ask<string>("Tailscale IP or MagicDNS name:");
        var port = AnsiConsole.Prompt(new TextPrompt<int>("Agent port:").DefaultValue(_config.AgentPort));
        var watts = AnsiConsole.Prompt(new TextPrompt<double>("Fallback watts (used when no power sensor):").DefaultValue(0d));
        var rate = AnsiConsole.Prompt(new TextPrompt<double>(
                $"Electricity at this machine, {UiHelpers.Escape(_config.Electricity.Currency)}/kWh " +
                $"(0 = use the fleet default of {_config.Electricity.PricePerKwh:N2}):")
            .DefaultValue(0d));

        _config.Nodes.Add(new NodeConfig
        {
            Name = name,
            Host = host,
            Port = port,
            PowerFallbackWatts = watts > 0 ? watts : null,
            PricePerKwh = rate > 0 ? rate : null,
        });
        _config.Save();

        UiHelpers.Result(true, $"Added {name}.");
        UiHelpers.Pause();
    }

    private async Task EditAsync(CancellationToken ct)
    {
        var node = UiHelpers.SelectNode(_config, "Node to edit");
        if (node is null) return;

        UiHelpers.Header($"Edit {node.Name}");

        node.Host = AnsiConsole.Prompt(UiHelpers.Text("Host:").DefaultValue(node.Host));
        node.Port = AnsiConsole.Prompt(new TextPrompt<int>("Agent port:").DefaultValue(node.Port));
        node.MinerPath = NullIfBlank(AnsiConsole.Prompt(
            UiHelpers.Text("xmrig directory on that node:").AllowEmpty().DefaultValue(node.MinerPath ?? "")));

        var watts = AnsiConsole.Prompt(new TextPrompt<double>("Fallback watts (0 = none):").DefaultValue(node.PowerFallbackWatts ?? 0));
        node.PowerFallbackWatts = watts > 0 ? watts : null;

        var rate = AnsiConsole.Prompt(new TextPrompt<double>(
                $"Electricity at this machine, {UiHelpers.Escape(_config.Electricity.Currency)}/kWh " +
                $"(0 = use the fleet default of {_config.Electricity.PricePerKwh:N2}):")
            .DefaultValue(node.PricePerKwh ?? 0));
        node.PricePerKwh = rate > 0 ? rate : null;

        node.Token = NullIfBlank(AnsiConsole.Prompt(
            UiHelpers.Text("Token override (blank uses the fleet token):").AllowEmpty().DefaultValue(node.Token ?? "")));

        _config.Save();

        // Push the settings the agent itself needs to know about.
        if (AnsiConsole.Confirm("Push pool/wallet/path settings to the agent now?", defaultValue: true))
            await PushConfigAsync(node, ct);

        UiHelpers.Pause();
    }

    private async Task PushConfigAsync(NodeConfig node, CancellationToken ct)
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
            }, ct);

            UiHelpers.Result(pushed is not null, pushed is null
                ? "Agent returned no config."
                : $"Agent config updated (exe: {pushed.ExecutablePath ?? "not set"}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            UiHelpers.Result(false, $"{node.Name}: {ex.Message}");
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

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
