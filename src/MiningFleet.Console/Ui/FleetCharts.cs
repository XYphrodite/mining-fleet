using System.Globalization;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MiningFleet.Console.Ui;

/// <summary>
/// Level-1 terminal charts: rolling Unicode block sparklines (<c>▁..█</c>) for live Dashboard cells
/// and Spectre <see cref="BarChart"/> / <see cref="BreakdownChart"/> summaries for Economics.
/// </summary>
public static class FleetCharts
{
    private static readonly char[] Bars = ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

    private static readonly Color[] NodePalette =
    [
        Color.Aqua,
        Color.SpringGreen2,
        Color.DodgerBlue1,
        Color.Gold1,
        Color.MediumPurple2,
        Color.Turquoise2,
    ];

    /// <summary>
    /// Formats a series of non-negative readings into a single-line Unicode sparkline scaled against
    /// <c>[0 .. peak]</c> rather than <c>[min .. max]</c>, so steady full-speed hashing renders as a
    /// level bar instead of magnifying 0.2% measurement jitter into false spikes, and pauses (<c>0</c>)
    /// stand out as <c>·</c>.
    /// </summary>
    public static string SparklineMarkup(IReadOnlyList<double> samples, string activeStyle = "dim aqua")
    {
        if (samples.Count < 2) return "";

        var peak = 0.0;
        foreach (var v in samples)
        {
            if (double.IsFinite(v) && v > peak) peak = v;
        }
        if (peak <= 0) return "";

        var sb = new StringBuilder();
        var inActive = false;

        foreach (var raw in samples)
        {
            var v = double.IsFinite(raw) && raw > 0 ? raw : 0.0;
            if (v <= 0)
            {
                if (inActive)
                {
                    sb.Append("[/]");
                    inActive = false;
                }
                sb.Append("[grey]·[/]");
                continue;
            }

            if (!inActive)
            {
                sb.Append('[').Append(activeStyle).Append(']');
                inActive = true;
            }

            var ratio = Math.Clamp(v / peak, 0.0, 1.0);
            var index = Math.Clamp((int)Math.Ceiling(ratio * Bars.Length) - 1, 0, Bars.Length - 1);
            sb.Append(Bars[index]);
        }

        if (inActive) sb.Append("[/]");
        return sb.ToString();
    }

    /// <summary>
    /// Renders <see cref="BreakdownChart"/> (CPU vs GPU share, or node shares on CPU-only fleets)
    /// and per-machine <see cref="BarChart"/> for the Economics screen. Returns null when no active
    /// node has measurable revenue yet.
    /// </summary>
    public static IRenderable? BuildEconomicsCharts(FleetEconomics economics)
    {
        var currency = economics.Currency;
        var active = economics.NodeBreakdown
            .Where(n => n.State.AnyMining)
            .Select(n => (Node: n, Value: n.TotalRevenuePerDay ?? n.KnownRevenuePerDay ?? 0.0))
            .Where(x => x.Value > 0 && double.IsFinite(x.Value))
            .OrderByDescending(x => x.Value)
            .ToList();

        if (active.Count == 0) return null;

        var widgets = new List<IRenderable>();

        var cpuRev = economics.RevenuePerDay is { } cr && cr > 0 ? cr : 0.0;
        var gpuRev = economics.GpuRevenuePerDay is { } gr && gr > 0 ? gr : 0.0;

        if (cpuRev > 0 && gpuRev > 0)
        {
            var sum = cpuRev + gpuRev;
            var breakdown = new BreakdownChart()
                .FullSize()
                .ShowTagValues()
                .UseValueFormatter(v => $"{v.ToString("N2", CultureInfo.CurrentCulture)} {currency}")
                .AddItem($"CPU ({cpuRev / sum:P0})", Math.Round(cpuRev, 2), Color.Aqua)
                .AddItem($"GPU ({gpuRev / sum:P0})", Math.Round(gpuRev, 2), Color.SpringGreen2);

            widgets.Add(new Panel(breakdown)
                .Header("[bold]Income mix (CPU vs GPU)[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey35)
                .Expand());
        }
        else if (active.Count > 1)
        {
            var sum = active.Sum(x => x.Value);
            var breakdown = new BreakdownChart()
                .FullSize()
                .ShowTagValues()
                .UseValueFormatter(v => $"{v.ToString("N2", CultureInfo.CurrentCulture)} {currency}");

            for (var i = 0; i < active.Count; i++)
            {
                var (node, value) = active[i];
                var label = $"{UiHelpers.Escape(node.State.Node.Name)} ({value / sum:P0})";
                breakdown.AddItem(label, Math.Round(value, 2), NodePalette[i % NodePalette.Length]);
            }

            widgets.Add(new Panel(breakdown)
                .Header("[bold]Fleet income share[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey35)
                .Expand());
        }

        var bar = new BarChart()
            .UseValueFormatter(v => $"{v.ToString("N2", CultureInfo.CurrentCulture)} {currency}");

        foreach (var (node, value) in active)
        {
            var suffix = node.HasMissingRevenue ? " (partial)" : "";
            var label = UiHelpers.Escape(node.State.Node.Name + suffix);
            var colour = node.ProfitPerDay switch
            {
                >= 0 => Color.SpringGreen2,
                < 0 => Color.Red1,
                _ => Color.Yellow,
            };
            bar.AddItem(label, Math.Round(value, 2), colour);
        }

        var legend = new Markup(
            "[springgreen2]■[/] [grey]profitable[/]   " +
            "[red1]■[/] [grey]power > income[/]   " +
            "[yellow]■[/] [grey]partial pool/price data[/]");

        widgets.Add(new Panel(new Rows(bar, legend))
            .Header($"[bold]Income per node / day ({UiHelpers.Escape(currency)})[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey35)
            .Expand());

        return new Rows(widgets);
    }
}

/// <summary>
/// Keeps the last <paramref name="capacity"/> CPU and GPU speed readings per node while the live
/// Dashboard is open, so each row can show a compact sparkline of recent activity and game pauses.
/// </summary>
public sealed class SparklineHistory(int capacity = 10)
{
    private readonly int _capacity = Math.Max(2, capacity);
    private readonly Dictionary<string, Queue<double>> _cpu = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<double>> _gpu = new(StringComparer.OrdinalIgnoreCase);

    public void Record(IEnumerable<NodeState> states)
    {
        foreach (var state in states) RecordOne(state);
    }

    public void RecordOne(NodeState state)
    {
        if (state.Pending) return;
        Push(_cpu, state.Node.Name, state.Mining ? state.Hashrate : 0.0);
        if (state.Gpu is not null || _gpu.ContainsKey(state.Node.Name))
        {
            Push(_gpu, state.Node.Name, state.GpuMining ? state.GpuHashrate : 0.0);
        }
    }

    public string CpuMarkup(string nodeName) =>
        _cpu.TryGetValue(nodeName, out var q) ? FleetCharts.SparklineMarkup(q.ToArray(), "dim aqua") : "";

    public string GpuMarkup(string nodeName) =>
        _gpu.TryGetValue(nodeName, out var q) ? FleetCharts.SparklineMarkup(q.ToArray(), "dim green") : "";

    private void Push(Dictionary<string, Queue<double>> map, string key, double value)
    {
        if (!map.TryGetValue(key, out var queue))
        {
            queue = new Queue<double>(_capacity);
            map[key] = queue;
        }
        if (queue.Count >= _capacity) queue.Dequeue();
        queue.Enqueue(double.IsFinite(value) && value > 0 ? value : 0.0);
    }
}
