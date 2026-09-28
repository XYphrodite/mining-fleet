using Spectre.Console;
using Spectre.Console.Rendering;

namespace MiningFleet.Console.Ui;

public static class XtmIncomePanel
{
    public static IRenderable Render(IReadOnlyList<XtmIncomeRow> rows, DateTimeOffset now)
    {
        // An unknown node is not an XTM account. Keep failures for known accounts visible,
        // but leave unrelated/offline nodes to the main fleet table.
        var wallets = rows.Where(row => row.Account is not null).ToList();
        if (wallets.Count == 0) return new Text("");
        var table = new Table().Border(TableBorder.Rounded)
            .Title("[bold]XTM — pool wallet income[/]")
            .AddColumn("Nodes / shared wallet")
            .AddColumn("Accrued / last 24h")
            .AddColumn("Forecast / next 24h")
            .AddColumn("Data / basis");
        foreach (var row in wallets)
        {
            var income = row.Income;
            var basis = row.ConfigUnverified ? null : income?.ForecastBasis(now);
            var accrued = income?.Day is { } day ? $"{day.Amount:N2} XTM" : "[grey]unavailable[/]";
            var forecast = basis is not null ? $"~{basis.Amount * 24 / basis.Hours:N2} XTM"
                : "[grey]unavailable[/]";
            string status;
            if (income is null) status = "[yellow]pool unavailable[/]";
            else
            {
                var age = Math.Max(0, (now - income.Day!.End).TotalMinutes);
                status = $"{age:0}m ago";
                if (income.IsStale(now)) status += " [yellow]STALE[/]";
                else if (basis is not null) status += $" / {basis.Hours}h mean";
                else if (!row.ConfigUnverified) status += " / need 24h history";
            }
            if (row.ConfigUnverified) status += " [yellow]config unverified[/]";
            table.AddRow(UiHelpers.Escape(row.Nodes), accrued, forecast, status);
        }
        return new Rows(table, new Markup("[grey]Wallet totals include all workers, once per wallet. "
            + "Accruals include locked rewards. Forecast assumes the same activity and pool conditions; "
            + "pauses are included in the historical mean.[/]"));
    }
}
