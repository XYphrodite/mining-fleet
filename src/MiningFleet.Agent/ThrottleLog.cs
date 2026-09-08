using System.Globalization;
using System.Text;

namespace MiningFleet.Agent;

/// <summary>
/// The node's own record of what its CPU was doing and what the throttle did about it, in
/// throttle.log beside the agent. Two kinds of line, interleaved in time on purpose: a rung
/// change when one happens, and one summary line a minute always.
///
/// The thresholds shipped with this feature are a guess. Tuning them from how the machine felt is
/// how we spent a day chasing a hashrate effect that eleven guesses could not explain, and the
/// only thing that moved it was a log of numbers. So the log carries the inputs, not just the
/// outcome: a week of it should say plainly which rung fired for nothing.
///
/// The per-minute line is written whether or not throttling is switched on, which is the whole
/// point of it. Throttling ships off, so until now a node kept no record of its own load at all,
/// and "the machine felt slow an hour ago" could only ever be answered by guessing.
///
/// Bounded the same way <see cref="FileLogger"/> is - a node runs unattended for months, and no
/// diagnostic is worth filling its disk. Four megabytes at roughly 150 KB a day is about a month,
/// and the rotation keeps the month before that.
/// </summary>
public sealed class ThrottleLog
{
    private const long MaxBytes = 4 * 1024 * 1024;

    private readonly string _path;
    private readonly object _gate = new();

    public ThrottleLog(string basePath) => _path = Path.Combine(basePath, "throttle.log");

    public void Record(int from, int to, string reason, SystemLoad load)
    {
        // Also in threads, because the percentage alone hides the thing most likely to need
        // fixing: the ladder is read against the whole machine, so one thread of somebody's work
        // is 8% on a 12-thread node and 3.6% on a 28-thread one. The same rung means different
        // amounts of interference on different rigs, and this column is what will show it.
        var busyThreads = load.OtherCpuPercent * Environment.ProcessorCount / 100.0;

        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss}  {1,3} -> {2,-3}  cpu={3:0.#}% other={4:0.#}% ({5:0.0} of {6} threads) miner={7:0.#}% mem={8:0}%  {9}",
            DateTime.Now, from, to, load.TotalCpuPercent, load.OtherCpuPercent,
            busyThreads, Environment.ProcessorCount, load.MinerCpuPercent, load.MemoryUsedPercent, reason);

        Append(line);
    }

    /// <summary>
    /// One minute of load. Written every minute, throttling on or off.
    ///
    /// The peak is beside the average because they answer different questions and the average
    /// alone is the misleading one: a machine that was pinned for eight seconds and idle for the
    /// other fifty-two reads as barely busy, and that is exactly the shape of a download or a
    /// build that made somebody reach for the off switch.
    /// </summary>
    public void Record(LoadSummary minute)
    {
        // The rung, or "off". A node with throttling switched off is not the same as one held at
        // 0%: the first is unwatched, the second was stopped on purpose.
        var rung = minute.Level is { } level ? $"{level,3}%" : " off";

        // A minute nobody could measure says so rather than printing a row of zeros, which would
        // read as a quiet machine - the opposite of what an unusable sample means.
        var body = minute.Samples > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "other avg={0:0.#}% peak={1:0.#}% ({2:0.0} of {3} threads)  miner={4:0.#}%  mem={5:0}%",
                minute.OtherMeanPercent, minute.OtherPeakPercent,
                minute.OtherPeakPercent * Environment.ProcessorCount / 100.0, Environment.ProcessorCount,
                minute.MinerMeanPercent, minute.MemoryPeakPercent)
            : $"no usable sample in {minute.Unusable} attempt(s)";

        var dropped = minute.Samples > 0 && minute.Unusable > 0 ? $"  [{minute.Unusable} unusable]" : "";

        Append(string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm}     load {1}  {2}{3}",
            minute.Minute.LocalDateTime, rung, body, dropped));
    }

    /// <summary>
    /// One change of the miner's thread count, with the temperature behind it.
    ///
    /// In the same file as the rungs and the per-minute load, because they are the same question
    /// asked three ways — what was this machine allowed to do, and why — and an operator chasing a
    /// node that is slower than its twin should not have to know which of three logs to open.
    /// </summary>
    public void RecordThreads(int from, int to, double? temperatureC, string reason)
    {
        var temp = temperatureC is { } c
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.#}C", c)
            : "no reading";

        Append(string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss}  threads {1,2} -> {2,-2}  cpu={3,-11}  {4}",
            DateTime.Now, from, to, temp, reason));
    }

    /// <summary>
    /// The CPU miner standing aside for somebody at the machine, or coming back.
    ///
    /// Recorded here rather than only in the agent log because this file is the node's own account
    /// of what it was allowed to do, and "why was this rig idle last night" is the question it
    /// exists to answer.
    /// </summary>
    public void RecordPause(bool paused, string reason) =>
        Append(string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss}  miner {1,-8}  {2}",
            DateTime.Now, paused ? "paused" : "resumed", reason));

    private void Append(string line)
    {
        lock (_gate)
        {
            try
            {
                Rotate();
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A node that cannot write its diagnostic must still mine.
            }
        }
    }

    /// <summary>The tail of the log, newest last, for the console to show without an RDP session.</summary>
    public IReadOnlyList<string> Tail(int lines)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return [];
                return File.ReadLines(_path).TakeLast(Math.Max(1, lines)).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    private void Rotate()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < MaxBytes) return;

        var previous = _path + ".1";
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(_path, previous);
    }
}
