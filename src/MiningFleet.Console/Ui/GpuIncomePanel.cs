using Spectre.Console;
using Spectre.Console.Rendering;

namespace MiningFleet.Console.Ui;

/// <summary>One wallet's row: the cards mining to it, what the pool says it holds, and what it is worth.</summary>
public sealed record GpuIncomeRow(
    string Nodes,
    GpuPoolTarget Target,
    GpuIncome? Income,
    double? Price,
    bool ConfigUnverified = false);

/// <summary>
/// Pool wallet income for coins read from a pool's balance (Pearl on Kryptex), laid out like the XTM
/// panel so the two can be read side by side: accrued over the last day, a forecast for the next,
/// and how old and how well-founded each number is.
/// </summary>
public static class GpuIncomePanel
{
    public static IRenderable Render(IReadOnlyList<GpuIncomeRow> rows, MoneyFormat money, DateTimeOffset now)
    {
        if (rows.Count == 0) return new Text("");

        var tables = new List<IRenderable>();
        foreach (var coin in rows.GroupBy(r => r.Target.Coin.Split('-')[0].ToUpperInvariant()))
        {
            var table = new Table().Border(TableBorder.Rounded)
                .Title($"[bold]{UiHelpers.Escape(coin.Key)} — pool wallet income[/]")
                .AddColumn("Nodes / shared wallet")
                .AddColumn("Accrued / last 24h")
                .AddColumn("Forecast / next 24h")
                .AddColumn("Value / next 24h")
                .AddColumn("On the pool")
                .AddColumn("Data / basis");

            foreach (var row in coin) table.AddRow(Cells(row, coin.Key, money, now));
            tables.Add(table);
        }

        tables.Add(new Markup("[grey]Wallet totals include all workers, once per wallet. Income is the growth of what the " +
            "pool holds plus what it has paid, so it counts unconfirmed rewards. Forecast is a trailing mean that " +
            "includes pauses and assumes the same activity and pool conditions.[/]"));
        return new Rows(tables);
    }

    private static IRenderable[] Cells(GpuIncomeRow row, string coin, MoneyFormat money, DateTimeOffset now)
    {
        var income = row.Income;
        var nodes = new Markup(UiHelpers.Escape(row.Nodes));
        if (income is null)
        {
            var gap = new Markup("[grey]unavailable[/]");
            return [nodes, gap, gap, gap, gap, new Markup("[yellow]pool unavailable[/]" + Unverified(row))];
        }

        var day = income.Day;
        string accrued;
        if (day is not null) accrued = Amount(day.Amount, coin);
        else if (income.SinceStart is { } since) accrued = $"{Amount(since.Amount, coin)} [grey]in {Span(since.Hours)}[/]";
        else accrued = "[grey]unavailable[/]";

        var basis = row.ConfigUnverified ? null : income.ForecastBasis;
        var perDay = row.ConfigUnverified ? null : income.ForecastPerDay;
        var forecast = perDay is { } f ? $"~{Amount(f, coin)}" : "[grey]unavailable[/]";
        var value = perDay is { } p && row.Price is { } price
            ? "~" + money.Markup(p * price)
            : "[grey]unavailable[/]";

        var pool = income.Stats.Pending is { } pending
            ? Amount(pending, coin) + (income.Stats.Threshold is { } t ? $" [grey]/ {t:0.##}[/]" : "")
            : "[grey]-[/]";

        var age = income.ReadAt is { } at ? Math.Max(0, (now - at).TotalMinutes) : (double?)null;
        var status = age is { } a ? $"{a:0}m ago" : "age unknown";
        if (income.IsStale(now)) status += " [yellow]STALE[/]";
        else if (basis is not null) status += $" / {Span(basis.Hours)} mean";
        else if (!row.ConfigUnverified) status += " / need 1h history";
        status += Unverified(row);

        return [nodes, new Markup(accrued), new Markup(forecast), new Markup(value), new Markup(pool), new Markup(status)];
    }

    private static string Unverified(GpuIncomeRow row) => row.ConfigUnverified ? " [yellow]config unverified[/]" : "";

    private static string Span(double hours) => hours < 1 ? $"{hours * 60:0}m" : $"{hours:0.#}h";

    /// <summary>Pearl amounts start out as thousandths of a coin; a fixed two decimals would show them all as zero.</summary>
    public static string Amount(double value, string coin)
    {
        var digits = value switch { >= 100 => "N2", >= 1 => "N4", _ => "N6" };
        return $"{value.ToString(digits, System.Globalization.CultureInfo.CurrentCulture)} {UiHelpers.Escape(coin)}";
    }
}
