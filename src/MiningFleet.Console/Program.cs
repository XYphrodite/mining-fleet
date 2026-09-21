using Spectre.Console;
using MiningFleet.Console;
using MiningFleet.Console.Ui;

System.Console.OutputEncoding = System.Text.Encoding.UTF8;
System.Console.Title = "mining-fleet";

FleetConfig config;
try
{
    config = FleetConfig.Load();
}
catch (InvalidOperationException ex)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
    return 1;
}

// First run: leave a config on disk so the file can be edited by hand too.
if (!File.Exists(config.Path)) config.Save();

// Remove whatever the previous self-update displaced.
UpdateService.CleanUpPreviousUpdate();

using var cts = new CancellationTokenSource();
System.Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var fleet = new FleetService(config);
using var market = new MarketService(config);

// One-shot mode for scripts and scheduled tasks.
if (args.Length > 0)
    return await Cli.RunAsync(args, config, fleet, market, cts.Token);

// The menu drives the cursor directly, which needs a real terminal.
if (System.Console.IsOutputRedirected || System.Console.IsInputRedirected)
{
    AnsiConsole.WriteLine("The interactive console needs a terminal. Use a command instead:");
    AnsiConsole.WriteLine();
    AnsiConsole.WriteLine(Cli.Usage);
    return 2;
}

var dashboard = new Dashboard(config, fleet, market);
var nodes = new NodesScreen(config, fleet);
var miner = new MinerScreen(config, fleet);
var hardware = new HardwareScreen(config, fleet);
var economics = new EconomicsScreen(config, fleet, market);
var pool = new PoolScreen(config, market);
var settings = new SettingsScreen(config);

var consoleVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

await Updater.NotifyIfOutdatedAsync(config, cts.Token);

try
{
    while (!cts.IsCancellationRequested)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("mining fleet").Color(Color.Aqua));
        AnsiConsole.MarkupLine($"[dim]v{Markup.Escape(consoleVersion)}[/]");
        AnsiConsole.MarkupLine(
            $"[grey]{config.Nodes.Count(n => n.Enabled)} enabled node(s)  |  pool {Markup.Escape(config.Pool.Url)}  |  {Markup.Escape(config.Path)}[/]");
        if (string.IsNullOrWhiteSpace(config.Token))
            AnsiConsole.MarkupLine("[yellow]No fleet token set - agents with a token will reject this console.[/]");
        AnsiConsole.WriteLine();

        // Escape at the top of the tree has nowhere further back to go, so it leaves - the same
        // answer as the entry the operator would otherwise have to scroll past everything to reach.
        var choice = AnsiConsole.Prompt(UiHelpers.Menu("Main menu", "Exit",
                "Dashboard (live)",
                "Miner control",
                "Nodes",
                "Update agents",
                "Hardware & sensors",
                "Economics",
                "Pool & wallet",
                "Settings",
                "Exit")
            .PageSize(12));

        switch (choice)
        {
            case "Dashboard (live)": await dashboard.ShowAsync(cts.Token); break;
            case "Miner control": await miner.ShowAsync(cts.Token); break;
            case "Nodes": await nodes.ShowAsync(cts.Token); break;
            case "Update agents": await UpdateAgentsFromPanelAsync(config, fleet, cts.Token); break;
            case "Hardware & sensors": await hardware.ShowAsync(cts.Token); break;
            case "Economics": await economics.ShowAsync(cts.Token); break;
            case "Pool & wallet": await pool.ShowAsync(cts.Token); break;
            case "Settings": await settings.ShowAsync(cts.Token); break;
            default: return 0;
        }
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C on a screen that was waiting on the network.
}
finally
{
    AnsiConsole.Clear();
}

static async Task UpdateAgentsFromPanelAsync(FleetConfig cfg, FleetService flt, CancellationToken ct)
{
    UiHelpers.Header("Update agents");

    var nodes = UiHelpers.SelectNodes(cfg, "Update which nodes?");
    if (nodes.Count == 0) return;

    var ver = AnsiConsole.Prompt(UiHelpers.Text("Release tag (latest / v1.17.11):").DefaultValue("latest"));
    if (string.IsNullOrWhiteSpace(ver)) ver = "latest";
    var force = AnsiConsole.Confirm("Force reinstall even if same version?", defaultValue: false);

    AnsiConsole.MarkupLine($"[grey]Updating {nodes.Count} node(s) to {UiHelpers.Escape(ver)}...[/]");
    AnsiConsole.WriteLine();

    var failures = 0;
    foreach (var node in nodes.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
    {
        using var client = flt.CreateClient(node);
        MiningFleet.Contracts.AgentInfoDto? before;
        try { before = await client.GetInfoAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            UiHelpers.Result(false, $"{node.Name}: unreachable, skipped ({ex.Message})");
            failures++; continue;
        }

        AnsiConsole.MarkupLine($"[grey]{UiHelpers.Escape(node.Name)}[/] agent {UiHelpers.Escape(before?.AgentVersion ?? "?")} -> updating...");

        try
        {
            var res = await client.UpdateAgentAsync(new MiningFleet.Contracts.AgentUpdateRequestDto { Version = ver == "latest" ? null : ver, Force = force }, ct);
            if (res is null) { UiHelpers.Result(false, $"{node.Name}: no result"); failures++; continue; }
            UiHelpers.Result(res.Ok, $"{node.Name}: {res.Message}");
            if (!res.Ok) { failures++; continue; }
            if (!res.Restarting) continue;
            var after = await WaitForAgentAsync(flt, node, before, ct);
            if (after is not null) AnsiConsole.MarkupLine($"  [green]back up[/] on {UiHelpers.Escape(after.AgentVersion)}");
            else { AnsiConsole.MarkupLine("  [yellow]did not come back within 120s[/]"); failures++; }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AnsiConsole.MarkupLine("  [grey]connection closed during swap, waiting...[/]");
            var after = await WaitForAgentAsync(flt, node, before, ct);
            if (after is not null) AnsiConsole.MarkupLine($"  [green]back up[/] on {UiHelpers.Escape(after.AgentVersion)}");
            else { UiHelpers.Result(false, $"{node.Name}: did not come back ({ex.Message})"); failures++; }
        }
    }

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine(failures == 0 ? "[green]All agents updated.[/]" : $"[yellow]{failures} node(s) need attention.[/]");
    UiHelpers.Pause();
}

static async Task<MiningFleet.Contracts.AgentInfoDto?> WaitForAgentAsync(FleetService flt, NodeConfig node, MiningFleet.Contracts.AgentInfoDto? before, CancellationToken ct)
{
    var started = DateTime.UtcNow;
    while (DateTime.UtcNow - started < TimeSpan.FromSeconds(120))
    {
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        try
        {
            using var c = flt.CreateClient(node, TimeSpan.FromSeconds(5));
            if (await c.GetInfoAsync(ct) is not { } info) continue;
            var younger = info.AgentUptimeSeconds < (DateTime.UtcNow - started).TotalSeconds + 5;
            var moved = before is not null && info.AgentVersion != before.AgentVersion;
            if (younger || moved) return info;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
    }
    return null;
}

return 0;
