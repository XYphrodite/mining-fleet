using Spectre.Console;

namespace MiningFleet.Console.Ui;

/// <summary>Electricity cost, projected income and the per-node breakdown.</summary>
public sealed class EconomicsScreen
{
    private readonly FleetConfig _config;
    private readonly FleetService _fleet;
    private readonly MarketService _market;
    private readonly GpuPoolService _gpuPool = new();

    public EconomicsScreen(FleetConfig config, FleetService fleet, MarketService market)
    {
        _config = config;
        _fleet = fleet;
        _market = market;
    }

    public async Task ShowAsync(CancellationToken ct)
    {
        UiHelpers.Header("Economics");

        IReadOnlyList<NodeState> states = [];
        PoolNetworkStats? network = null;
        PoolWalletStats? wallet = null;
        double? price = null;
        MoneyFormat money = MoneyFormat.Single(_config.Electricity.Currency);

        await AnsiConsole.Status().StartAsync("Collecting fleet and market data...", async _ =>
        {
            var pollTask = _fleet.PollAsync(ct);
            var networkTask = _market.GetNetworkStatsAsync(ct);
            var walletTask = _market.GetWalletStatsAsync(ct);
            var priceTask = _market.GetPriceAsync(ct);
            await Task.WhenAll(pollTask, networkTask, walletTask, priceTask);
            states = pollTask.Result;
            network = networkTask.Result;
            wallet = walletTask.Result;
            price = priceTask.Result;
            money = await _market.GetMoneyFormatAsync(ct);
        });

        var currency = _config.Electricity.Currency;
        var economics = Economics.Calculate(states, _config, network, price);

        var summary = new Grid().AddColumn().AddColumn();
        summary.AddRow("[grey]Mining nodes[/]", $"{states.Count(s => s.Mining)} of {states.Count}");
        summary.AddRow("[grey]Fleet hashrate[/]", $"[aqua]{Economics.FormatHashrate(economics.TotalHashrate)}[/]");
        summary.AddRow("[grey]Power draw[/]", $"{economics.TotalWatts:0} W");
        var overrides = _config.Nodes.Count(n => n.Enabled && n.PricePerKwh is not null);
        summary.AddRow("[grey]Electricity[/]",
            $"{_config.Electricity.PricePerKwh:N2} {UiHelpers.Escape(currency)} / kWh"
            + (overrides > 0 ? $" [grey](default; {overrides} node(s) on their own tariff)[/]" : ""));
        summary.AddRow("[grey]XMR price[/]", price is null
            ? $"[yellow]no price available in {UiHelpers.Escape(currency)}[/]"
            : money.Markup(price));
        summary.AddRow("[grey]Network hashrate[/]", network?.NetworkHashrate is { } nh ? Economics.FormatHashrate(nh) : "[yellow]pool API unavailable[/]");
        AnsiConsole.Write(new Panel(summary).Header("[bold]Inputs[/]").Border(BoxBorder.Rounded).Expand());

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .Title("[bold]Per period[/]")
            .AddColumn("Period")
            .AddColumn(new TableColumn("Income XMR").RightAligned())
            .AddColumn(new TableColumn("Income").RightAligned())
            .AddColumn(new TableColumn("Electricity").RightAligned())
            .AddColumn(new TableColumn("Profit").RightAligned());

        foreach (var (label, factor) in new[] { ("Hour", 1 / 24.0), ("Day", 1.0), ("Week", 7.0), ("Month (30d)", 30.0) })
        {
            table.AddRow(
                label,
                economics.XmrPerDay is { } xmr ? $"{xmr * factor:0.00000}" : "[grey]-[/]",
                money.Markup(economics.RevenuePerDay * factor),
                money.Markup(economics.CostPerDay * factor),
                money.Signed(economics.ProfitPerDay * factor));
        }

        AnsiConsole.Write(table);

        if (economics.CostPerXmr is { } costPerXmr)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Cost to mine 1 XMR:[/] {money.Markup(costPerXmr)}  " +
                $"[grey](break-even XMR price at current draw)[/]");
        }

        await ShowCardsAsync(states, currency, money, ct);

        AnsiConsole.WriteLine();
        var breakdown = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .Title("[bold]Per node, per day[/]")
            .AddColumn("Node")
            .AddColumn(new TableColumn("Hashrate").RightAligned())
            .AddColumn(new TableColumn("Share").RightAligned())
            .AddColumn(new TableColumn("Watts").RightAligned())
            .AddColumn(new TableColumn($"{UiHelpers.Escape(currency)}/kWh").RightAligned())
            .AddColumn(new TableColumn("Cost").RightAligned())
            .AddColumn(new TableColumn("Income").RightAligned())
            .AddColumn(new TableColumn("Profit").RightAligned());

        foreach (var state in states.Where(s => s.Mining).OrderByDescending(s => s.Hashrate))
        {
            // Income is split by hashrate share, cost by actual draw: that is what makes
            // one node profitable and another not.
            var share = economics.TotalHashrate > 0 ? state.Hashrate / economics.TotalHashrate : 0;
            var cost = Economics.DailyCost(state, _config);
            var income = economics.RevenuePerDay * share;

            breakdown.AddRow(
                new Markup(UiHelpers.Escape(state.Node.Name)),
                new Markup(Economics.FormatHashrate(state.Hashrate)),
                new Markup($"{share:P1}"),
                new Markup($"{state.PowerWatts:0}"),
                // A node on the fleet default is dimmed, so an overridden tariff stands out.
                new Markup(state.Node.PricePerKwh is { } own
                    ? $"{own:N2}"
                    : $"[grey]{_config.Electricity.PricePerKwh:N2}[/]"),
                new Markup(money.Markup(cost)),
                new Markup(money.Markup(income)),
                new Markup(money.Signed(income - cost)));
        }

        AnsiConsole.Write(breakdown);

        RenderReconciliation(economics, wallet, price, currency, money);

        UiHelpers.Pause();
    }

    /// <summary>
    /// Puts the estimate next to what the pool actually credited, so the operator does not
    /// have to hold two screens in their head. A large gap is a signal worth chasing: a node
    /// mining to the wrong wallet, a miner submitting no shares, or simply pool variance.
    /// </summary>
    private void RenderReconciliation(FleetEconomics economics, PoolWalletStats? wallet, double? price, string currency, MoneyFormat money)
    {
        AnsiConsole.WriteLine();

        var grid = new Grid().AddColumn().AddColumn().AddColumn();
        grid.AddRow(
            new Markup("[grey]Estimated per day[/]"),
            new Markup(economics.XmrPerDay is { } est ? $"{est:0.000000} XMR" : "[grey]-[/]"),
            new Markup(money.Markup(economics.RevenuePerDay)));
        grid.AddRow(
            new Markup("[grey]Credited by the pool[/]"),
            new Markup(wallet?.CreditedTodayXmr is { } act ? $"{act:0.000000} XMR" : "[grey]-[/]"),
            new Markup(wallet?.CreditedTodayXmr is { } act2 && price is { } p
                ? money.Markup(act2 * p)
                : "[grey]-[/]"));

        if (economics.XmrPerDay is > 0 && wallet?.CreditedTodayXmr is { } credited)
        {
            var ratio = credited / economics.XmrPerDay.Value;
            var colour = ratio switch { >= 0.8 and <= 1.25 => "green", >= 0.5 => "yellow", _ => "red" };
            grid.AddRow(
                new Markup("[grey]Actual vs estimate[/]"),
                new Markup($"[{colour}]{ratio:P0}[/]"),
                new Markup(""));
        }

        AnsiConsole.Write(new Panel(grid)
            .Header("[bold]Estimate against the pool[/]")
            .Border(BoxBorder.Rounded)
            .Expand());

        AnsiConsole.MarkupLine(
            "[grey]The estimate extrapolates the hashrate measured right now across a whole day; " +
            "the pool figure is a rolling 24h for the entire wallet, including any machine outside " +
            "this fleet. They diverge on variance alone, so read a gap as a hint, not a verdict.[/]");
    }

    /// <summary>
    /// What the graphics cards earned, which nothing above this line knows about: the tables are
    /// built from Hashvault, and Hashvault has never heard of the coin a card mines.
    ///
    /// The settings come from each node rather than from fleet.json, because the node is where they
    /// are true. A card set up by pushing settings straight to an agent - which is how the fleet's
    /// first one was - leaves nothing behind in the operator's config to read.
    ///
    /// Unlike everything else on this screen these are not estimates. They are sums of payments the
    /// pool has already made, which is why they are shown per coin and not folded into the profit
    /// line above until the operator can see both.
    /// </summary>
    private async Task ShowCardsAsync(
        IReadOnlyList<NodeState> states, string currency, MoneyFormat money, CancellationToken ct)
    {
        var mining = states.Where(s => s.GpuMining).ToList();
        if (mining.Count == 0) return;

        var found = new List<(NodeState Node, GpuPoolStats Stats, double? Price)>();
        var unreadable = new List<string>();

        await AnsiConsole.Status().StartAsync("Reading what the cards earned...", async _ =>
        {
            foreach (var state in mining)
            {
                using var client = _fleet.CreateClient(state.Node);
                var config = await SafeConfigAsync(client, ct);

                if (GpuPoolService.TargetFor(ToConfig(config)) is not { } target)
                {
                    unreadable.Add(state.Node.Name);
                    continue;
                }

                var stats = await _gpuPool.GetAsync(target, ct);
                if (stats is null) { unreadable.Add(state.Node.Name); continue; }

                found.Add((state, stats, await _gpuPool.GetPriceAsync(target, currency, ct)));
            }
        });

        if (found.Count == 0 && unreadable.Count == 0) return;

        AnsiConsole.WriteLine();
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .Title("[bold]Graphics cards, from payments the pool actually made[/]")
            .AddColumn("Node")
            .AddColumn("Coin")
            .AddColumn(new TableColumn("Per day").RightAligned())
            .AddColumn(new TableColumn("Value/day").RightAligned())
            .AddColumn(new TableColumn("Paid out").RightAligned())
            .AddColumn(new TableColumn("On the pool").RightAligned())
            .AddColumn("Measured over");

        var now = DateTimeOffset.UtcNow;
        double? valuePerDay = null;

        foreach (var (state, stats, price) in found)
        {
            var perDay = stats.PaidPerDay();
            var span = stats.PayoutSpan();
            double? value = perDay is { } p && price is { } q ? p * q : null;
            if (value is { } v) valuePerDay = (valuePerDay ?? 0) + v;

            table.AddRow(
                new Markup(UiHelpers.Escape(state.Node.Name)),
                new Markup(UiHelpers.Escape(stats.Target.Coin.ToUpperInvariant())),
                new Markup(perDay is { } d ? $"[aqua]{d:N0}[/]" : "[grey]-[/]"),
                new Markup(value is null ? $"[grey]price unavailable in {UiHelpers.Escape(currency)}[/]" : money.Markup(value)),
                new Markup(stats.Paid is { } paid ? $"{paid:N2}" : "[grey]-[/]"),
                new Markup(stats.Pending is { } pending
                    ? $"{pending:N2}" + (stats.Threshold is { } t ? $" [grey]/ {t:N0}[/]" : "")
                    : "[grey]-[/]"),
                // Naming the window is the point: two payouts four hours apart do not describe a
                // day, and a rate presented without its window invites being read as one. The age
                // of the last payment is beside it because a rate measured over a stale window is
                // history, not a forecast.
                new Markup(span is { } s
                    ? $"[grey]{s.TotalHours:N0} h, {stats.Payouts.Count} payout(s)[/]" + Age(stats, now)
                    : "[yellow]too few payouts to say[/]"));
        }

        AnsiConsole.Write(table);

        if (valuePerDay is { } total)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Cards add[/] {money.Markup(total)} [grey]a day that the tables above do not " +
                "count - they are built from Hashvault, which only knows Monero.[/]");
        }

        foreach (var name in unreadable)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{UiHelpers.Escape(name)}[/] [grey]is mining on a card whose pool this console " +
                "cannot read. Only Kryptex is understood; the electricity still counts against it.[/]");
        }
    }

    /// <summary>Flags a rate whose newest payment is old enough that it describes the past.</summary>
    private static string Age(GpuPoolStats stats, DateTimeOffset now)
    {
        if (stats.LastPayoutAt is not { } last) return "";
        var since = now - last;
        return since < TimeSpan.FromHours(12)
            ? ""
            : $" [yellow]last paid {since.TotalHours:N0} h ago[/]";
    }

    private static async Task<MiningFleet.Contracts.MinerConfigDto?> SafeConfigAsync(AgentClient client, CancellationToken ct)
    {
        try { return await client.GetConfigAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The node's own answer, in the shape the target reader expects.</summary>
    private static GpuMinerConfig? ToConfig(MiningFleet.Contracts.MinerConfigDto? config) =>
        config?.GpuMiner is not { } gpu
            ? null
            : new GpuMinerConfig { Enabled = gpu.Enabled, PoolUrl = gpu.PoolUrl, User = gpu.User };
}
