using Spectre.Console;
using MiningFleet.Console.Ui;

namespace MiningFleet.Console;

/// <summary>The `update` command and the start-up "a newer version exists" notice.</summary>
public static class Updater
{
    public static async Task<int> RunAsync(FleetConfig config, bool checkOnly, CancellationToken ct)
    {
        using var service = new UpdateService(config.Update);

        UpdateInfo? update;
        try
        {
            update = await AnsiConsole.Status().StartAsync(
                $"Checking {UiHelpers.Escape(config.Update.Repository)}...",
                async _ => await service.CheckAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            UiHelpers.Result(false, ex.Message);
            return 2;
        }

        if (update is null)
        {
            AnsiConsole.MarkupLine($"[green]xmrig-fleet {UpdateService.CurrentVersion} is the latest release.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine(
            $"[yellow]{UpdateService.CurrentVersion}[/] -> [green]{update.Version}[/]  " +
            $"[grey]{UiHelpers.Escape(update.AssetName)}, {Size(update.SizeBytes)}[/]");

        if (!string.IsNullOrWhiteSpace(update.Notes))
        {
            var notes = update.Notes.Trim();
            if (notes.Length > 600) notes = notes[..600] + "...";
            AnsiConsole.Write(new Panel(UiHelpers.Escape(notes))
                .Header("[bold]Release notes[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey35).Expand());
        }

        // `update --check` is for scripts: report and exit without touching the installation.
        if (checkOnly) return 1;

        // A redirected console cannot prompt, so an unattended `update` proceeds.
        if (!System.Console.IsInputRedirected &&
            !AnsiConsole.Confirm($"Install {update.Tag} into {UiHelpers.Escape(UpdateService.InstallDirectory)}?", defaultValue: true))
        {
            return 0;
        }

        try
        {
            var message = await DownloadWithProgressAsync(service, update, ct);
            UiHelpers.Result(true, message);
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            UiHelpers.Result(false, $"Update failed: {ex.Message}");
            AnsiConsole.MarkupLine("[grey]The running installation was left as it was.[/]");
            return 2;
        }
    }

    private static async Task<string> DownloadWithProgressAsync(UpdateService service, UpdateInfo update, CancellationToken ct)
    {
        string message = "";

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new DownloadedColumn(),
                new TransferSpeedColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async context =>
            {
                // The declared asset size is a good enough maximum until the response says otherwise.
                var task = context.AddTask($"[green]{UiHelpers.Escape(update.Tag)}[/]",
                    maxValue: update.SizeBytes > 0 ? update.SizeBytes : 1);

                message = await service.ApplyAsync(update, (received, total) =>
                {
                    if (total is > 0 && Math.Abs(task.MaxValue - total.Value) > 0.5) task.MaxValue = total.Value;
                    task.Value = received;
                }, ct);

                task.Value = task.MaxValue;
                task.StopTask();
            });

        return message;
    }

    /// <summary>
    /// Start-up check for the interactive console. Never throws and never blocks the menu:
    /// a missing network or an unpublished repository must not stand between the operator
    /// and their fleet.
    /// </summary>
    public static async Task NotifyIfOutdatedAsync(FleetConfig config, CancellationToken ct)
    {
        if (!config.Update.CheckOnStart || string.IsNullOrWhiteSpace(config.Update.Repository)) return;

        try
        {
            using var service = new UpdateService(config.Update);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));

            if (await service.CheckAsync(timeout.Token) is { } update)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]xmrig-fleet {update.Version} is available[/] [grey](running {UpdateService.CurrentVersion}) - " +
                    "run 'xmrig-fleet update'[/]");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or OperationCanceledException)
        {
        }
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 20 => $"{bytes / (double)(1 << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1 << 10):0.0} KB",
        > 0 => $"{bytes} B",
        _ => "size unknown",
    };
}
