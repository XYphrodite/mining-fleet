using Spectre.Console;
using Spectre.Console.Rendering;

namespace MiningFleet.Console.Ui;

/// <summary>
/// The live fleet view: one row per node plus a totals panel. Refreshes on the configured
/// interval until a key is pressed. Market data is fetched far less often than node status
/// because the pool and price APIs are rate-limited.
/// </summary>
public sealed class Dashboard : IDisposable
{
    private static readonly TimeSpan MarketRefresh = TimeSpan.FromMinutes(2);

    private readonly FleetConfig _config;
    private readonly FleetService _fleet;
    private readonly MarketService _market;
    private readonly XtmIncomeService _xtm = new();
    private readonly GpuIncomeService _gpuIncome;

    private PoolNetworkStats? _network;
    private double? _price;
    private readonly Dictionary<string, XtmAccount> _xtmAccounts = new();
    private IReadOnlyList<XtmIncomeRow> _xtmIncome = [];
    private DateTimeOffset _xtmFetchedAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, GpuPoolTarget> _gpuTargets = new();
    private IReadOnlyList<GpuIncomeRow> _gpuIncomeRows = [];
    private MoneyFormat? _money;
    private DateTimeOffset _marketFetchedAt = DateTimeOffset.MinValue;

    public Dashboard(FleetConfig config, FleetService fleet, MarketService market)
    {
        _config = config;
        _fleet = fleet;
        _market = market;
        _gpuIncome = new GpuIncomeService(new GpuPoolService(), GpuIncomeLedger.For(config));
    }

    public async Task ShowAsync(CancellationToken ct)
    {
        UiHelpers.Header("Fleet dashboard");
        if (_config.Nodes.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No nodes configured. Add one from the Nodes menu.[/]");
            UiHelpers.Pause();
            return;
        }

        DrainKeys();
        _xtmFetchedAt = DateTimeOffset.MinValue;
        var interval = TimeSpan.FromSeconds(Math.Max(1, _config.PollIntervalSeconds));

        await AnsiConsole.Live(new Rows())
            .AutoClear(true)
            .StartAsync(async ctx =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await RefreshMarketAsync(ct);
                    var states = await _fleet.PollAsync(ct);
                    await RefreshXtmAsync(states, _xtm, ct);
                    var gpuQuotes = await _gpuIncome.ResolveQuotesAsync(states, _config, _fleet, ct, _gpuTargets);
                    var economics = Economics.Calculate(states, _config, _network, _price, gpuQuotes);

                    ctx.UpdateTarget(Compose(states, economics));
                    ctx.Refresh();

                    if (await WaitOrKeyAsync(interval, ct)) return;
                }
            });
    }

    private async Task RefreshMarketAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _marketFetchedAt < MarketRefresh) return;
        _marketFetchedAt = DateTimeOffset.UtcNow;

        var networkTask = _market.GetNetworkStatsAsync(ct);
        var priceTask = _market.GetPriceAsync(ct);
        await Task.WhenAll(networkTask, priceTask);
        _network = networkTask.Result ?? _network;
        _price = priceTask.Result ?? _price;
        _money = await _market.GetMoneyFormatAsync(ct);
    }

    private async Task RefreshXtmAsync(IReadOnlyList<NodeState> states, XtmIncomeService service,
        CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _xtmFetchedAt < MarketRefresh) return;
        _xtmFetchedAt = DateTimeOffset.UtcNow;
        var accounts = new List<(string Node, XtmAccount Account, bool Unverified)>();
        var gpuAccounts = new List<(string Node, GpuPoolTarget Target, bool Unverified)>();
        var rows = new List<XtmIncomeRow>();
        foreach (var state in states)
        {
            // Read paused cards too: stopping a miner does not erase the wallet's earnings.
            var key = state.Node.Endpoint;
            var unverified = true;
            if (state.Online)
            {
                using var client = _fleet.CreateClient(state.Node);
                try
                {
                    var settings = await client.GetConfigAsync(ct);
                    if (settings is not null)
                    {
                        unverified = false;
                        var gpu = settings.GpuMiner;
                        var target = XtmAccount.From(gpu?.PoolUrl, gpu?.User);
                        if (target is not null) _xtmAccounts[key] = target;
                        else _xtmAccounts.Remove(key);

                        // Pearl and any other coin read from Kryptex's balance. The same settings
                        // answer both questions, so the node is asked once.
                        var pool = GpuPoolService.TargetFor(new GpuMinerConfig
                        {
                            Enabled = gpu?.Enabled, PoolUrl = gpu?.PoolUrl, User = gpu?.User,
                        });
                        if (pool is not null) _gpuTargets[key] = pool;
                        else _gpuTargets.Remove(key);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                    || ex is OperationCanceledException && !ct.IsCancellationRequested) { }
            }
            if (_xtmAccounts.TryGetValue(key, out var account))
                accounts.Add((state.Node.Name, account, unverified));
            if (_gpuTargets.TryGetValue(key, out var gpuTarget))
                gpuAccounts.Add((state.Node.Name, gpuTarget, unverified));
        }
        // Group BEFORE requesting or rendering: two cards on one wallet are one income stream.
        var currency = _config.Electricity.Currency;
        var xtmPrice = accounts.Count > 0
            ? await _gpuIncome.GetPriceAsync(new GpuPoolTarget("luckypool", "xtm", ""), currency, ct)
            : null;
        foreach (var group in accounts.GroupBy(a => a.Account))
            rows.Add(new(string.Join(", ", group.Select(a => a.Node)), group.Key,
                await service.GetAsync(group.Key, ct), group.Any(a => a.Unverified), xtmPrice));
        _xtmIncome = rows;

        var gpuRows = new List<GpuIncomeRow>();
        foreach (var group in gpuAccounts.GroupBy(a => a.Target))
        {
            gpuRows.Add(new(string.Join(", ", group.Select(a => a.Node)), group.Key,
                await _gpuIncome.GetAsync(group.Key, ct),
                await _gpuIncome.GetPriceAsync(group.Key, currency, ct),
                group.Any(a => a.Unverified)));
        }
        _gpuIncomeRows = gpuRows;
    }

    private IRenderable Compose(IReadOnlyList<NodeState> states, FleetEconomics economics) =>
        RenderView(
            states,
            economics,
            _money ?? MoneyFormat.Single(economics.Currency),
            _network,
            _price,
            _xtmIncome,
            _gpuIncomeRows,
            DateTimeOffset.UtcNow);

    public static IRenderable RenderView(
        IReadOnlyList<NodeState> states,
        FleetEconomics economics,
        MoneyFormat money,
        PoolNetworkStats? network,
        double? price,
        IReadOnlyList<XtmIncomeRow> xtmIncome,
        IReadOnlyList<GpuIncomeRow> gpuIncomeRows,
        DateTimeOffset now)
    {
        // Twelve columns already wrap an 80-column terminal, so the GPU pair only appears when
        // some node in the fleet actually has a card mining. A fleet with none loses nothing.
        var showGpu = states.Any(s => s.Gpu is not null);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35)
            .Title($"[bold]Nodes[/] [grey]{now.ToLocalTime():HH:mm:ss}[/]")
            .AddColumn("Node")
            .AddColumn("State")
            .AddColumn(new TableColumn("Hashrate").RightAligned())
            .AddColumn(new TableColumn("Thr").RightAligned())
            .AddColumn(new TableColumn("Pwr").RightAligned())
            .AddColumn(new TableColumn("Pages").RightAligned())
            .AddColumn(new TableColumn("MSR").RightAligned())
            .AddColumn(new TableColumn("CPU").RightAligned())
            .AddColumn(new TableColumn("Temp").RightAligned())
            .AddColumn(new TableColumn("Watts").RightAligned())
            .AddColumn(new TableColumn("Shares").RightAligned());

        if (showGpu)
        {
            table
                .AddColumn(new TableColumn("GPU").RightAligned())
                .AddColumn(new TableColumn("GPU sh").RightAligned());
        }

        table
            .AddColumn(new TableColumn("Income/d").RightAligned().NoWrap())
            .AddColumn(new TableColumn("Uptime").RightAligned());

        foreach (var state in states.OrderBy(s => s.Node.Name, StringComparer.OrdinalIgnoreCase))
        {
            var miner = state.Snapshot?.Miner;
            var hardware = state.Snapshot?.Hardware;
            var nodeEconomics = economics.ForNode(state.Node.Name);

            var cells = new List<IRenderable>
            {
                new Markup($"[bold]{UiHelpers.Escape(state.Node.Name)}[/]\n[grey]{UiHelpers.Escape(state.Node.Host)}[/]"),
                new Markup(UiHelpers.StatusBadge(state)),
                new Markup(state.Hashrate > 0 ? $"[aqua]{Economics.FormatHashrate(state.Hashrate)}[/]" : "[grey]-[/]"),
                new Markup(miner?.MiningThreads is { } thr ? $"{thr}" : "[grey]-[/]"),
                new Markup(UiHelpers.ThrottleBadge(state.Throttle)),
                new Markup(UiHelpers.HugePages(miner)),
                new Markup(UiHelpers.MsrBadge(miner)),
                new Markup(hardware?.CpuLoadPercent is { } load ? $"{load:0}%" : "[grey]-[/]"),
                new Markup(UiHelpers.TemperaturePair(hardware)),
                new Markup(state.PowerWatts > 0
                    ? $"{state.PowerWatts:0}{(hardware?.PowerIsMeasured == true ? "" : "[grey]~[/]")}"
                    : "[grey]-[/]"),
                new Markup(miner is { SharesTotal: > 0 }
                    ? $"{miner.SharesGood}/{miner.SharesTotal}"
                    : "[grey]-[/]"),
            };

            if (showGpu)
            {
                cells.Add(new Markup(UiHelpers.GpuBadge(state.Gpu)));
                cells.Add(new Markup(UiHelpers.GpuShares(state.Gpu)));
            }

            cells.Add(new Markup(FormatNodeIncomeMarkup(nodeEconomics, money)));
            cells.Add(new Markup(miner is { Running: true }
                ? Economics.FormatDuration(miner.UptimeSeconds)
                : "[grey]-[/]"));
            table.AddRow(cells);
        }

        var online = states.Count(s => s.Online);
        var mining = states.Count(s => s.AnyMining);

        var summary = new Grid().AddColumn().AddColumn().AddColumn();
        summary.AddRow(
            Cell("Fleet", $"[green]{mining}[/] mining / {online} online / {states.Count} total"),
            Cell("Hashrate", $"[aqua]{Economics.FormatHashrate(economics.TotalHashrate)}[/]"),
            Cell("Draw", $"{economics.TotalWatts:0} W"));

        string totalsNote;
        if (economics.TotalGpuHashrate > 0)
        {
            var accepted = states.Where(x => x.GpuMining).Sum(x => x.Gpu?.AcceptedShares ?? 0);
            var totalShares = states.Where(x => x.GpuMining)
                .Sum(x => (x.Gpu?.AcceptedShares ?? 0) + (x.Gpu?.RejectedShares ?? 0) + (x.Gpu?.StaleShares ?? 0));

            summary.AddRow(
                Cell("CPU income/day", economics.XmrPerDay is { } xmr
                    ? $"{xmr:0.00000} XMR  {money.Markup(economics.RevenuePerDay)}"
                    : economics.TotalHashrate <= 0
                        ? money.Markup(0)
                        : "[grey]needs pool data[/]"),
                Cell("GPU income/day", economics.GpuRevenuePerDay is { } gpuRev
                    ? money.Markup(gpuRev)
                    : "[yellow]needs GPU pool/price[/]"),
                Cell("Power cost/day", money.Markup(economics.CostPerDay)));

            summary.AddRow(
                Cell("GPU", $"[aqua]{UiHelpers.Escape(Economics.FormatGpuFleetRate(states))}[/]  [grey]({accepted}/{totalShares} sh)[/]"),
                Cell("Total income/day", FormatFleetTotalIncomeMarkup(economics, money)),
                Cell("Net profit/day", economics.TotalProfitPerDay is { } netProfit
                    ? money.Signed(netProfit)
                    : "[grey]needs all pool prices[/]"));

            totalsNote = "[grey]Totals combine live CPU + GPU speed at current pool yield minus total power draw. Rolling wallet accruals are shown below.[/]";
        }
        else
        {
            summary.AddRow(
                Cell("Power cost/day", money.Markup(economics.CostPerDay)),
                Cell("XMR income/day", economics.XmrPerDay is { } xmr
                    ? $"{xmr:0.00000} XMR  {money.Markup(economics.RevenuePerDay)}"
                    : "[grey]needs pool data[/]"),
                Cell("Net profit/day", money.Signed(economics.TotalProfitPerDay ?? economics.ProfitPerDay)));

            totalsNote = "[grey]Net profit = live XMR income minus the displayed power cost.[/]";
        }

        var footer = new Markup(
            $"[grey]price[/] {money.Markup(price)}   " +
            $"[grey]net[/] {(network?.NetworkHashrate is { } nh ? Economics.FormatHashrate(nh) : "-")}   " +
            "[grey]press any key to return[/]");

        return new Rows(
            table,
            new Panel(new Rows(summary, new Markup(totalsNote)))
                .Header("[bold]Totals[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey35).Expand(),
            XtmIncomePanel.Render(xtmIncome, now, money),
            GpuIncomePanel.Render(gpuIncomeRows, money, now),
            footer);
    }

    /// <summary>
    /// Formats one machine's combined daily income cell. Dual-mining machines show their total on
    /// the first line and the CPU + GPU split right below in the second line that the row already occupies.
    /// </summary>
    public static string FormatNodeIncomeMarkup(NodeEconomics? node, MoneyFormat money)
    {
        if (node is null || !node.State.AnyMining) return "[grey]-[/]";

        if (node.TotalRevenuePerDay is { } total)
        {
            if (node.State.Mining && node.State.GpuMining
                && node.CpuRevenuePerDay is { } cpu && node.GpuRevenuePerDay is { } gpu)
            {
                return $"{money.CompactMarkup(total)}\n[grey]{cpu:N2} + {gpu:N2}[/]";
            }

            if (!node.State.Mining && node.State.GpuMining)
            {
                return $"{money.CompactMarkup(total)}\n[grey]GPU[/]";
            }

            return money.CompactMarkup(total);
        }

        if (node.KnownRevenuePerDay is { } known)
        {
            var missing = node.GpuRevenueMissing ? "+ GPU ?" : "+ CPU ?";
            return $"{money.CompactMarkup(known)}\n[yellow]{missing}[/]";
        }

        return "[grey]?[/]";
    }

    public static string FormatFleetTotalIncomeMarkup(FleetEconomics economics, MoneyFormat money)
    {
        if (economics.TotalRevenuePerDay is { } total) return money.Markup(total);
        if (economics.KnownRevenuePerDay is { } known && economics.HasMissingRevenue)
            return $"{money.Markup(known)} [yellow](partial)[/]";
        return "[grey]needs pool data[/]";
    }

    private static IRenderable Cell(string label, string value) => new Markup($"[grey]{UiHelpers.Escape(label)}[/]  {value}");

    /// <summary>Returns true when a key was pressed, which ends the live view.</summary>
    private static async Task<bool> WaitOrKeyAsync(TimeSpan interval, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + interval;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return true;
            if (System.Console.KeyAvailable)
            {
                System.Console.ReadKey(intercept: true);
                return true;
            }
            await Task.Delay(80, ct);
        }
        return false;
    }

    private static void DrainKeys()
    {
        while (System.Console.KeyAvailable) System.Console.ReadKey(intercept: true);
    }

    public void Dispose()
    {
        _xtm.Dispose();
        _gpuIncome.Dispose();
    }
}
