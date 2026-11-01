using Spectre.Console;

namespace MiningFleet.Console.Ui;

/// <summary>Electricity cost, projected income and the per-node breakdown.</summary>
public sealed class EconomicsScreen
{
    private readonly FleetConfig _config;
    private readonly FleetService _fleet;
    private readonly MarketService _market;
    private readonly GpuPoolService _gpuPool = new();
    private readonly GpuIncomeService _gpuIncome;
    private readonly XtmIncomeService _xtm = new();

    public EconomicsScreen(FleetConfig config, FleetService fleet, MarketService market)
    {
        _config = config;
        _fleet = fleet;
        _market = market;
        _gpuIncome = new GpuIncomeService(_gpuPool, GpuIncomeLedger.For(config));
    }

    public async Task ShowAsync(CancellationToken ct)
    {
        UiHelpers.Header("Economics");

        IReadOnlyList<NodeState> states = [];
        PoolNetworkStats? network = null;
        PoolWalletStats? wallet = null;
        double? price = null;
        IReadOnlyDictionary<string, NodeGpuQuote> gpuQuotes = new Dictionary<string, NodeGpuQuote>();
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
            var quotesTask = _gpuPool.ResolveQuotesAsync(states, _config, _fleet, ct);
            var moneyTask = _market.GetMoneyFormatAsync(ct);
            await Task.WhenAll(quotesTask, moneyTask);
            gpuQuotes = quotesTask.Result;
            money = moneyTask.Result;
        });

        var currency = _config.Electricity.Currency;
        var economics = Economics.Calculate(states, _config, network, price, gpuQuotes);

        var summary = new Grid().AddColumn().AddColumn();
        summary.AddRow("[grey]Mining nodes[/]", $"{states.Count(s => s.AnyMining)} of {states.Count}");
        summary.AddRow("[grey]Fleet CPU hashrate[/]", $"[aqua]{Economics.FormatHashrate(economics.TotalHashrate)}[/]");
        if (economics.TotalGpuHashrate > 0)
        {
            summary.AddRow("[grey]Fleet GPU rate[/]", $"[aqua]{UiHelpers.Escape(Economics.FormatGpuFleetRate(states))}[/]");
        }
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

        AnsiConsole.Write(BuildPeriodTable(economics, money));

        if (economics.CostPerXmr is { } costPerXmr)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Cost to mine 1 XMR:[/] {money.Markup(costPerXmr)}  " +
                $"[grey](break-even XMR price at current draw)[/]");
        }

        await ShowCardsAsync(states, currency, money, ct);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(BuildNodeBreakdownTable(economics, _config, money));

        RenderReconciliation(economics, wallet, price, currency, money);

        UiHelpers.Pause();
    }

    public static Table BuildPeriodTable(FleetEconomics economics, MoneyFormat money)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .Title("[bold]Per period[/]")
            .AddColumn("Period")
            .AddColumn(new TableColumn("CPU (XMR)").RightAligned());

        var showGpu = economics.TotalGpuHashrate > 0;
        if (showGpu)
        {
            table.AddColumn(new TableColumn("GPU").RightAligned());
            table.AddColumn(new TableColumn("Total income").RightAligned());
        }
        else
        {
            table.AddColumn(new TableColumn("Income").RightAligned());
        }

        table
            .AddColumn(new TableColumn("Electricity").RightAligned())
            .AddColumn(new TableColumn("Profit").RightAligned());

        foreach (var (label, factor) in new[] { ("Hour", 1 / 24.0), ("Day", 1.0), ("Week", 7.0), ("Month (30d)", 30.0) })
        {
            var xmrText = economics.XmrPerDay is { } xmr ? $"{xmr * factor:0.00000}" : "[grey]-[/]";
            var totalRev = economics.TotalRevenuePerDay is { } tr
                ? money.Markup(tr * factor)
                : economics.KnownRevenuePerDay is { } kr && economics.HasMissingRevenue
                    ? $"{money.Markup(kr * factor)} [yellow](partial)[/]"
                    : "[grey]-[/]";
            var profit = economics.TotalProfitPerDay is { } tp
                ? money.Signed(tp * factor)
                : "[grey]-[/]";

            if (showGpu)
            {
                var cpuMoney = economics.RevenuePerDay is { } cr ? $" [grey]({money.CompactMarkup(cr * factor)})[/]" : "";
                table.AddRow(
                    label,
                    xmrText + cpuMoney,
                    economics.GpuRevenuePerDay is { } gr ? money.CompactMarkup(gr * factor) : "[yellow]?[/]",
                    totalRev,
                    money.Markup(economics.CostPerDay * factor),
                    profit);
            }
            else
            {
                table.AddRow(
                    label,
                    xmrText,
                    totalRev,
                    money.Markup(economics.CostPerDay * factor),
                    profit);
            }
        }

        return table;
    }

    /// <summary>
    /// Per-machine daily breakdown combining CPU (XMR) and GPU (PRL/XTM) revenue against the
    /// whole machine's power draw, so a rig whose card carries most of its earnings shows its
    /// true total income and net profit instead of looking like an unprofitable CPU box.
    /// </summary>
    public static Table BuildNodeBreakdownTable(FleetEconomics economics, FleetConfig config, MoneyFormat money)
    {
        var active = economics.NodeBreakdown
            .Where(n => n.State.AnyMining)
            .OrderByDescending(n => n.TotalRevenuePerDay ?? n.KnownRevenuePerDay ?? 0)
            .ThenByDescending(n => n.State.Hashrate)
            .ToList();

        var showGpu = active.Any(n => n.State.GpuMining);
        var currency = economics.Currency;

        var breakdown = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .Title("[bold]Per node, per day[/]");

        if (showGpu)
        {
            breakdown
                .AddColumn("Node")
                .AddColumn(new TableColumn("CPU").RightAligned())
                .AddColumn(new TableColumn("GPU").RightAligned())
                .AddColumn(new TableColumn("CPU/day").RightAligned())
                .AddColumn(new TableColumn("GPU/day").RightAligned())
                .AddColumn(new TableColumn("Total/day").RightAligned())
                .AddColumn(new TableColumn("Watts").RightAligned())
                .AddColumn(new TableColumn("Cost").RightAligned())
                .AddColumn(new TableColumn("Profit").RightAligned());

            foreach (var node in active)
            {
                var state = node.State;
                var cpuCell = !state.Mining
                    ? "[grey]-[/]"
                    : node.CpuRevenuePerDay is { } c
                        ? money.CompactMarkup(c)
                        : "[yellow]?[/]";
                var gpuCell = !state.GpuMining
                    ? "[grey]-[/]"
                    : node.GpuRevenuePerDay is { } g
                        ? money.CompactMarkup(g)
                        : "[yellow]?[/]";
                var totalCell = node.TotalRevenuePerDay is { } total
                    ? $"[bold]{money.Markup(total)}[/]"
                    : node.KnownRevenuePerDay is { } known
                        ? $"{money.Markup(known)} [yellow]+ {(node.GpuRevenueMissing ? "GPU?" : "CPU?")}[/]"
                        : "[yellow]?[/]";

                breakdown.AddRow(
                    new Markup(UiHelpers.Escape(state.Node.Name)),
                    new Markup(state.Mining ? Economics.FormatHashrate(state.Hashrate) : "[grey]-[/]"),
                    new Markup(state.GpuMining ? UiHelpers.Escape(Economics.FormatGpuRate(state.GpuHashrate, state.Gpu?.HashrateUnit)) : "[grey]-[/]"),
                    new Markup(cpuCell),
                    new Markup(gpuCell),
                    new Markup(totalCell),
                    new Markup($"{state.PowerWatts:0}"),
                    new Markup(money.CompactMarkup(node.CostPerDay)),
                    new Markup(money.Signed(node.ProfitPerDay)));
            }

            if (active.Count > 1)
            {
                breakdown.AddEmptyRow();
                breakdown.AddRow(
                    new Markup("[bold]Fleet total[/]"),
                    new Markup($"[bold]{Economics.FormatHashrate(economics.TotalHashrate)}[/]"),
                    new Markup($"[bold]{UiHelpers.Escape(Economics.FormatGpuFleetRate(active.Select(a => a.State)))}[/]"),
                    new Markup(economics.RevenuePerDay is { } cr ? $"[bold]{money.CompactMarkup(cr)}[/]" : "[grey]-[/]"),
                    new Markup(economics.GpuRevenuePerDay is { } gr ? $"[bold]{money.CompactMarkup(gr)}[/]" : "[yellow]?[/]"),
                    new Markup(economics.TotalRevenuePerDay is { } tr
                        ? $"[bold]{money.Markup(tr)}[/]"
                        : economics.KnownRevenuePerDay is { } kr
                            ? $"[bold]{money.Markup(kr)}[/] [yellow](partial)[/]"
                            : "[yellow]?[/]"),
                    new Markup($"[bold]{economics.TotalWatts:0}[/]"),
                    new Markup($"[bold]{money.CompactMarkup(economics.CostPerDay)}[/]"),
                    new Markup(money.Signed(economics.TotalProfitPerDay)));
            }

            return breakdown;
        }

        breakdown
            .AddColumn("Node")
            .AddColumn(new TableColumn("Hashrate").RightAligned())
            .AddColumn(new TableColumn("Share").RightAligned())
            .AddColumn(new TableColumn("Watts").RightAligned())
            .AddColumn(new TableColumn($"{UiHelpers.Escape(currency)}/kWh").RightAligned())
            .AddColumn(new TableColumn("Cost").RightAligned())
            .AddColumn(new TableColumn("Income").RightAligned())
            .AddColumn(new TableColumn("Profit").RightAligned());

        foreach (var node in active)
        {
            var state = node.State;
            var share = economics.TotalHashrate > 0 ? state.Hashrate / economics.TotalHashrate : 0;

            breakdown.AddRow(
                new Markup(UiHelpers.Escape(state.Node.Name)),
                new Markup(Economics.FormatHashrate(state.Hashrate)),
                new Markup($"{share:P1}"),
                new Markup($"{state.PowerWatts:0}"),
                new Markup(state.Node.PricePerKwh is { } own
                    ? $"{own:N2}"
                    : $"[grey]{config.Electricity.PricePerKwh:N2}[/]"),
                new Markup(money.Markup(node.CostPerDay)),
                new Markup(money.Markup(node.TotalRevenuePerDay)),
                new Markup(money.Signed(node.ProfitPerDay)));
        }

        if (active.Count > 1)
        {
            breakdown.AddEmptyRow();
            breakdown.AddRow(
                new Markup("[bold]Fleet total[/]"),
                new Markup($"[bold]{Economics.FormatHashrate(economics.TotalHashrate)}[/]"),
                new Markup("100.0%"),
                new Markup($"[bold]{economics.TotalWatts:0}[/]"),
                new Markup(""),
                new Markup($"[bold]{money.Markup(economics.CostPerDay)}[/]"),
                new Markup($"[bold]{money.Markup(economics.TotalRevenuePerDay)}[/]"),
                new Markup(money.Signed(economics.TotalProfitPerDay)));
        }

        return breakdown;
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

        var found = new List<(string Nodes, GpuPoolStats Stats, double? Price, GpuIncome? Income)>();
        var xtmFound = new List<(string Nodes, XtmIncome Income, double? Price)>();
        var unreadable = new List<string>();

        await AnsiConsole.Status().StartAsync("Reading what the cards earned...", async _ =>
        {
            var kryptexNodes = new List<(string Node, GpuPoolTarget Target)>();
            var xtmNodes = new List<(string Node, XtmAccount Account)>();
            foreach (var state in mining)
            {
                using var client = _fleet.CreateClient(state.Node);
                var config = await SafeConfigAsync(client, ct);

                if (GpuPoolService.TargetFor(ToConfig(config)) is { } target)
                {
                    kryptexNodes.Add((state.Node.Name, target));
                    continue;
                }

                if (XtmAccount.From(config?.GpuMiner?.PoolUrl, config?.GpuMiner?.User) is { } xtm)
                {
                    xtmNodes.Add((state.Node.Name, xtm));
                    continue;
                }

                unreadable.Add(state.Node.Name);
            }

            foreach (var group in kryptexNodes.GroupBy(x => x.Target))
            {
                var names = string.Join(", ", group.Select(x => x.Node));
                var income = await _gpuIncome.GetAsync(group.Key, ct);
                var stats = income?.Stats;
                if (stats is null) { unreadable.Add(names); continue; }

                found.Add((names, stats, await _gpuPool.GetPriceAsync(group.Key, currency, ct), income));
            }

            if (xtmNodes.Count > 0)
            {
                var xtmPrice = await _gpuPool.GetPriceAsync(new GpuPoolTarget("luckypool", "xtm", ""), currency, ct);
                foreach (var group in xtmNodes.GroupBy(x => x.Account))
                {
                    var names = string.Join(", ", group.Select(x => x.Node));
                    var income = await _xtm.GetAsync(group.Key, ct);
                    if (income is null) { unreadable.Add(names); continue; }

                    xtmFound.Add((names, income, xtmPrice));
                }
            }
        });

        if (found.Count == 0 && xtmFound.Count == 0 && unreadable.Count == 0) return;

        AnsiConsole.WriteLine();
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .Title("[bold]Graphics cards, from pool payouts and wallet accruals[/]")
            .AddColumn("Nodes / wallet")
            .AddColumn("Coin")
            .AddColumn(new TableColumn("Per day").RightAligned())
            .AddColumn(new TableColumn("Value/day").RightAligned())
            .AddColumn(new TableColumn("Paid out").RightAligned())
            .AddColumn(new TableColumn("On the pool").RightAligned())
            .AddColumn("Measured over");

        var now = DateTimeOffset.UtcNow;
        double? valuePerDay = null;

        foreach (var (names, stats, price, income) in found)
        {
            // Payments first: they are money that has actually left the pool. A coin with a high
            // payout threshold (Pearl pays at one whole coin) has none for days, so the growth of
            // the pool balance stands in, and the cell says so.
            var paidPerDay = stats.PaidPerDay();
            var accruedPerDay = paidPerDay is null ? income?.ForecastPerDay : null;
            var perDay = paidPerDay ?? accruedPerDay;
            var span = stats.PayoutSpan();
            double? value = perDay is { } p && price is { } q ? p * q : null;
            if (value is { } v) valuePerDay = (valuePerDay ?? 0) + v;

            table.AddRow(
                new Markup(UiHelpers.Escape(names)),
                new Markup(UiHelpers.Escape(stats.Target.Coin.ToUpperInvariant())),
                new Markup(perDay is { } d ? $"[aqua]{(accruedPerDay is null ? d.ToString("N0") : GpuIncomePanel.Amount(d, "").Trim())}[/]" : "[grey]-[/]"),
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
                    : income?.ForecastBasis is { } basis
                        ? $"[grey]pool balance growth over {basis.Hours:0.#} h, no payouts yet[/]"
                        : "[yellow]too few payouts to say[/]"));
        }

        foreach (var (names, income, price) in xtmFound)
        {
            var basis = income.ForecastBasis(now);
            var perDay = basis is not null ? basis.Amount * 24 / basis.Hours : income.Day?.Amount;
            double? value = perDay is { } p && price is { } q ? p * q : null;
            if (value is { } v) valuePerDay = (valuePerDay ?? 0) + v;

            table.AddRow(
                new Markup(UiHelpers.Escape(names)),
                new Markup("XTM"),
                new Markup(perDay is { } d ? $"[aqua]{d:N2}[/]" : "[grey]-[/]"),
                new Markup(value is null ? $"[grey]price unavailable in {UiHelpers.Escape(currency)}[/]" : money.Markup(value)),
                new Markup("[grey]-[/]"),
                new Markup(income.Day is { } day ? $"{day.Amount:N2} [grey](24h)[/]" : "[grey]-[/]"),
                new Markup(basis is not null
                    ? $"[grey]{basis.Hours} h pool reward mean[/]"
                    : income.Day is not null
                        ? "[grey]last 24 h pool rewards[/]"
                        : "[yellow]need 24h history[/]"));
        }

        AnsiConsole.Write(table);

        if (valuePerDay is { } total)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Rolling GPU wallet accruals average[/] {money.Markup(total)} [grey]/ day across unique pool wallets " +
                "(including pauses), alongside the live speed-based GPU rate folded into the tables above.[/]");
        }

        foreach (var name in unreadable)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{UiHelpers.Escape(name)}[/] [grey]is mining on a card whose pool this console " +
                "cannot read. Only Kryptex and LuckyPool C29 are understood; the electricity still counts against it.[/]");
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
