using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;

namespace MiningFleet.Agent;

/// <summary>
/// A tested hypothesis that turned out to be wrong. It is kept so nobody tries it again, and
/// this comment exists to stop the code being read as a remedy - which it is not.
///
/// The problem it was meant to solve: an i7-12700KF mines RandomX at 4,380 H/s with nothing
/// watching and 7,092 H/s with Task Manager open, and 7,097 with Resource Monitor. The two
/// monitors agreeing to within a tenth of a percent said the effect belonged to something they
/// share rather than to either application, and the obvious shared thing is that both poll the
/// performance-counter subsystem continuously. So: let the agent poll the same counters and the
/// window becomes unnecessary.
///
/// It does not. Polling them from here changes the hashrate by nothing measurable. Whatever
/// Windows does differently, it is not "somebody is reading these counters" - or not only that,
/// because a service in session 0 reading them is evidently not the same as a window in the
/// logged-on session reading them. That is why <see cref="SessionMonitorService"/> still keeps a
/// real Task Manager open, and why it says in its own comments that it is a remedy without a
/// diagnosis.
///
/// This was the ninth of eleven explanations tried and discarded, each by a controlled A/B on
/// that node. The others: huge pages (constant at 1180/1180 throughout), free memory (20 GB
/// spare either way), CPU frequency, competing processes (nothing above 0.5%), xmrig's own
/// priority (worth +26% on its own, not this), the High Performance power plan, a 1 ms timer
/// resolution, opting the process out of EcoQoS, Win32PrioritySeparation, and simply having a
/// window open - a visible Notepad changes nothing.
///
/// Left in the build rather than deleted because a negative result is worth keeping where the
/// next person will look for it, and because polling three counters once a second costs nothing
/// worth measuring. Switch it off with Agent:PollPerformanceCounters if that judgement is wrong.
///
/// Counters are added by their English names through PdhAddEnglishCounter: the localised path a
/// tool like typeperf expects fails outright on a non-English Windows, which is how this was
/// first noticed.
/// </summary>
public sealed class PerformanceCounterPump : BackgroundService
{
    // What Task Manager and Resource Monitor read continuously. The frequency counter is the one
    // that matters if the mechanism really is P-state management; the load counter is cheap and
    // makes the query resemble a real monitor more closely.
    private static readonly string[] CounterPaths =
    [
        @"\Processor Information(_Total)\% Processor Performance",
        @"\Processor Information(_Total)\% Processor Utility",
        @"\Processor(_Total)\% Processor Time",
    ];

    private const uint ErrorSuccess = 0;

    private readonly IOptions<AgentOptions> _options;
    private readonly ILogger<PerformanceCounterPump> _log;

    public PerformanceCounterPump(IOptions<AgentOptions> options, ILogger<PerformanceCounterPump> log)
    {
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;

        if (!options.PollPerformanceCounters) return;
        if (!OperatingSystem.IsWindows())
        {
            _log.LogDebug("Performance counter polling is a Windows behaviour; skipping.");
            return;
        }

        var query = IntPtr.Zero;
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out query) != ErrorSuccess)
            {
                _log.LogWarning("Could not open a PDH query; hashrate on hybrid CPUs may sit below what this node can do.");
                return;
            }

            var added = 0;
            foreach (var path in CounterPaths)
            {
                // A counter missing on this SKU must not take the others down with it.
                if (PdhAddEnglishCounterW(query, path, IntPtr.Zero, out _) == ErrorSuccess) added++;
                else _log.LogDebug("Performance counter not available on this machine: {Counter}", path);
            }

            if (added == 0)
            {
                _log.LogWarning("No performance counters could be added; not polling.");
                return;
            }

            // PDH needs two collections before rate counters mean anything. Nothing reads the
            // values here, but a monitor would, so behave like one.
            PdhCollectQueryData(query);

            var interval = TimeSpan.FromMilliseconds(Math.Clamp(options.PerformanceCounterIntervalMs, 200, 10_000));
            _log.LogInformation("Polling {Count} performance counters every {Interval} to keep the CPU out of its background performance state.", added, interval);

            while (!stoppingToken.IsCancellationRequested)
            {
                PdhCollectQueryData(query);
                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Everything is caught on purpose. An unhandled exception in a BackgroundService
            // stops the whole host by default, and this service only makes mining faster - it
            // must never be the reason a node stops answering the console. P/Invoke into a
            // missing or differently-shaped pdh.dll would otherwise do exactly that.
            _log.LogWarning(ex, "Performance counter polling is off; mining continues, possibly below this CPU's rate.");
        }
        finally
        {
            if (query != IntPtr.Zero) PdhCloseQuery(query);
        }
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    /// <summary>English counter names, so a localised Windows resolves them the same way.</summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
