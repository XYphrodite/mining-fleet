using Spectre.Console;

namespace MiningFleet.Console.Ui;

/// <summary>Fleet-wide settings: token, pool, wallet, electricity price, refresh rate.</summary>
public sealed class SettingsScreen
{
    private readonly FleetConfig _config;

    public SettingsScreen(FleetConfig config) => _config = config;

    public Task ShowAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UiHelpers.Header("Settings");

            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("[grey]Config file[/]", UiHelpers.Escape(_config.Path));
            grid.AddRow("[grey]Fleet token[/]", string.IsNullOrWhiteSpace(_config.Token) ? "[red]not set[/]" : "[green]set[/]");
            grid.AddRow("[grey]Default agent port[/]", _config.AgentPort.ToString());
            grid.AddRow("[grey]Refresh interval[/]", $"{_config.PollIntervalSeconds}s");
            grid.AddRow("[grey]Pool URL[/]", UiHelpers.Escape(_config.Pool.Url));
            grid.AddRow("[grey]Pool API[/]", UiHelpers.Escape(_config.Pool.ApiBase));
            grid.AddRow("[grey]Wallet[/]", string.IsNullOrWhiteSpace(_config.Pool.Wallet) ? "[red]not set[/]" : UiHelpers.Escape(_config.Pool.Wallet));
            grid.AddRow("[grey]Electricity[/]", $"{_config.Electricity.PricePerKwh:N2} {UiHelpers.Escape(_config.Electricity.Currency)} / kWh");
            AnsiConsole.Write(new Panel(grid).Border(BoxBorder.Rounded).Expand());
            AnsiConsole.WriteLine();

            var choice = AnsiConsole.Prompt(UiHelpers.Menu("Change", "< back",
                    "Fleet token",
                    "Pool & wallet",
                    "Electricity price",
                    "Refresh interval",
                    "Default agent port",
                    "< back"));

            switch (choice)
            {
                case "Fleet token":
                    _config.Token = AnsiConsole.Prompt(UiHelpers.Text("Shared token (must match Agent:Token on every node):")
                        .AllowEmpty().DefaultValue(_config.Token));
                    break;

                case "Pool & wallet":
                    _config.Pool.Url = AnsiConsole.Prompt(UiHelpers.Text("Stratum URL:").DefaultValue(_config.Pool.Url));
                    _config.Pool.Wallet = AnsiConsole.Prompt(UiHelpers.Text("Wallet address:").AllowEmpty().DefaultValue(_config.Pool.Wallet));
                    _config.Pool.ApiBase = AnsiConsole.Prompt(UiHelpers.Text("Pool REST API base:").DefaultValue(_config.Pool.ApiBase));
                    _config.Pool.Password = Blank(AnsiConsole.Prompt(UiHelpers.Text("Pool password (blank = worker name):")
                        .AllowEmpty().DefaultValue(_config.Pool.Password ?? "")));
                    break;

                case "Electricity price":
                    AnsiConsole.MarkupLine("[grey]This is the fleet default. A node with its own tariff keeps it — set that under Nodes.[/]");
                    _config.Electricity.PricePerKwh = AnsiConsole.Prompt(
                        new TextPrompt<double>("Default price per kWh:").DefaultValue(_config.Electricity.PricePerKwh));
                    _config.Electricity.Currency = AnsiConsole.Prompt(
                        UiHelpers.Text("Currency code (used for the price feed too):").DefaultValue(_config.Electricity.Currency)).ToUpperInvariant();
                    break;

                case "Refresh interval":
                    _config.PollIntervalSeconds = AnsiConsole.Prompt(
                        new TextPrompt<int>("Dashboard refresh, seconds:")
                            .DefaultValue(_config.PollIntervalSeconds)
                            .Validate(v => v is >= 1 and <= 300 ? ValidationResult.Success() : ValidationResult.Error("[red]1-300[/]")));
                    break;

                case "Default agent port":
                    _config.AgentPort = AnsiConsole.Prompt(new TextPrompt<int>("Port:").DefaultValue(_config.AgentPort));
                    break;

                default:
                    return Task.CompletedTask;
            }

            _config.Save();
            UiHelpers.Result(true, $"Saved to {_config.Path}");
            UiHelpers.Pause();
        }

        return Task.CompletedTask;
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
