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
    private static readonly TimeSpan NodePollTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan EarlyPoolTriggerDelay = TimeSpan.FromMilliseconds(600);

    private readonly FleetConfig _config;
    private readonly FleetService _fleet;
    private readonly MarketService _market;
    private readonly XtmIncomeService _xtm = new();
    private readonly GpuIncomeService _gpuIncome;
    private readonly LifetimeIncomeLedger _lifetime;

    private PoolNetworkStats? _network;
    private double? _price;
    private readonly Dictionary<string, NodeState> _lastStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XtmAccount> _xtmAccounts = new();
    private IReadOnlyList<XtmIncomeRow> _xtmIncome = [];
    private DateTimeOffset _xtmFetchedAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, GpuPoolTarget> _gpuTargets = new();
    private IReadOnlyList<GpuIncomeRow> _gpuIncomeRows = [];
    private IReadOnlyDictionary<string, NodeGpuQuote> _gpuQuotes = new Dictionary<string, NodeGpuQuote>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, NodeGpuQuote> _coinQuotes = new Dictionary<string, NodeGpuQuote>(StringComparer.OrdinalIgnoreCase);
    private readonly SparklineHistory _sparklines = new();
    private MoneyFormat? _money;
    private DateTimeOffset _marketFetchedAt = DateTimeOffset.MinValue;

    private sealed record MarketSnapshot(PoolNetworkStats? Network, double? Price, MoneyFormat Money);

    private sealed record PoolPanelsSnapshot(
        Dictionary<string, XtmAccount> XtmAccounts,
        Dictionary<string, GpuPoolTarget> GpuTargets,
        IReadOnlyList<XtmIncomeRow> XtmIncome,
        IReadOnlyList<GpuIncomeRow> GpuIncomeRows,
        IReadOnlyDictionary<string, NodeGpuQuote> GpuQuotes,
        IReadOnlyDictionary<string, NodeGpuQuote> CoinQuotes);

    public Dashboard(FleetConfig config, FleetService fleet, MarketService market)
    {
        _config = config;
        _fleet = fleet;
        _market = market;
        _gpuIncome = new GpuIncomeService(new GpuPoolService(), GpuIncomeLedger.For(config));
        _lifetime = LifetimeIncomeLedger.For(config);
    }

    public async Task ShowAsync(CancellationToken ct)
    {
        UiHelpers.Header("Fleet dashboard");
        var enabled = _fleet.EnabledNodes;
        if (enabled.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No enabled nodes configured. Add or enable one from the Nodes menu.[/]");
            UiHelpers.Pause();
            return;
        }

        DrainKeys();
        _xtmFetchedAt = DateTimeOffset.MinValue;
        var interval = TimeSpan.FromSeconds(Math.Max(1, _config.PollIntervalSeconds));

        var states = BuildInitialStates(enabled);
        using var liveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var liveCt = liveCts.Token;

        IRenderable BuildFrame()
        {
            var economics = Economics.Calculate(states, _config, _network, _price, _gpuQuotes);
            var lifetime = _lifetime.UpdateAndCalculate(states, _config, _network, _price, _coinQuotes, _gpuQuotes);
            return Compose(states, economics, lifetime);
        }

        await AnsiConsole.Live(BuildFrame())
            .AutoClear(true)
            .StartAsync(async ctx =>
            {
                Task<MarketSnapshot?>? marketTask = null;
                Task<IReadOnlyDictionary<string, NodeGpuQuote>?>? fastQuotesTask = null;
                Task<PoolPanelsSnapshot?>? poolTask = null;
                Task? fleetSyncTask = null;

                // Kick off coin yields & prices immediately from known config/ledger slugs so GPU
                // daily and lifetime RUB figures do not wait for offline nodes or /config round-trips.
                var initialSlugs = KnownCoinSlugsFor(states, enabled);
                if (initialSlugs.Count > 0)
                    fastQuotesTask = FetchCoinQuotesSafeAsync(initialSlugs, liveCt);

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        enabled = _fleet.EnabledNodes;
                        if (enabled.Count == 0) return;
                        states = AlignStates(states, enabled);

                        var now = DateTimeOffset.UtcNow;
                        if (marketTask is null && now - _marketFetchedAt >= MarketRefresh)
                        {
                            _marketFetchedAt = now;
                            marketTask = FetchMarketSafeAsync(liveCt);
                        }

                        var needPoolRefresh = now - _xtmFetchedAt >= MarketRefresh;
                        var waveStartedAt = DateTime.UtcNow;
                        var nodeTasks = new List<Task<(int Index, NodeState State)>>(enabled.Count);
                        for (var i = 0; i < enabled.Count; i++)
                            nodeTasks.Add(PollIndexedAsync(i, enabled[i], liveCt));

                        while (nodeTasks.Count > 0)
                        {
                            if (needPoolRefresh && poolTask is null
                                && DateTime.UtcNow - waveStartedAt >= EarlyPoolTriggerDelay
                                && states.Any(s => s.Online))
                            {
                                needPoolRefresh = false;
                                _xtmFetchedAt = DateTimeOffset.UtcNow;
                                poolTask = FetchPoolPanelsSafeAsync(states.ToArray(), liveCt);
                            }

                            var earlyWakeUtc = needPoolRefresh && poolTask is null && states.Any(s => s.Online)
                                ? waveStartedAt + EarlyPoolTriggerDelay
                                : DateTime.MaxValue;

                            var completed = await WaitNextCompletionOrKeyAsync(
                                nodeTasks, marketTask, fastQuotesTask, poolTask,
                                earlyWakeUtc,
                                liveCt);

                            if (completed is null)
                            {
                                liveCts.Cancel();
                                return;
                            }

                            var dirty = false;
                            if (completed == marketTask)
                            {
                                ApplyMarket(await marketTask);
                                marketTask = null;
                                dirty = true;
                            }
                            else if (completed == fastQuotesTask)
                            {
                                ApplyFastQuotes(await fastQuotesTask, states);
                                fastQuotesTask = null;
                                dirty = true;
                            }
                            else if (completed == poolTask)
                            {
                                ApplyPoolPanels(await poolTask, states);
                                poolTask = null;
                                dirty = true;
                            }
                            else if (completed is Task<(int Index, NodeState State)> doneNode)
                            {
                                nodeTasks.Remove(doneNode);
                                var (idx, nodeState) = await doneNode;
                                if (idx >= 0 && idx < states.Length)
                                {
                                    states[idx] = nodeState;
                                    _lastStates[nodeState.Node.Endpoint] = nodeState;
                                    _sparklines.RecordOne(nodeState);
                                    RefreshNodeQuotesFromKnownCoins(states);
                                    dirty = true;
                                }
                            }

                            if (dirty)
                            {
                                ctx.UpdateTarget(BuildFrame());
                                ctx.Refresh();
                            }
                        }

                        if (needPoolRefresh && poolTask is null)
                        {
                            needPoolRefresh = false;
                            _xtmFetchedAt = DateTimeOffset.UtcNow;
                            poolTask = FetchPoolPanelsSafeAsync(states.ToArray(), liveCt);
                        }

                        fleetSyncTask ??= SyncFleetFromSnapshotsSafeAsync(states.ToArray(), liveCt);

                        var nextWaveAt = DateTime.UtcNow + interval;
                        while (DateTime.UtcNow < nextWaveAt)
                        {
                            if (fleetSyncTask is { IsCompleted: true })
                            {
                                await fleetSyncTask;
                                fleetSyncTask = null;
                            }
                            var completed = await WaitNextCompletionOrKeyAsync(
                                [], marketTask, fastQuotesTask, poolTask,
                                nextWaveAt,
                                liveCt);

                            if (completed is null)
                            {
                                liveCts.Cancel();
                                return;
                            }

                            var dirty = false;
                            if (completed == marketTask)
                            {
                                ApplyMarket(await marketTask);
                                marketTask = null;
                                dirty = true;
                            }
                            else if (completed == fastQuotesTask)
                            {
                                ApplyFastQuotes(await fastQuotesTask, states);
                                fastQuotesTask = null;
                                dirty = true;
                            }
                            else if (completed == poolTask)
                            {
                                ApplyPoolPanels(await poolTask, states);
                                poolTask = null;
                                dirty = true;
                            }
                            else
                            {
                                // Timer elapsed cleanly: start next poll wave.
                                break;
                            }

                            if (dirty)
                            {
                                ctx.UpdateTarget(BuildFrame());
                                ctx.Refresh();
                            }
                        }
                    }
                }
                finally
                {
                    liveCts.Cancel();
                }
            });
    }

    public NodeState[] BuildInitialStates(IReadOnlyList<NodeConfig> enabled) =>
        enabled
            .Select(n => _lastStates.TryGetValue(n.Endpoint, out var prev)
                ? prev
                : new NodeState(n, null, null, DateTimeOffset.Now, Pending: true))
            .ToArray();

    private static NodeState[] AlignStates(NodeState[] current, IReadOnlyList<NodeConfig> enabled)
    {
        if (current.Length == enabled.Count
            && current.Zip(enabled).All(pair => pair.First.Node.Endpoint == pair.Second.Endpoint))
        {
            return current;
        }

        var byEndpoint = current.ToDictionary(s => s.Node.Endpoint, StringComparer.OrdinalIgnoreCase);
        return enabled
            .Select(n => byEndpoint.TryGetValue(n.Endpoint, out var prev)
                ? prev with { Node = n }
                : new NodeState(n, null, null, DateTimeOffset.Now, Pending: true))
            .ToArray();
    }

    private IReadOnlyList<string> KnownCoinSlugsFor(IReadOnlyList<NodeState> states, IReadOnlyList<NodeConfig> enabled)
    {
        var slugs = new HashSet<string>(_lifetime.KnownGpuCoinSlugs(states, _config), StringComparer.OrdinalIgnoreCase);
        foreach (var node in enabled)
        {
            if (GpuPoolService.CoinForPool(_config.GpuMinerFor(node).PoolUrl) is { } slug)
                slugs.Add(slug);
        }
        foreach (var state in states)
        {
            if (GpuPoolService.CoinForPool(state.Gpu?.Pool) is { } slug)
                slugs.Add(slug);
        }
        return slugs.ToArray();
    }

    private void RefreshNodeQuotesFromKnownCoins(IReadOnlyList<NodeState> states)
    {
        if (_coinQuotes.Count == 0) return;

        var merged = new Dictionary<string, NodeGpuQuote>(_gpuQuotes, StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            if (!state.GpuMining) continue;
            var coin = GpuPoolService.CoinForPool(state.Gpu?.Pool)
                       ?? (_gpuTargets.TryGetValue(state.Node.Endpoint, out var target) ? target.Coin : null)
                       ?? GpuPoolService.CoinForPool(_config.GpuMinerFor(state.Node).PoolUrl);
            if (coin is not null && _coinQuotes.TryGetValue(coin, out var quote))
                merged[state.Node.Name] = quote;
        }
        _gpuQuotes = merged;
    }

    private async Task<(int Index, NodeState State)> PollIndexedAsync(int index, NodeConfig node, CancellationToken ct) =>
        (index, await _fleet.PollNodeAsync(node, ct, NodePollTimeout));

    private async Task SyncFleetFromSnapshotsSafeAsync(IReadOnlyList<NodeState> states, CancellationToken ct)
    {
        try
        {
            await _fleet.ObserveSnapshotsForSyncAsync(states, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private async Task<MarketSnapshot?> FetchMarketSafeAsync(CancellationToken ct)
    {
        try
        {
            var networkTask = _market.GetNetworkStatsAsync(ct);
            var priceTask = _market.GetPriceAsync(ct);
            var moneyTask = _market.GetMoneyFormatAsync(ct);
            await Task.WhenAll(networkTask, priceTask, moneyTask);
            return new MarketSnapshot(networkTask.Result, priceTask.Result, moneyTask.Result);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, NodeGpuQuote>?> FetchCoinQuotesSafeAsync(
        IReadOnlyList<string> slugs,
        CancellationToken ct)
    {
        try
        {
            return await _gpuIncome.ResolveCoinQuotesAsync(slugs, _config.Electricity.Currency, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<PoolPanelsSnapshot?> FetchPoolPanelsSafeAsync(
        IReadOnlyList<NodeState> states,
        CancellationToken ct)
    {
        try
        {
            var xtmAccounts = new Dictionary<string, XtmAccount>(_xtmAccounts);
            var gpuTargets = new Dictionary<string, GpuPoolTarget>(_gpuTargets);

            // Read paused cards too: stopping a miner does not erase the wallet's earnings.
            var onlineStates = states.Where(s => s.Online).ToArray();
            var configResults = await Task.WhenAll(onlineStates.Select(async state =>
            {
                using var client = _fleet.CreateClient(state.Node, NodePollTimeout);
                try
                {
                    return (Endpoint: state.Node.Endpoint, Settings: await client.GetConfigAsync(ct));
                }
                catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                    || ex is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    return (Endpoint: state.Node.Endpoint, Settings: null);
                }
            }));

            var verifiedEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (endpoint, settings) in configResults)
            {
                if (settings is null) continue;
                verifiedEndpoints.Add(endpoint);

                var gpu = settings.GpuMiner;
                var target = XtmAccount.From(gpu?.PoolUrl, gpu?.User);
                if (target is not null) xtmAccounts[endpoint] = target;
                else xtmAccounts.Remove(endpoint);

                var pool = GpuPoolService.TargetFor(new GpuMinerConfig
                {
                    Enabled = gpu?.Enabled, PoolUrl = gpu?.PoolUrl, User = gpu?.User,
                });
                if (pool is not null) gpuTargets[endpoint] = pool;
                else gpuTargets.Remove(endpoint);
            }

            var accounts = new List<(string Node, XtmAccount Account, bool Unverified)>();
            var gpuAccounts = new List<(string Node, GpuPoolTarget Target, bool Unverified)>();
            foreach (var state in states)
            {
                var key = state.Node.Endpoint;
                var unverified = !verifiedEndpoints.Contains(key);
                if (xtmAccounts.TryGetValue(key, out var account))
                    accounts.Add((state.Node.Name, account, unverified));
                if (gpuTargets.TryGetValue(key, out var gpuTarget))
                    gpuAccounts.Add((state.Node.Name, gpuTarget, unverified));
            }

            // Group BEFORE requesting or rendering: two cards on one wallet are one income stream.
            var currency = _config.Electricity.Currency;
            var xtmPriceTask = accounts.Count > 0
                ? _gpuIncome.GetPriceAsync(new GpuPoolTarget("luckypool", "xtm", ""), currency, ct)
                : Task.FromResult<double?>(null);

            var xtmGroupTasks = accounts
                .GroupBy(a => a.Account)
                .Select(async group => (
                    Nodes: string.Join(", ", group.Select(a => a.Node)),
                    Account: group.Key,
                    Income: await _xtm.GetAsync(group.Key, ct),
                    Unverified: group.Any(a => a.Unverified)))
                .ToArray();

            var gpuGroupTasks = gpuAccounts
                .GroupBy(a => a.Target)
                .Select(async group =>
                {
                    var incomeTask = _gpuIncome.GetAsync(group.Key, ct);
                    var priceTask = _gpuIncome.GetPriceAsync(group.Key, currency, ct);
                    await Task.WhenAll(incomeTask, priceTask);
                    return new GpuIncomeRow(
                        string.Join(", ", group.Select(a => a.Node)),
                        group.Key,
                        incomeTask.Result,
                        priceTask.Result,
                        group.Any(a => a.Unverified));
                })
                .ToArray();

            var gpuQuotesTask = _gpuIncome.ResolveQuotesAsync(states, _config, _fleet, ct, gpuTargets);
            var coinSlugs = new HashSet<string>(_lifetime.KnownGpuCoinSlugs(states, _config), StringComparer.OrdinalIgnoreCase);
            foreach (var t in gpuTargets.Values) coinSlugs.Add(t.Coin);
            var coinQuotesTask = _gpuIncome.ResolveCoinQuotesAsync(coinSlugs, currency, ct);

            await Task.WhenAll(
                xtmPriceTask,
                Task.WhenAll(xtmGroupTasks),
                Task.WhenAll(gpuGroupTasks),
                gpuQuotesTask,
                coinQuotesTask);

            var xtmPrice = xtmPriceTask.Result;
            var xtmRows = xtmGroupTasks
                .Select(t => t.Result)
                .Select(r => new XtmIncomeRow(r.Nodes, r.Account, r.Income, r.Unverified, xtmPrice))
                .ToArray();

            return new PoolPanelsSnapshot(
                xtmAccounts,
                gpuTargets,
                xtmRows,
                gpuGroupTasks.Select(t => t.Result).ToArray(),
                gpuQuotesTask.Result,
                coinQuotesTask.Result);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void ApplyMarket(MarketSnapshot? snapshot)
    {
        if (snapshot is null) return;
        _network = snapshot.Network ?? _network;
        _price = snapshot.Price ?? _price;
        _money = snapshot.Money;
    }

    private void ApplyFastQuotes(IReadOnlyDictionary<string, NodeGpuQuote>? quotes, IReadOnlyList<NodeState> states)
    {
        if (quotes is null || quotes.Count == 0) return;
        var merged = new Dictionary<string, NodeGpuQuote>(_coinQuotes, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in quotes) merged[k] = v;
        _coinQuotes = merged;
        RefreshNodeQuotesFromKnownCoins(states);
    }

    private void ApplyPoolPanels(PoolPanelsSnapshot? snapshot, IReadOnlyList<NodeState> states)
    {
        if (snapshot is null) return;

        _xtmAccounts.Clear();
        foreach (var (k, v) in snapshot.XtmAccounts) _xtmAccounts[k] = v;

        _gpuTargets.Clear();
        foreach (var (k, v) in snapshot.GpuTargets) _gpuTargets[k] = v;

        _xtmIncome = snapshot.XtmIncome;
        _gpuIncomeRows = snapshot.GpuIncomeRows;

        var mergedCoins = new Dictionary<string, NodeGpuQuote>(_coinQuotes, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in snapshot.CoinQuotes) mergedCoins[k] = v;
        _coinQuotes = mergedCoins;

        var mergedNodes = new Dictionary<string, NodeGpuQuote>(_gpuQuotes, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in snapshot.GpuQuotes) mergedNodes[k] = v;
        _gpuQuotes = mergedNodes;
        RefreshNodeQuotesFromKnownCoins(states);
    }

    /// <summary>
    /// Waits for any tracked background task to finish, the wake-up deadline to pass, or a key press.
    /// Returns <c>null</c> when the user pressed a key or <paramref name="ct"/> was cancelled.
    /// </summary>
    private static async Task<Task?> WaitNextCompletionOrKeyAsync(
        IReadOnlyList<Task<(int Index, NodeState State)>> nodeTasks,
        Task? marketTask,
        Task? fastQuotesTask,
        Task? poolTask,
        DateTime wakeUpUtc,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (System.Console.KeyAvailable)
            {
                System.Console.ReadKey(intercept: true);
                return null;
            }

            foreach (var nt in nodeTasks)
                if (nt.IsCompleted) return nt;
            if (marketTask is { IsCompleted: true }) return marketTask;
            if (fastQuotesTask is { IsCompleted: true }) return fastQuotesTask;
            if (poolTask is { IsCompleted: true }) return poolTask;

            var now = DateTime.UtcNow;
            if (nodeTasks.Count == 0 && now >= wakeUpUtc)
                return Task.CompletedTask;

            var sliceMs = 60;
            if (wakeUpUtc > now)
            {
                var rem = (wakeUpUtc - now).TotalMilliseconds;
                if (rem < sliceMs) sliceMs = Math.Max(5, (int)rem);
            }

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(sliceMs, delayCts.Token);

            var candidates = new List<Task>(nodeTasks.Count + 4) { delayTask };
            candidates.AddRange(nodeTasks);
            if (marketTask is not null) candidates.Add(marketTask);
            if (fastQuotesTask is not null) candidates.Add(fastQuotesTask);
            if (poolTask is not null) candidates.Add(poolTask);

            var finished = await Task.WhenAny(candidates);
            if (finished != delayTask)
            {
                delayCts.Cancel();
                return finished;
            }

            if (nodeTasks.Count > 0 && DateTime.UtcNow >= wakeUpUtc)
            {
                // Give the caller a chance to trigger early pool refresh once online nodes are in.
                wakeUpUtc = DateTime.MaxValue;
                return Task.CompletedTask;
            }
        }

        return null;
    }

    private IRenderable Compose(IReadOnlyList<NodeState> states, FleetEconomics economics, FleetLifetimeSummary lifetime) =>
        RenderView(
            states,
            economics,
            _money ?? MoneyFormat.Single(economics.Currency),
            _network,
            _price,
            _xtmIncome,
            _gpuIncomeRows,
            DateTimeOffset.UtcNow,
            _sparklines,
            lifetime);

    public static IRenderable RenderView(
        IReadOnlyList<NodeState> states,
        FleetEconomics economics,
        MoneyFormat money,
        PoolNetworkStats? network,
        double? price,
        IReadOnlyList<XtmIncomeRow> xtmIncome,
        IReadOnlyList<GpuIncomeRow> gpuIncomeRows,
        DateTimeOffset now,
        SparklineHistory? sparklines = null,
        FleetLifetimeSummary? lifetime = null)
    {
        // Twelve columns already wrap an 80-column terminal, so the GPU pair only appears when
        // some node in the fleet actually has a card mining. A fleet with none loses nothing.
        var showGpu = states.Any(s => s.Gpu is not null);
        var showLifetime = lifetime is { AnyTracked: true };

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

        table.AddColumn(new TableColumn("Income/d").RightAligned().NoWrap());
        if (showLifetime)
        {
            table.AddColumn(new TableColumn("All-time").RightAligned().NoWrap());
        }
        table.AddColumn(new TableColumn("Uptime").RightAligned());

        foreach (var state in states.OrderBy(s => s.Node.Name, StringComparer.OrdinalIgnoreCase))
        {
            var miner = state.Snapshot?.Miner;
            var hardware = state.Snapshot?.Hardware;
            var nodeEconomics = economics.ForNode(state.Node.Name);

            var cpuSpark = sparklines?.CpuMarkup(state.Node.Name) ?? "";
            var cpuCell = (state.Hashrate > 0 ? $"[aqua]{Economics.FormatHashrate(state.Hashrate)}[/]" : "[grey]-[/]")
                + (cpuSpark.Length > 0 ? $"\n{cpuSpark}" : "");

            var cells = new List<IRenderable>
            {
                new Markup($"[bold]{UiHelpers.Escape(state.Node.Name)}[/]\n[grey]{UiHelpers.Escape(state.Node.Host)}[/]"),
                new Markup(UiHelpers.StatusBadge(state)),
                new Markup(cpuCell),
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
                var gpuSpark = sparklines?.GpuMarkup(state.Node.Name) ?? "";
                var gpuCell = UiHelpers.GpuBadge(state.Gpu) + (gpuSpark.Length > 0 ? $"\n{gpuSpark}" : "");
                cells.Add(new Markup(gpuCell));
                cells.Add(new Markup(UiHelpers.GpuShares(state.Gpu)));
            }

            cells.Add(new Markup(FormatNodeIncomeMarkup(nodeEconomics, money)));
            if (showLifetime)
            {
                cells.Add(new Markup(FormatNodeLifetimeMarkup(lifetime!.ForNode(state.Node.Name), money)));
            }
            cells.Add(new Markup(miner is { Running: true }
                ? Economics.FormatDuration(miner.UptimeSeconds)
                : "[grey]-[/]"));
            table.AddRow(cells);
        }

        var online = states.Count(s => s.Online);
        var mining = states.Count(s => s.AnyMining);
        var pending = states.Count(s => s.Pending);
        var pendingNote = pending > 0 ? $" [grey]({pending} polling)[/]" : "";

        var summary = new Grid().AddColumn().AddColumn().AddColumn();
        summary.AddRow(
            Cell("Fleet", $"[green]{mining}[/] mining / {online} online / {states.Count} total{pendingNote}"),
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

        if (showLifetime)
        {
            summary.AddRow(
                Cell("All-time income", EconomicsScreen.FormatLifetimeIncomeMarkup(lifetime!, money)),
                Cell("All-time power", $"{lifetime!.TotalEnergyKwh:N2} kWh  {money.Markup(lifetime.TotalElectricityCost)}"),
                Cell("All-time net", lifetime.TotalNetProfit is { } allNet
                    ? money.Signed(allNet)
                    : "[grey]needs pool prices[/]"));
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

    public static string FormatNodeLifetimeMarkup(NodeLifetimeSummary? node, MoneyFormat money)
    {
        if (node is null) return "[grey]-[/]";
        if (!node.HasData) return node.NeedsAgentUpgrade ? "[yellow]upgrade[/]" : "[grey]-[/]";

        if (node.TotalRevenue is { } total)
        {
            var netLine = node.NetProfit is { } net && (total > 0 || node.ElectricityCost > 0)
                ? $"\n[grey]net[/] {money.CompactSigned(net)}"
                : "";
            return $"{money.CompactMarkup(total)}{netLine}";
        }

        if (node.KnownRevenue is { } known && node.HasMissingRevenue)
        {
            var missing = node.GpuRevenueMissing ? "+ GPU ?" : "+ CPU ?";
            return $"{money.CompactMarkup(known)}\n[yellow]{missing}[/]";
        }

        return "[grey]?[/]";
    }

    private static IRenderable Cell(string label, string value) => new Markup($"[grey]{UiHelpers.Escape(label)}[/]  {value}");

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
