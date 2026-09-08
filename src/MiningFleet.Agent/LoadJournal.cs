namespace MiningFleet.Agent;

/// <summary>One minute of load, as it will be written down.</summary>
/// <param name="Minute">The minute this covers, at its start.</param>
/// <param name="Samples">Usable samples behind the averages.</param>
/// <param name="Unusable">Samples that could not be trusted; see <see cref="SystemLoadReader.Read"/>.</param>
/// <param name="OtherMeanPercent">Average load that was not the miner's.</param>
/// <param name="OtherPeakPercent">The worst second of the minute, which is the one that gets noticed.</param>
/// <param name="MinerMeanPercent">Average load the miner itself caused.</param>
/// <param name="MemoryPeakPercent">Highest physical memory in use.</param>
/// <param name="Level">The power rung in force, or null when throttling is switched off.</param>
public readonly record struct LoadSummary(
    DateTimeOffset Minute,
    int Samples,
    int Unusable,
    double OtherMeanPercent,
    double OtherPeakPercent,
    double MinerMeanPercent,
    double MemoryPeakPercent,
    int? Level);

/// <summary>
/// Folds the once-a-second load samples into one line a minute.
///
/// The point of this class is that it runs whether or not throttling is switched on. Until now the
/// only record a node kept of its own load was the throttle's list of rung changes, so a machine
/// with throttling off - which is the shipped default - wrote nothing at all, and the one question
/// an operator actually asks afterwards ("what was this thing doing when it felt slow?") had no
/// answer anywhere. The diagnostic was locked behind the remedy.
///
/// A minute is the bucket because a second is too many lines to keep and an hour hides the thing
/// worth seeing. Both the average and the peak are kept for the same reason: a download that
/// stutters is a burst, and an average over sixty seconds flattens exactly the spike that caused
/// the complaint.
///
/// Pure and clock-injected, like <see cref="ThrottleLadder"/> and <see cref="GpuPauseRule"/>, so
/// the boundary behaviour can be tested without waiting a minute for it.
/// </summary>
public sealed class LoadJournal
{
    private DateTimeOffset _minute;
    private bool _open;
    private int _samples;
    private int _unusable;
    private double _otherSum;
    private double _otherPeak;
    private double _minerSum;
    private double _memoryPeak;
    private int? _level;

    /// <summary>
    /// Takes one sample. Returns the minute that just ended, or null while it is still filling.
    ///
    /// <paramref name="level"/> is the rung in force, or null when throttling is off - and those
    /// are different answers, not two spellings of one. Rung 0 means the throttle has stopped the
    /// miner on purpose; off means nothing is watching at all.
    /// </summary>
    public LoadSummary? Add(SystemLoad load, int? level, DateTimeOffset now)
    {
        var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);

        LoadSummary? finished = null;
        if (_open && minute != _minute)
        {
            finished = Close();
            _open = false;
        }

        if (!_open)
        {
            _minute = minute;
            _open = true;
            _samples = 0;
            _unusable = 0;
            _otherSum = 0;
            _otherPeak = 0;
            _minerSum = 0;
            _memoryPeak = 0;
        }

        _level = level;

        // Memory is read straight from the OS and is trustworthy even when the CPU delta is not,
        // so it is kept from every sample rather than only the usable ones.
        if (load.MemoryUsedPercent > _memoryPeak) _memoryPeak = load.MemoryUsedPercent;

        if (!load.Usable)
        {
            _unusable++;
            return finished;
        }

        _samples++;
        _otherSum += load.OtherCpuPercent;
        _minerSum += load.MinerCpuPercent;
        if (load.OtherCpuPercent > _otherPeak) _otherPeak = load.OtherCpuPercent;

        return finished;
    }

    // There is deliberately no Flush for a shutting-down agent. The minute in progress is thrown
    // away, because a line covering twenty seconds looks exactly like one covering sixty and would
    // quietly understate a peak. The gap in timestamps says the agent restarted, which is the
    // truer thing to leave behind.

    private LoadSummary Close() => new(
        _minute,
        _samples,
        _unusable,
        _samples > 0 ? _otherSum / _samples : 0,
        _otherPeak,
        _samples > 0 ? _minerSum / _samples : 0,
        _memoryPeak,
        _level);
}
