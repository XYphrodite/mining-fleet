using Spectre.Console;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Ui;

/// <summary>GPU mining: enable, algorithm, pool and user — fleet-wide and per-node, pushed immediately.</summary>
public sealed class GpuScreen
{
    private readonly FleetConfig _config;
    private readonly FleetService _fleet;

    public GpuScreen(FleetConfig config, FleetService fleet)
    {
        _config = config;
        _fleet = fleet;
    }

    public async Task ShowAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UiHelpers.Header("GPU mining");
            RenderSummary();

            var choice = AnsiConsole.Prompt(UiHelpers.Menu("Action", "< back",
                    "Configure fleet GPU defaults",
                    "Configure nodes GPU",
                    "Start GPU mining",
                    "Stop GPU mining",
                    "View GPU log",
                    "< back"));

            switch (choice)
            {
                case "Configure fleet GPU defaults": await ConfigureFleetAsync(ct); break;
                case "Configure nodes GPU": await ConfigureNodesAsync(ct); break;
                case "Start GPU mining": await RunGpuAsync(true, ct); break;
                case "Stop GPU mining": await RunGpuAsync(false, ct); break;
                case "View GPU log": await LogsAsync(ct); break;
                default: return;
            }
        }
    }

    private void RenderSummary()
    {
        var f = _config.GpuMiner;
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .AddColumn("Scope").AddColumn("Enabled").AddColumn("Algorithm").AddColumn("Pool").AddColumn("User");
        table.AddRow("[bold]fleet[/]",
            f.Enabled == true ? "[green]yes[/]" : "[grey]no[/]",
            UiHelpers.Escape(f.Algorithm ?? "-"),
            UiHelpers.Escape(f.PoolUrl ?? "-"),
            UiHelpers.Escape(f.User ?? "-"));
        foreach (var n in _config.Nodes.Where(n => n.GpuMiner is not null))
        {
            var g = n.GpuMiner!;
            table.AddRow(UiHelpers.Escape(n.Name),
                g.Enabled is null ? "[grey]-[/]" : g.Enabled == true ? "[green]yes[/]" : "[red]no[/]",
                UiHelpers.Escape(g.Algorithm ?? "-"),
                UiHelpers.Escape(g.PoolUrl ?? "-"),
                UiHelpers.Escape(g.User ?? "-"));
        }
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Fleet defaults apply to every node that has no own value. Nodes override only what they set. Changes save to fleet.json and push to nodes immediately.[/]");
        AnsiConsole.WriteLine();
    }

    private async Task ConfigureFleetAsync(CancellationToken ct)
    {
        UiHelpers.Header("Fleet GPU defaults");
        var cur = _config.GpuMiner;

        var enabled = AnsiConsole.Confirm("Enable GPU mining by default?", cur.Enabled == true);
        var algo = AnsiConsole.Prompt(UiHelpers.Text("Algorithm (CR29, NEXA, blank = leave):").AllowEmpty().DefaultValue(cur.Algorithm ?? ""));
        var pool = AnsiConsole.Prompt(UiHelpers.Text("Pool host:port (blank = leave):").AllowEmpty().DefaultValue(cur.PoolUrl ?? ""));
        var user = AnsiConsole.Prompt(UiHelpers.Text("Pool user (address/worker, blank = leave):").AllowEmpty().DefaultValue(cur.User ?? ""));

        _config.GpuMiner.Enabled = enabled;
        if (!string.IsNullOrWhiteSpace(algo)) _config.GpuMiner.Algorithm = algo.Trim();
        if (!string.IsNullOrWhiteSpace(pool)) _config.GpuMiner.PoolUrl = pool.Trim();
        if (!string.IsNullOrWhiteSpace(user)) _config.GpuMiner.User = user.Trim();
        _config.Save();

        await PushAllAsync(ct);
        UiHelpers.Pause();
    }

    private async Task ConfigureNodesAsync(CancellationToken ct)
    {
        var nodes = UiHelpers.SelectNodes(_config, "Configure which nodes?");
        if (nodes.Count == 0) return;

        UiHelpers.Header($"GPU for {nodes.Count} node(s)");

        // Show current merged view for first node as hint
        var hint = _config.GpuMinerFor(nodes[0]);
        AnsiConsole.MarkupLine($"[grey]Current fleet: {(hint.Enabled == true ? hint.Algorithm : "off")} on {hint.PoolUrl ?? "-"} as {UiHelpers.Escape(hint.User ?? "-")}[/]");
        AnsiConsole.WriteLine();

        var enabledChoice = AnsiConsole.Prompt(UiHelpers.Menu("Enabled", "< back", "on", "off", "inherit fleet", "< back"));
        if (enabledChoice == "< back") return;
        bool? enabled = enabledChoice == "on" ? true : enabledChoice == "off" ? false : null;

        var algo = AnsiConsole.Prompt(UiHelpers.Text("Algorithm (CR29/NEXA, blank = inherit):").AllowEmpty().DefaultValue(""));
        var pool = AnsiConsole.Prompt(UiHelpers.Text("Pool host:port (blank = inherit):").AllowEmpty().DefaultValue(""));
        var user = AnsiConsole.Prompt(UiHelpers.Text("Pool user (blank = inherit):").AllowEmpty().DefaultValue(""));

        foreach (var n in nodes)
        {
            n.GpuMiner ??= new GpuMinerConfig();
            if (enabled is not null) n.GpuMiner.Enabled = enabled;
            else if (enabledChoice == "inherit fleet") n.GpuMiner.Enabled = null;
            if (!string.IsNullOrWhiteSpace(algo)) n.GpuMiner.Algorithm = algo.Trim();
            else if (algo == "") { /* keep */ }
            if (!string.IsNullOrWhiteSpace(pool)) n.GpuMiner.PoolUrl = pool.Trim();
            if (!string.IsNullOrWhiteSpace(user)) n.GpuMiner.User = user.Trim();
            // Blank leaves null (inherit), non-blank sets. To clear a node override explicitly, set fleet value and inherit.
        }
        _config.Save();

        // Push immediately to those nodes
        var results = await _fleet.PushGpuMinerAsync(nodes, ct);
        foreach (var (node, res) in results.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
            UiHelpers.Result(res.Ok, $"{node.Name}: {res.Message}");

        UiHelpers.Pause();
    }

    private async Task PushAllAsync(CancellationToken ct)
    {
        var enabled = _config.Nodes.Where(n => n.Enabled).ToList();
        if (enabled.Count == 0) { AnsiConsole.MarkupLine("[yellow]No enabled nodes to push to.[/]"); return; }
        var results = await _fleet.PushGpuMinerAsync(enabled, ct);
        foreach (var (node, res) in results.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
            UiHelpers.Result(res.Ok, $"{node.Name}: {res.Message}");
    }

    private async Task RunGpuAsync(bool start, CancellationToken ct)
    {
        var nodes = UiHelpers.SelectNodes(_config, $"{(start ? "Start" : "Stop")} GPU on which nodes?");
        if (nodes.Count == 0) return;
        var verb = start ? "Starting" : "Stopping";
        IReadOnlyList<(NodeConfig Node, CommandResultDto Result)> results = [];
        await AnsiConsole.Status().StartAsync($"{verb} GPU on {nodes.Count} node(s)...", async _ =>
        {
            results = await _fleet.ForEachAsync(nodes, (c, t) => start ? c.GpuStartAsync(t) : c.GpuStopAsync(t), ct);
        });
        foreach (var (node, res) in results.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
            UiHelpers.Result(res.Ok, $"{node.Name}: {res.Message}");
        UiHelpers.Pause();
    }

    private async Task LogsAsync(CancellationToken ct)
    {
        var node = UiHelpers.SelectNode(_config, "GPU log from which node?");
        if (node is null) return;
        UiHelpers.Header($"{node.Name} - lolMiner output");
        using var client = _fleet.CreateClient(node, TimeSpan.FromSeconds(15));
        try
        {
            var logs = await client.GetGpuLogsAsync(ct);
            if (logs is null || logs.Lines.Count == 0) AnsiConsole.MarkupLine("[grey]No output captured.[/]");
            else foreach (var line in logs.Lines.TakeLast(40)) AnsiConsole.MarkupLine($"[grey]{UiHelpers.Escape(line)}[/]");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            UiHelpers.Result(false, ex.Message);
        }
        UiHelpers.Pause();
    }
}
