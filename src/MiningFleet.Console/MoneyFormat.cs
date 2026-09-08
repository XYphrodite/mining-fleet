using Spectre.Console;

namespace MiningFleet.Console;

/// <summary>
/// Renders an amount in the fleet currency, optionally echoing it in a second one.
///
/// The cross rate is derived from the pool quoting XMR in both currencies at the same
/// instant, so the two figures always agree with each other and with the XMR price shown
/// elsewhere. When the pool does not carry one of them the echo is simply dropped: a
/// converted number from an unrelated feed would quietly disagree with the rest of the screen.
/// </summary>
public sealed record MoneyFormat(string Currency, string? SecondaryCurrency, double? SecondaryPerPrimary)
{
    public static MoneyFormat Single(string currency) => new(currency, null, null);

    public bool HasSecondary => SecondaryCurrency is { Length: > 0 } && SecondaryPerPrimary is > 0;

    /// <summary>Plain amount, e.g. <c>13.88 RUB (0.32 USD)</c>. Null renders as a dash.</summary>
    public string Format(double? amount)
    {
        if (amount is null) return "-";
        var primary = $"{amount.Value:N2} {Currency}";
        return HasSecondary
            ? $"{primary} ({amount.Value * SecondaryPerPrimary!.Value:N2} {SecondaryCurrency})"
            : primary;
    }

    /// <summary>The same, marked up for the console: dimmed echo, dash in grey.</summary>
    public string Markup(double? amount)
    {
        if (amount is null) return "[grey]-[/]";
        var primary = $"{amount.Value:N2} {Escape(Currency)}";
        return HasSecondary
            ? $"{primary} [grey]({amount.Value * SecondaryPerPrimary!.Value:N2} {Escape(SecondaryCurrency!)})[/]"
            : primary;
    }

    /// <summary>Coloured by sign, for profit and loss.</summary>
    public string Signed(double? amount)
    {
        if (amount is null) return "[grey]-[/]";
        var colour = amount.Value >= 0 ? "green" : "red";
        var primary = $"[{colour}]{amount.Value:N2} {Escape(Currency)}[/]";
        return HasSecondary
            ? $"{primary} [grey]({amount.Value * SecondaryPerPrimary!.Value:N2} {Escape(SecondaryCurrency!)})[/]"
            : primary;
    }

    private static string Escape(string value) => Spectre.Console.Markup.Escape(value);
}
