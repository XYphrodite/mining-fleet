using Spectre.Console;
using Spectre.Console.Rendering;

namespace MiningFleet.Console.Ui;

public static class XtmIncomePanel
{
    public static IRenderable Render(IReadOnlyList<XtmIncomeRow> rows, DateTimeOffset now)
    {
        if (rows.Count == 0) return new Text("");
        var table = new Table().Border(TableBorder.Rounded)
            .Title("[bold]XTM — pool wallet income[/]")
            .AddColumn("Nodes / shared wallet")
            .AddColumn("Accrued / last 24h")
            .AddColumn("Forecast / next 24h")
            .AddColumn("Data / basis");
        foreach (var row in rows)
        {
            var income = row.Income;
            var basis = row.ConfigUnverified ? null : income?.ForecastBasis(now);
            var accrued = income?.Day is { } day ? $"{day.Amount:N2} XTM" : "[grey]unavailable[/]";
            var forecast = basis is not null ? $"~{basis.Amount * 24 / basis.Hours:N2} XTM"
                : "[grey]unavailable[/]";
            string status;
            if (row.Account is null) status = "[yellow]pool unsupported / config unavailable[/]";
            else if (income is null) status = "[yellow]pool unavailable[/]";
            else
            {
                var age = Math.Max(0, (now - income.Day!.End).TotalMinutes);
                status = $"{age:0}m ago";
                if (income.IsStale(now)) status += " [yellow]STALE[/]";
                else if (basis is null && !row.ConfigUnverified) status += " / need 24h history";
                else status += $" / {basis.Hours}h mean";
            }
            if (row.ConfigUnverified) status += " [yellow]config unverified[/]";
            table.AddRow(UiHelpers.Escape(row.Nodes), accrued, forecast, status);
        }
        return new Rows(table, new Markup("[grey]Wallet totals include all workers, once per wallet. "
            + "Accruals include locked rewards. Forecast assumes the same activity and pool conditions; "
            + "pauses are included in the historical mean.[/]"));
    }
}
