using Spectre.Console;
using Spectre.Console.Rendering;

namespace MiningFleet.Console.Ui;

/// <summary>
/// The live fleet view: one row per node plus a totals panel. Refreshes on the configured
/// interval until a key is pressed. Market data is fetched far less often than node status
/// because the pool and price APIs are rate-limited.
/// </summary>
public sealed class Dashboard
{
    private static readonly TimeSpan MarketRefresh = TimeSpan.FromMinutes(2);

    private readonly FleetConfig _config;
    private readonly FleetService _fleet;
    private readonly MarketService _market;

    private PoolNetworkStats? _network;
    private double? _price;
    private MoneyFormat? _money;
    private DateTimeOffset _marketFetchedAt = DateTimeOffset.MinValue;

    public Dashboard(FleetConfig config, FleetService fleet, MarketService market)
    {
        _config = config;
        _fleet = fleet;
        _market = market;
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
        var interval = TimeSpan.FromSeconds(Math.Max(1, _config.PollIntervalSeconds));

        await AnsiConsole.Live(new Rows())
            .AutoClear(true)
            .StartAsync(async ctx =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await RefreshMarketAsync(ct);
                    var states = await _fleet.PollAsync(ct);
                    var economics = Economics.Calculate(states, _config, _network, _price);

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

    private IRenderable Compose(IReadOnlyList<NodeState> states, FleetEconomics economics)
    {
        var money = _money ?? MoneyFormat.Single(economics.Currency);

        // Twelve columns already wrap an 80-column terminal, so the GPU pair only appears when
        // some node in the fleet actually has a card mining. A fleet with none loses nothing.
        var showGpu = states.Any(s => s.Gpu is not null);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey35)
            .Title($"[bold]Nodes[/] [grey]{DateTime.Now:HH:mm:ss}[/]")
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

        table.AddColumn(new TableColumn("Uptime").RightAligned());

        foreach (var state in states.OrderBy(s => s.Node.Name, StringComparer.OrdinalIgnoreCase))
        {
            var miner = state.Snapshot?.Miner;
            var hardware = state.Snapshot?.Hardware;

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
                new Markup(UiHelpers.Temperature(hardware?.CpuTemperatureC ?? hardware?.Gpus.FirstOrDefault()?.TemperatureC)),
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

            cells.Add(new Markup(miner is { Running: true } ? Economics.FormatDuration(miner.UptimeSeconds) : "[grey]-[/]"));
            table.AddRow(cells);
        }

        var online = states.Count(s => s.Online);
        var mining = states.Count(s => s.Mining);

        var summary = new Grid().AddColumn().AddColumn().AddColumn();
        summary.AddRow(
            Cell("Fleet", $"[green]{mining}[/] mining / {online} online / {states.Count} total"),
            Cell("Hashrate", $"[aqua]{Economics.FormatHashrate(economics.TotalHashrate)}[/]"),
            Cell("Draw", $"{economics.TotalWatts:0} W"));
        summary.AddRow(
            Cell("Power cost/day", money.Markup(economics.CostPerDay)),
            Cell("Income/day", economics.XmrPerDay is { } xmr
                ? $"{xmr:0.00000} XMR  {money.Markup(economics.RevenuePerDay)}"
                : "[grey]needs pool data[/]"),
            Cell("Profit/day", money.Signed(economics.ProfitPerDay)));

        var footer = new Markup(
            $"[grey]price[/] {money.Markup(_price)}   " +
            $"[grey]net[/] {(_network?.NetworkHashrate is { } nh ? Economics.FormatHashrate(nh) : "-")}   " +
            "[grey]press any key to return[/]");

        return new Rows(
            table,
            new Panel(summary).Header("[bold]Totals[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey35).Expand(),
            footer);
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
}
