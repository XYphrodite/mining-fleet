using Spectre.Console;
using MiningFleet.Contracts;

namespace MiningFleet.Console.Ui;

/// <summary>Start/stop/restart mining, install or update xmrig, read the miner log tail.</summary>
public sealed class MinerScreen
{
    private readonly FleetConfig _config;
    private readonly FleetService _fleet;

    public MinerScreen(FleetConfig config, FleetService fleet)
    {
        _config = config;
        _fleet = fleet;
    }

    public async Task ShowAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UiHelpers.Header("Miner control");

            var choice = AnsiConsole.Prompt(UiHelpers.Menu("Action", "< back",
                    "Start mining",
                    "Stop mining",
                    "Restart mining",
                    "Install / update xmrig",
                    "Push pool settings to nodes",
                    "Start mining when the node boots",
                    "Session monitor (hashrate workaround)",
                    "Power limit while the PC is in use",
                    "View miner log",
                    "< back"));

            switch (choice)
            {
                case "Start mining": await RunAsync("Starting", (c, t) => c.StartAsync(t), ct); break;
                case "Stop mining": await RunAsync("Stopping", (c, t) => c.StopAsync(t), ct); break;
                case "Restart mining": await RunAsync("Restarting", (c, t) => c.RestartAsync(t), ct); break;
                case "Install / update xmrig": await InstallAsync(ct); break;
                case "Push pool settings to nodes": await PushAsync(ct); break;
                case "Start mining when the node boots": await AutoStartAsync(ct); break;
                case "Session monitor (hashrate workaround)": await SessionMonitorAsync(ct); break;
                case "Power limit while the PC is in use": await ThrottleAsync(ct); break;
                case "View miner log": await LogsAsync(ct); break;
                default: return;
            }
        }
    }

    private async Task RunAsync(string verb, Func<AgentClient, CancellationToken, Task<CommandResultDto?>> action, CancellationToken ct)
    {
        var nodes = UiHelpers.SelectNodes(_config, $"{verb} on which nodes?");
        if (nodes.Count == 0) return;

        IReadOnlyList<(NodeConfig Node, CommandResultDto Result)> results = [];
        await AnsiConsole.Status().StartAsync($"{verb} on {nodes.Count} node(s)...", async _ =>
        {
            results = await _fleet.ForEachAsync(nodes, action, ct);
        });

        AnsiConsole.WriteLine();
        foreach (var (node, result) in results.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
            UiHelpers.Result(result.Ok, $"{node.Name}: {result.Message}");

        UiHelpers.Pause();
    }

    private async Task InstallAsync(CancellationToken ct)
    {
        UiHelpers.Header("Install / update xmrig");

        var nodes = UiHelpers.SelectNodes(_config, "Install on which nodes?");
        if (nodes.Count == 0) return;

        var defaultPath = nodes.Select(n => n.MinerPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))
                          ?? (OperatingSystem.IsWindows() ? @"C:\mining\xmrig" : "/opt/xmrig");

        var targetPath = AnsiConsole.Prompt(UiHelpers.Text("Install directory on the nodes:").DefaultValue(defaultPath));
        var version = AnsiConsole.Prompt(UiHelpers.Text("Release tag (or 'latest'):").DefaultValue("latest"));
        var restart = AnsiConsole.Confirm("Restart the miner afterwards if it was running?", defaultValue: true);

        AnsiConsole.MarkupLine($"[grey]Each node downloads xmrig itself from GitHub, so it needs outbound internet.[/]");
        if (!AnsiConsole.Confirm($"Install to [bold]{UiHelpers.Escape(targetPath)}[/] on {nodes.Count} node(s)?", defaultValue: true))
            return;

        var request = new InstallRequestDto
        {
            TargetPath = targetPath,
            Version = version,
            RestartAfterInstall = restart,
        };

        var results = new List<(string Node, InstallResultDto? Result, string? Error)>();
        await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new SpinnerColumn(), new ElapsedTimeColumn())
            .StartAsync(async progress =>
            {
                var tasks = nodes.Select(async node =>
                {
                    var task = progress.AddTask(UiHelpers.Escape(node.Name), maxValue: 1);
                    using var client = _fleet.CreateClient(node, TimeSpan.FromMinutes(6));
                    try
                    {
                        var result = await client.InstallAsync(request, ct);
                        lock (results) results.Add((node.Name, result, null));
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                    {
                        lock (results) results.Add((node.Name, null, ex.Message));
                    }
                    finally
                    {
                        task.Increment(1);
                        task.StopTask();
                    }
                });
                await Task.WhenAll(tasks);
            });

        AnsiConsole.WriteLine();
        foreach (var (name, result, error) in results.OrderBy(r => r.Node, StringComparer.OrdinalIgnoreCase))
        {
            if (error is not null)
            {
                UiHelpers.Result(false, $"{name}: {error}");
                continue;
            }
            UiHelpers.Result(result?.Ok == true, $"{name}: {result?.Message ?? "no response"}");
        }

        // Remember the path so the next install and the config push prefill correctly.
        foreach (var node in nodes) node.MinerPath = targetPath;
        _config.Save();

        UiHelpers.Pause();
    }

    /// <summary>
    /// Turns autostart on or off per node, and reports what each node is set to now.
    ///
    /// The agent has always had the flag, but only in its own appsettings.json, which meant a
    /// remote hand edit and a service restart per node. It belongs here because it is the
    /// setting that decides whether an outage costs an hour or costs until somebody notices.
    /// </summary>
    private async Task AutoStartAsync(CancellationToken ct)
    {
        UiHelpers.Header("Start mining when the node boots");

        AnsiConsole.MarkupLine(
            "The agent puts the miner to work as soon as it starts, so a node that came back on");
        AnsiConsole.MarkupLine(
            "its own - mains failure, bugcheck, service restart - returns to mining with nobody");
        AnsiConsole.MarkupLine(
            "signed in to tell it to.");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "[yellow]This does not start mining now[/], and it does not overrule the power limit: a");
        AnsiConsole.MarkupLine(
            "node the limit took to zero stays down, because somebody is using that machine.");
        AnsiConsole.MarkupLine(
            "[grey]A node never told either way keeps doing whatever its own appsettings.json says.[/]");
        AnsiConsole.WriteLine();

        var nodes = UiHelpers.SelectNodes(_config, "Change which nodes?");
        if (nodes.Count == 0) return;

        IReadOnlyList<(NodeConfig Node, CommandResultDto Result)> current = [];
        await AnsiConsole.Status().StartAsync("Reading current settings...", async _ =>
        {
            current = await _fleet.ForEachAsync(nodes, FleetService.AutoStartAction(null), ct);
        });

        AnsiConsole.MarkupLine("[grey]Now:[/]");
        foreach (var (node, result) in current.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
            UiHelpers.Result(result.Ok, $"{node.Name}: {result.Message}");
        AnsiConsole.WriteLine();

        // A two-answer menu read with `== "on"` has no way to say "never mind", and Escape would
        // have had to mean one of them: "off", pushed to every node picked above. Turning the
        // fleet's autostart off is exactly the setting that leaves a rebooted rig idle until
        // somebody notices, so the third answer is spelled out rather than inferred.
        var answer = AnsiConsole.Prompt(UiHelpers.Menu("Autostart should be", "< back", "on", "off", "< back"));
        if (answer == "< back") return;
        var enable = answer == "on";

        IReadOnlyList<(NodeConfig Node, CommandResultDto Result)> results = [];
        await AnsiConsole.Status().StartAsync(enable ? "Enabling..." : "Disabling...", async _ =>
        {
            results = await _fleet.ForEachAsync(nodes, FleetService.AutoStartAction(enable), ct);
        });

        foreach (var (node, result) in results.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
            UiHelpers.Result(result.Ok, $"{node.Name}: {result.Message}");

        UiHelpers.Pause();
    }

    /// <summary>
    /// Turns the Task Manager workaround on or off per node. Presented with its own measurement
    /// and its own catch, because an operator switching this on deserves to know both that it is
    /// worth 62% and that nobody can explain why.
    /// </summary>
    private async Task SessionMonitorAsync(CancellationToken ct)
    {
        UiHelpers.Header("Session monitor");

        AnsiConsole.MarkupLine(
            "Keeps a monitor window open, hidden, in the node's own logged-on session.");
        AnsiConsole.MarkupLine(
            "[yellow]Resource Monitor, not Task Manager[/]: both are single-instance, so a hidden one");
        AnsiConsole.MarkupLine(
            "makes that tool unopenable for whoever sits at the machine - Ctrl+Shift+Esc does");
        AnsiConsole.MarkupLine(
            "nothing at all. Resource Monitor is worth the same hashrate and is missed far less.");
        AnsiConsole.MarkupLine(
            "[grey]Measured on an i7-12700KF: 4,380 H/s with nothing watching, 7,092 H/s with[/]");
        AnsiConsole.MarkupLine(
            "[grey]Task Manager open. Eleven explanations were tested and none held, so this is[/]");
        AnsiConsole.MarkupLine(
            "[grey]a remedy without a diagnosis - it may stop working after a Windows update.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "[yellow]Somebody has to be logged on[/] for this to work: with no session there is no");
        AnsiConsole.MarkupLine(
            "desktop to put the window on. The agent watches for one and starts it whenever a");
        AnsiConsole.MarkupLine(
            "person signs in, so a node that reboots picks it up again on its own.");
        AnsiConsole.WriteLine();

        var nodes = UiHelpers.SelectNodes(_config, "Change which nodes?");
        if (nodes.Count == 0) return;

        // Same shape as autostart and a costlier mistake: switching this off was measured at
        // 7,092 -> 4,380 H/s on one node, so Escape must not be able to mean "off".
        var answer = AnsiConsole.Prompt(UiHelpers.Menu("Session monitor should be", "< back", "on", "off", "< back"));
        if (answer == "< back") return;
        var enable = answer == "on";

        var results = new List<(string Node, bool Ok, string Message)>();
        await AnsiConsole.Status().StartAsync(enable ? "Enabling..." : "Disabling...", async _ =>
        {
            var tasks = nodes.Select(async node =>
            {
                using var client = _fleet.CreateClient(node, TimeSpan.FromSeconds(60));
                try
                {
                    var pushed = await client.PutConfigAsync(new MinerConfigDto { KeepMonitorOpen = enable }, ct);

                    // Read back what the node actually did rather than echoing the request. A push
                    // that lands is not a window that opened: the monitor can fail to start, and
                    // saying "session monitor on" over that is how a rig sat at 60% of its
                    // hashrate for a fortnight with nothing to show for the setting.
                    var snapshot = pushed is null ? null : await client.GetStatusAsync(ct);
                    var message = snapshot?.MonitorNotice
                        ?? (enable ? "session monitor on" : "session monitor off");

                    lock (results) results.Add((node.Name, pushed is not null, message));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    lock (results) results.Add((node.Name, false, ex.Message));
                }
            });
            await Task.WhenAll(tasks);
        });

        foreach (var (name, ok, message) in results.OrderBy(r => r.Node, StringComparer.OrdinalIgnoreCase))
            UiHelpers.Result(ok, $"{name}: {message}");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Give it a minute, then check the dashboard: the change is not instant.[/]");
        UiHelpers.Pause();
    }

    private async Task PushAsync(CancellationToken ct)
    {
        UiHelpers.Header("Push pool settings");

        if (string.IsNullOrWhiteSpace(_config.Pool.Wallet))
        {
            AnsiConsole.MarkupLine("[red]No wallet configured.[/] Set it under Settings first.");
            UiHelpers.Pause();
            return;
        }

        var nodes = UiHelpers.SelectNodes(_config, "Push to which nodes?");
        if (nodes.Count == 0) return;

        AnsiConsole.MarkupLine($"[grey]pool[/] {UiHelpers.Escape(_config.Pool.Url)}");
        AnsiConsole.MarkupLine($"[grey]wallet[/] {UiHelpers.Escape(_config.Pool.Wallet)}");
        AnsiConsole.MarkupLine(string.IsNullOrWhiteSpace(_config.Pool.Password)
            ? "[grey]worker name[/] = node name (the pool password field)"
            : $"[grey]worker name[/] {UiHelpers.Escape(_config.Pool.Password)} [grey]on every node[/]");
        AnsiConsole.WriteLine();

        var results = new List<(string Node, bool Ok, string Message)>();
        await AnsiConsole.Status().StartAsync("Pushing...", async _ =>
        {
            var tasks = nodes.Select(async node =>
            {
                using var client = _fleet.CreateClient(node, TimeSpan.FromSeconds(20));
                try
                {
                    var pushed = await client.PutConfigAsync(new MinerConfigDto
                    {
                        ExecutablePath = node.MinerPath,
                        PoolUrl = _config.Pool.Url,
                        Wallet = _config.Pool.Wallet,
                        // Hashvault reads the worker name from the password field, not from
                        // an address suffix. Leaving the fleet password blank therefore gives
                        // every rig its own name in the pool's worker list; setting one merges
                        // them under it.
                        WorkerName = null,
                        Password = string.IsNullOrWhiteSpace(_config.Pool.Password) ? node.Name : _config.Pool.Password,
                        PowerFallbackWatts = node.PowerFallbackWatts,
                    }, ct);
                    lock (results) results.Add((node.Name, pushed is not null, pushed?.ExecutablePath ?? "config saved"));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    lock (results) results.Add((node.Name, false, ex.Message));
                }
            });
            await Task.WhenAll(tasks);
        });

        foreach (var (name, ok, message) in results.OrderBy(r => r.Node, StringComparer.OrdinalIgnoreCase))
            UiHelpers.Result(ok, $"{name}: {message}");

        AnsiConsole.MarkupLine("[grey]Restart the miner for new pool settings to take effect.[/]");
        UiHelpers.Pause();
    }

    /// <summary>
    /// Turns the power limit on or off per node, or pins a rung by hand.
    ///
    /// The thresholds themselves are edited in fleet.json rather than here. They are a ladder of
    /// numbers that wants reading beside the node's own decision log, and a prompt that walks an
    /// operator through five rungs one at a time would be a worse way to do it than an editor.
    /// </summary>
    private async Task ThrottleAsync(CancellationToken ct)
    {
        UiHelpers.Header("Power limit while the PC is in use");

        AnsiConsole.MarkupLine(
            "Holds the miner back while somebody is working on the machine, and lets it run flat");
        AnsiConsole.MarkupLine(
            "out when they leave. The rung is read against CPU used by everything except the miner.");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "[grey]Coming down is immediate; going back up waits for the machine to stay quiet, so a[/]");
        AnsiConsole.MarkupLine(
            "[grey]single burst of activity does not rock the miner up and down.[/]");
        AnsiConsole.MarkupLine(
            "[grey]At 0% the miner is stopped outright rather than capped, because a capped miner[/]");
        AnsiConsole.MarkupLine(
            "[grey]still holds about 2.3 GB for its dataset - and on a 16 GB node that memory is[/]");
        AnsiConsole.MarkupLine(
            "[grey]what makes the machine feel slow.[/]");
        AnsiConsole.WriteLine();

        var current = _config.Throttle;
        AnsiConsole.MarkupLine(
            $"Rules in [blue]{UiHelpers.Escape(_config.Path)}[/]: floor [aqua]{current.FloorLevel ?? 0}%[/], " +
            $"back up after [aqua]{current.RampUpSeconds ?? 120}s[/] of quiet.");
        AnsiConsole.WriteLine();

        var nodes = UiHelpers.SelectNodes(_config, "Change which nodes?");
        if (nodes.Count == 0) return;

        // "pinned by hand" sits on the `default:` arm below, so without a way out of its own this
        // menu would answer Escape by asking for a percentage and then writing fleet.json.
        var choice = AnsiConsole.Prompt(
            UiHelpers.Menu("Power limit should be", "< back", "automatic", "off", "pinned by hand", "< back"));
        if (choice == "< back") return;

        int? pinned = null;
        var clearManual = false;

        switch (choice)
        {
            case "automatic":
                foreach (var node in nodes) EnableThrottle(node, true);
                clearManual = true;
                break;

            case "off":
                foreach (var node in nodes) EnableThrottle(node, false);
                clearManual = true;
                break;

            default:
                pinned = AnsiConsole.Prompt(
                    new TextPrompt<int>("Hold the miner at which percent?")
                        .DefaultValue(50)
                        .Validate(v => v is >= 0 and <= 100
                            ? ValidationResult.Success()
                            : ValidationResult.Error("[red]0 to 100[/]")));
                foreach (var node in nodes) EnableThrottle(node, true);
                break;
        }

        _config.Save();

        var results = await AnsiConsole.Status().StartAsync("Pushing...", async _ =>
            await _fleet.PushThrottleAsync(nodes, pinned, clearManual, ct));

        foreach (var (node, result) in results.OrderBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase))
            UiHelpers.Result(result.Ok, $"{node.Name}: {result.Message}");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]The Pwr column on the dashboard shows the rung each node is on.[/]");
        UiHelpers.Pause();
    }

    /// <summary>
    /// Records the choice against the node rather than the fleet, because it is a property of the
    /// machine: one rig has somebody sitting at it and the next one does not.
    /// </summary>
    private static void EnableThrottle(NodeConfig node, bool enabled)
    {
        node.Throttle ??= new ThrottleConfig();
        node.Throttle.Enabled = enabled;
    }

    private async Task LogsAsync(CancellationToken ct)
    {
        var node = UiHelpers.SelectNode(_config, "Log from which node?");
        if (node is null) return;

        UiHelpers.Header($"{node.Name} - xmrig output");

        using var client = _fleet.CreateClient(node, TimeSpan.FromSeconds(15));
        try
        {
            var logs = await client.GetLogsAsync(ct);
            if (logs is null || logs.Lines.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No output captured. The agent only records output of miners it started itself.[/]");
            }
            else
            {
                foreach (var line in logs.Lines.TakeLast(40))
                    AnsiConsole.MarkupLine($"[grey]{UiHelpers.Escape(line)}[/]");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            UiHelpers.Result(false, ex.Message);
        }

        UiHelpers.Pause();
    }
}
