using Spectre.Console;

namespace MiningFleet.Console.Ui;

public static class UiHelpers
{
    public static string Escape(string? value) => Markup.Escape(value ?? "");

    public static void Header(string title)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[bold aqua]{Escape(title)}[/]").LeftJustified());
        AnsiConsole.WriteLine();
    }

    public static void Pause(string message = "Enter to go back")
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[grey]{Escape(message)}[/]");
        System.Console.ReadLine();
    }

    public static void Result(bool ok, string message)
    {
        var colour = ok ? "green" : "red";
        var mark = ok ? "OK" : "FAIL";
        AnsiConsole.MarkupLine($"[{colour}]{mark}[/] {Escape(message)}");
    }

    public static string Temperature(double? celsius) => celsius switch
    {
        null => "[grey]-[/]",
        >= 85 => $"[red]{celsius:0}C[/]",
        >= 75 => $"[yellow]{celsius:0}C[/]",
        _ => $"[green]{celsius:0}C[/]",
    };

    public static string StatusBadge(NodeState state) => state switch
    {
        { Online: false } => $"[red]offline[/] [grey]{Escape(state.Error)}[/]",
        // A miner this agent did not start keeps its own API token, so hashrate is unreadable
        // until it is restarted through the fleet. Say so instead of showing a blank rate.
        { Mining: true, Hashrate: 0, Snapshot.Miner.ApiError: not null } => "[yellow]mining (no api)[/]",
        { Mining: true } => "[green]mining[/]",
        { Snapshot.Miner.Installed: false } => "[yellow]no miner[/]",
        _ => "[grey]idle[/]",
    };

    /// <summary>
    /// Huge-page allocation, which on RandomX is worth several times more than the CPU model.
    /// Anything short of 100% is a node leaving hashrate on the table, so only a full grant is
    /// green. Shown as the raw fraction too: 1174/1174 tells the operator the dataset size.
    /// </summary>
    public static string HugePages(MiningFleet.Contracts.MinerStatusDto? miner)
    {
        if (miner is not { Running: true }) return "[grey]-[/]";
        if (miner.HugePagesPercent is not { } fraction) return "[grey]?[/]";

        var colour = fraction switch { >= 1 => "green", >= 0.5 => "yellow", _ => "red" };
        return $"[{colour}]{fraction:P0}[/]";
    }

    /// <summary>The MSR mod is worth roughly 5-15% on RandomX and silently absent without admin.</summary>
    public static string MsrBadge(MiningFleet.Contracts.MinerStatusDto? miner)
    {
        if (miner is not { Running: true }) return "[grey]-[/]";
        return miner.MsrMod is { } msr ? $"[green]{Escape(msr)}[/]" : "[red]no[/]";
    }

    /// <summary>
    /// The throttle rung, which is the first thing to check when a node's hashrate looks wrong.
    ///
    /// A blank cell means the agent predates the feature rather than that nothing is happening -
    /// hence a dash for "off" and a dimmed one for "cannot say", which are different answers.
    /// </summary>
    public static string ThrottleBadge(MiningFleet.Contracts.ThrottleStatusDto? throttle)
    {
        if (throttle is null) return "[grey]?[/]";
        if (!throttle.Enabled) return "[grey]-[/]";

        var colour = throttle.Level switch { >= 100 => "green", >= 50 => "yellow", > 0 => "darkorange", _ => "red" };
        var pin = throttle.Manual ? "*" : "";
        return $"[{colour}]{throttle.Level}%{pin}[/]";
    }

    /// <summary>
    /// What the graphics card is doing, with the miner's own unit attached because the number
    /// means nothing without it: 4.5 g/s of Cuckaroo29 out-earns 62 Mh/s of NexaPoW twelvefold.
    ///
    /// Three different silences, deliberately distinguished. A dimmed question mark is an agent
    /// that predates GPU mining; a dash is a card that could mine and is not; a reason in words is
    /// a card handed back to somebody who is using the machine.
    /// </summary>
    public static string GpuBadge(MiningFleet.Contracts.GpuMinerStatusDto? gpu)
    {
        if (gpu is null) return "[grey]?[/]";
        if (!gpu.Running)
            return gpu.Notice is { Length: > 0 } notice ? $"[grey]{Escape(notice)}[/]" : "[grey]-[/]";

        if (gpu.Hashrate is not { } rate) return "[yellow]starting[/]";
        return $"[green]{rate:N2} {Escape(gpu.HashrateUnit ?? "")}[/]".Replace(" [/]", "[/]");
    }

    /// <summary>
    /// Accepted shares against stale ones. Worth its own cell: a stale rate of 18% was how a
    /// mis-set process priority announced itself on a live node, and nothing else showed it. A
    /// stale share is work the pool will not pay for.
    /// </summary>
    public static string GpuShares(MiningFleet.Contracts.GpuMinerStatusDto? gpu)
    {
        if (gpu is null) return "[grey]?[/]";
        if (gpu.AcceptedShares is not { } accepted) return "[grey]-[/]";

        var stale = (gpu.StaleShares ?? 0) + (gpu.RejectedShares ?? 0);
        if (stale == 0) return $"[green]{accepted}[/]";

        var colour = stale * 10 > accepted ? "red" : "yellow";
        return $"[{colour}]{accepted}/{stale}[/]";
    }

    public static string Money(double? amount, string currency) =>
        amount is null ? "[grey]-[/]" : $"{amount.Value:N2} {Escape(currency)}";

    public static string Signed(double? amount, string currency)
    {
        if (amount is null) return "[grey]-[/]";
        var colour = amount.Value >= 0 ? "green" : "red";
        return $"[{colour}]{amount.Value:N2} {Escape(currency)}[/]";
    }

    /// <summary>
    /// A free-text prompt whose default value is safe to display.
    ///
    /// <see cref="TextPrompt{T}"/> renders its default as markup, so a path such as
    /// <c>C:\mining\[rig]\xmrig</c> is read as a style tag and crashes the screen. Escaping
    /// happens in the converter, which is display-only: the value handed back is still the
    /// raw one the operator typed or accepted.
    /// </summary>
    public static TextPrompt<string> Text(string label) =>
        new TextPrompt<string>(label).WithConverter(Escape);

    /// <summary>
    /// A menu that wraps from the last entry back to the first, and that answers Escape with
    /// <paramref name="escapeTo"/>.
    ///
    /// Both are Spectre settings rather than anything written here, and both are off by default.
    /// Wrapping is harmless. The cancel result is not: Spectre hands it back as an ordinary
    /// answer, indistinguishable from a choice the operator made. A menu read with <c>== "on"</c>
    /// or falling through a <c>default:</c> would therefore treat Escape as a decision - once
    /// measured as pushing "autostart off" to every selected node. So the way out is named here,
    /// beside the choices, and every caller has to handle it like any other answer.
    /// </summary>
    public static SelectionPrompt<string> Menu(string title, string escapeTo, params string[] choices)
    {
        if (!choices.Contains(escapeTo))
            throw new ArgumentException($"'{escapeTo}' must be one of the menu's own choices.", nameof(escapeTo));

        return new SelectionPrompt<string>()
            .Title(Escape(title))
            .WrapAround(true)
            .AddCancelResult(escapeTo)
            .AddChoices(choices);
    }

    // Prompt choices are rendered as markup, so a name carrying [ or ] would be read as a
    // style tag and crash the prompt. Selecting the objects themselves and escaping only in
    // the converter keeps the display safe without mangling the underlying value.
    private static readonly NodeConfig BackChoice = new() { Name = "< back" };
    private static readonly NodeConfig AllChoice = new() { Name = "* all enabled nodes" };

    /// <summary>Picks one node, or null when the fleet is empty or the user backs out.</summary>
    public static NodeConfig? SelectNode(FleetConfig config, string prompt = "Node")
    {
        if (config.Nodes.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No nodes configured yet.[/]");
            Pause();
            return null;
        }

        var picked = AnsiConsole.Prompt(new SelectionPrompt<NodeConfig>()
            .Title(Escape(prompt))
            .PageSize(15)
            .WrapAround(true)
            // The sentinel itself, not a copy: Spectre returns the cancel value as the same
            // instance, which is what the ReferenceEquals below is already checking for.
            .AddCancelResult(BackChoice)
            .UseConverter(n => ReferenceEquals(n, BackChoice) ? "< back" : Escape(n.ToString()))
            .AddChoices(config.Nodes.Append(BackChoice)));

        return ReferenceEquals(picked, BackChoice) ? null : picked;
    }

    /// <summary>Picks any number of nodes; an empty result means the user chose nothing.</summary>
    public static IReadOnlyList<NodeConfig> SelectNodes(FleetConfig config, string prompt)
    {
        var enabled = config.Nodes.Where(n => n.Enabled).ToList();
        if (enabled.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No enabled nodes.[/]");
            Pause();
            return [];
        }

        var picked = AnsiConsole.Prompt(new MultiSelectionPrompt<NodeConfig>()
            .Title(Escape(prompt))
            .PageSize(15)
            .WrapAround(true)
            // The no-argument overload, which cancels to an empty list. The overload that takes an
            // item would answer Escape with that item selected - and the item nearest to hand here
            // is "all enabled nodes", so a mis-key would aim a fleet-wide command at the fleet.
            .AddCancelResult()
            .InstructionsText("[grey](space to toggle, enter to confirm)[/]")
            .UseConverter(n => ReferenceEquals(n, AllChoice) ? "* all enabled nodes" : Escape(n.ToString()))
            .AddChoices(enabled.Prepend(AllChoice)));

        return picked.Any(n => ReferenceEquals(n, AllChoice))
            ? enabled
            : picked.Where(n => !ReferenceEquals(n, AllChoice)).ToList();
    }
}
