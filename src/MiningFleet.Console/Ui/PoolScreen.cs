using Spectre.Console;

namespace MiningFleet.Console.Ui;

/// <summary>What Hashvault says about the wallet: pool-side hashrate, shares, balance, payouts.</summary>
public sealed class PoolScreen
{
    private readonly FleetConfig _config;
    private readonly MarketService _market;

    public PoolScreen(FleetConfig config, MarketService market)
    {
        _config = config;
        _market = market;
    }

    public async Task ShowAsync(CancellationToken ct)
    {
        UiHelpers.Header("Pool & wallet");

        if (string.IsNullOrWhiteSpace(_config.Pool.Wallet))
        {
            AnsiConsole.MarkupLine("[yellow]No wallet configured.[/] Set it under Settings.");
            UiHelpers.Pause();
            return;
        }

        PoolWalletStats? wallet = null;
        PoolNetworkStats? network = null;
        double? price = null;
        MoneyFormat money = MoneyFormat.Single(_config.Electricity.Currency);

        await AnsiConsole.Status().StartAsync("Querying the pool...", async _ =>
        {
            var walletTask = _market.GetWalletStatsAsync(ct);
            var networkTask = _market.GetNetworkStatsAsync(ct);
            var priceTask = _market.GetPriceAsync(ct);
            await Task.WhenAll(walletTask, networkTask, priceTask);
            wallet = walletTask.Result;
            network = networkTask.Result;
            price = priceTask.Result;
            money = await _market.GetMoneyFormatAsync(ct);
        });

        var currency = _config.Electricity.Currency;

        AnsiConsole.MarkupLine($"[grey]wallet[/] {UiHelpers.Escape(_config.Pool.Wallet)}");
        AnsiConsole.MarkupLine($"[grey]pool[/]   {UiHelpers.Escape(_config.Pool.Url)}");
        AnsiConsole.WriteLine();

        if (wallet is null)
        {
            AnsiConsole.MarkupLine("[red]The pool API did not answer.[/] Check the address and Pool.ApiBase in the config.");
        }
        else
        {
            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("[grey]Pool hashrate (now)[/]", wallet.HashrateNow is { } h ? $"[aqua]{Economics.FormatHashrate(h)}[/]" : "-");
            grid.AddRow("[grey]Pool hashrate (1h)[/]", wallet.Hashrate1h is { } h1 ? Economics.FormatHashrate(h1) : "-");
            grid.AddRow("[grey]Pool hashrate (24h)[/]", wallet.Hashrate24h is { } h24 ? Economics.FormatHashrate(h24) : "-");
            grid.AddRow("[grey]Valid shares[/]", wallet.ValidShares?.ToString("N0") ?? "-");
            grid.AddRow("[grey]Invalid shares[/]", wallet.InvalidShares?.ToString("N0") ?? "-");
            grid.AddRow("[grey]Last share[/]", wallet.LastShare is { } last
                ? $"{last.ToLocalTime():yyyy-MM-dd HH:mm} [grey]({Economics.FormatDuration((DateTimeOffset.Now - last).TotalSeconds)} ago)[/]"
                : "-");
            AnsiConsole.Write(new Panel(grid).Header("[bold]This wallet on the pool[/]").Border(BoxBorder.Rounded).Expand());

            var balance = new Grid().AddColumn().AddColumn().AddColumn();
            balance.AddRow(Xmr("Confirmed balance", wallet.ConfirmedBalanceXmr, price, money, bold: true));
            balance.AddRow(Xmr("Unconfirmed", wallet.UnconfirmedBalanceXmr, price, money));
            balance.AddRow(Xmr("Credited today", wallet.CreditedTodayXmr, price, money));
            balance.AddRow(Xmr("Paid out total", wallet.TotalPaidXmr, price, money));
            balance.AddRow(
                new Markup("[grey]Payouts sent[/]"),
                new Markup(wallet.PaymentsSent?.ToString("N0") ?? "-"),
                new Markup(wallet.LastWithdrawal is { } w ? $"[grey]last {w.ToLocalTime():yyyy-MM-dd HH:mm}[/]" : ""));

            // Nothing arrives in the wallet until the threshold is crossed, so show the gap.
            if (wallet.PayoutThresholdXmr is { } threshold and > 0)
            {
                var pending = wallet.PendingXmr ?? 0;
                balance.AddRow(
                    new Markup("[grey]Payout threshold[/]"),
                    new Markup($"{threshold:0.000000} XMR"),
                    new Markup(pending >= threshold
                        ? "[green]reached[/]"
                        : $"[grey]{pending / threshold:P0} there, {threshold - pending:0.000000} XMR to go[/]"));
            }

            AnsiConsole.Write(new Panel(balance).Header("[bold]Balance[/]").Border(BoxBorder.Rounded).Expand());
        }

        if (network is not null)
        {
            var net = new Grid().AddColumn().AddColumn();
            net.AddRow("[grey]Pool hashrate[/]", network.PoolHashrate is { } ph ? Economics.FormatHashrate(ph) : "-");
            net.AddRow("[grey]Pool miners[/]", network.PoolMiners?.ToString("N0") ?? "-");
            net.AddRow("[grey]Network hashrate[/]", network.NetworkHashrate is { } nh ? Economics.FormatHashrate(nh) : "-");
            net.AddRow("[grey]Network difficulty[/]", network.NetworkDifficulty?.ToString("N0") ?? "-");
            net.AddRow("[grey]Block height[/]", network.NetworkHeight?.ToString("N0") ?? "-");
            net.AddRow("[grey]Block reward[/]", network.BlockRewardXmr is { } r ? $"{r:0.000} XMR" : "-");
            net.AddRow("[grey]Block time[/]", $"{network.BlockTimeSeconds}s");
            net.AddRow("[grey]XMR price[/]", money.Markup(price));
            AnsiConsole.Write(new Panel(net).Header("[bold]Pool & network[/]").Border(BoxBorder.Rounded).Expand());
        }

        UiHelpers.Pause();
    }

    private static Spectre.Console.Rendering.IRenderable[] Xmr(string label, double? amount, double? price, MoneyFormat money, bool bold = false)
    {
        var value = amount is null ? "-" : $"{amount.Value:0.000000} XMR";
        return
        [
            new Markup($"[grey]{UiHelpers.Escape(label)}[/]"),
            new Markup(bold ? $"[bold]{value}[/]" : value),
            new Markup(amount is { } a && price is { } p ? money.Markup(a * p) : ""),
        ];
    }
}
