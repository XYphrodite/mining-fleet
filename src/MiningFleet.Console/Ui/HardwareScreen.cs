using Spectre.Console;

namespace MiningFleet.Console.Ui;

/// <summary>Per-node hardware detail: components, sensors, temperatures, draw.</summary>
public sealed class HardwareScreen
{
    private readonly FleetConfig _config;
    private readonly FleetService _fleet;

    public HardwareScreen(FleetConfig config, FleetService fleet)
    {
        _config = config;
        _fleet = fleet;
    }

    public async Task ShowAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var node = UiHelpers.SelectNode(_config, "Hardware of which node?");
            if (node is null) return;

            UiHelpers.Header($"{node.Name} - hardware");

            using var client = _fleet.CreateClient(node, TimeSpan.FromSeconds(20));
            try
            {
                var snapshot = await AnsiConsole.Status().StartAsync("Reading sensors...", async _ => await client.GetStatusAsync(ct));
                if (snapshot is null)
                {
                    UiHelpers.Result(false, "Agent returned nothing.");
                    UiHelpers.Pause();
                    continue;
                }

                Render(snapshot.Agent, snapshot.Hardware, snapshot.Miner);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                UiHelpers.Result(false, ex.Message);
            }

            UiHelpers.Pause();
        }
    }

    private static void Render(Contracts.AgentInfoDto agent, Contracts.HardwareDto hw, Contracts.MinerStatusDto miner)
    {
        var overview = new Grid().AddColumn().AddColumn();
        overview.AddRow("[grey]Host[/]", UiHelpers.Escape(agent.Hostname));
        overview.AddRow("[grey]OS[/]", UiHelpers.Escape(agent.OperatingSystem));
        overview.AddRow("[grey]Agent[/]", $"{UiHelpers.Escape(agent.AgentVersion)} {(agent.IsElevated ? "[green](elevated)[/]" : "[yellow](not elevated)[/]")}");
        overview.AddRow("[grey]CPU[/]", $"{UiHelpers.Escape(hw.CpuName)}  [grey]{hw.PhysicalCores}c / {hw.LogicalCores}t[/]");
        overview.AddRow("[grey]Board[/]", UiHelpers.Escape(hw.MotherBoard ?? "-"));
        overview.AddRow("[grey]RAM[/]", hw.MemoryTotalGb > 0 ? $"{hw.MemoryUsedGb:0.0} / {hw.MemoryTotalGb:0.0} GB" : "-");
        overview.AddRow("[grey]CPU load[/]", hw.CpuLoadPercent is { } l ? $"{l:0}%" : "-");
        overview.AddRow("[grey]CPU temp[/]", UiHelpers.Temperature(hw.CpuTemperatureC));
        overview.AddRow("[grey]Draw[/]", hw.EstimatedPowerWatts is { } w
            ? $"{w:0} W {(hw.PowerIsMeasured ? "[green](measured)[/]" : "[yellow](configured estimate)[/]")}"
            : "[yellow]unknown - set a fallback in the node settings[/]");
        overview.AddRow("[grey]Miner[/]", miner.Running
            ? $"[green]running[/] pid {miner.Pid}  {Economics.FormatHashrate(miner.Hashrate60s ?? 0)}  v{UiHelpers.Escape(miner.Version)}"
            : miner.Installed ? "[grey]installed, stopped[/]" : "[yellow]not installed[/]");

        AnsiConsole.Write(new Panel(overview).Header("[bold]Overview[/]").Border(BoxBorder.Rounded).Expand());

        if (hw.Gpus.Count > 0)
        {
            var gpuTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
                .AddColumn("GPU").AddColumn("Temp").AddColumn("Load").AddColumn("Power").AddColumn("VRAM");

            foreach (var gpu in hw.Gpus)
            {
                gpuTable.AddRow(
                    UiHelpers.Escape(gpu.Name),
                    UiHelpers.Temperature(gpu.TemperatureC),
                    gpu.LoadPercent is { } load ? $"{load:0}%" : "-",
                    gpu.PowerWatts is { } power ? $"{power:0} W" : "-",
                    gpu is { MemoryUsedMb: { } used, MemoryTotalMb: { } total } ? $"{used:0} / {total:0} MB" : "-");
            }

            AnsiConsole.Write(gpuTable);
        }

        var temps = hw.Sensors.Where(s => s.Kind == "Temperature").ToList();
        var powers = hw.Sensors.Where(s => s.Kind == "Power").ToList();
        var fans = hw.Sensors.Where(s => s.Kind == "Fan").ToList();

        if (hw.SensorNotice is { Length: > 0 } notice)
            AnsiConsole.MarkupLine($"[yellow]{UiHelpers.Escape(notice)}[/]");

        if (temps.Count + powers.Count + fans.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No sensors reported.[/]");
            return;
        }

        var sensorTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .Title("[bold]Sensors[/]")
            .AddColumn("Component").AddColumn("Sensor").AddColumn(new TableColumn("Value").RightAligned());

        foreach (var sensor in temps.Concat(powers).Concat(fans))
        {
            sensorTable.AddRow(
                UiHelpers.Escape(sensor.Component),
                UiHelpers.Escape(sensor.Name),
                sensor.Kind == "Temperature" ? UiHelpers.Temperature(sensor.Value) : $"{sensor.Value:0.#} {UiHelpers.Escape(sensor.Unit)}");
        }

        AnsiConsole.Write(sensorTable);
    }
}
