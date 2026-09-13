using System.Diagnostics;
using System.Globalization;

namespace MiningFleet.Agent;

/// <summary>Read-only fallback for drivers whose NVML draw is empty but power samples work.</summary>
public sealed class NvidiaPowerReader
{
    private DateTimeOffset _nextRead;
    private IReadOnlyDictionary<string, double> _cached = new Dictionary<string, double>();

    // HardwareService serialises callers. Failures expire the reading instead of reporting
    // old watts forever, and two bounded commands cannot consume the console's 8s timeout.
    public async Task<IReadOnlyDictionary<string, double>> ReadAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _nextRead) return _cached;
        _nextRead = DateTimeOffset.UtcNow.AddSeconds(10);
        var query = await RunAsync(["--query-gpu=pci.bus_id,name,power.draw", "--format=csv,noheader,nounits"], ct);
        var samples = query.Length > 0 && query.Split('\n').Any(line =>
            line.Split(',') is { Length: 3 } cells && Watts(cells[2]) is null)
            ? await RunAsync(["-q", "-d", "POWER"], ct)
            : "";
        return _cached = Parse(query, samples);
    }

    public static IReadOnlyDictionary<string, double> Parse(string query, string samples)
    {
        var byBus = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string? bus = null;
        var section = "";
        foreach (var line in samples.Split('\n'))
        {
            var text = line.Trim();
            if (line.StartsWith("GPU ", StringComparison.Ordinal))
            {
                bus = text[4..].Trim();
                section = "";
            }
            else if (text.Length > 0 && !text.Contains(':')) section = text;
            else if (bus is not null && text.IndexOf(':') is var colon && colon >= 0)
            {
                var key = text[..colon].Trim();
                var isDraw = section is "GPU Power Readings" or "Power Readings"
                    && key is "Power Draw" or "Average Power Draw" or "Instantaneous Power Draw";
                var isAverage = section == "Power Samples" && key == "Avg";
                if ((isDraw || isAverage) && Watts(text[(colon + 1)..]) is { } watts)
                    byBus.TryAdd(bus, watts);
            }
        }

        var readings = new List<(string Name, double? Watts)>();
        foreach (var line in query.Split('\n'))
        {
            var cells = line.Split(',');
            if (cells.Length != 3 || string.IsNullOrWhiteSpace(cells[1])) continue;
            var watts = Watts(cells[2]);
            if (watts is null && byBus.TryGetValue(cells[0].Trim(), out var sampled)) watts = sampled;
            readings.Add((cells[1].Trim(), watts));
        }

        // LHM's public snapshot identifies cards by name. Do not assign one board's watts
        // to two identical cards: leave them unknown until PCI identity is in the contract.
        return readings.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1 && group.Single().Watts is not null)
            .ToDictionary(group => group.Key, group => group.Single().Watts!.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static double? Watts(string text)
    {
        text = text.Trim();
        if (text.EndsWith(" W", StringComparison.Ordinal)) text = text[..^2].Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value) && value > 0 ? value : null;
    }

    private static async Task<string> RunAsync(string[] arguments, CancellationToken ct)
    {
        var exe = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe")
            : "/usr/bin/nvidia-smi";
        if (!File.Exists(exe)) return "";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try
        {
            if (!process.Start()) return "";
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            return process.ExitCode == 0 ? await output : "";
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception
                                   or IOException or InvalidOperationException)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception killError) when (killError is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            ct.ThrowIfCancellationRequested();
            return "";
        }
    }
}
