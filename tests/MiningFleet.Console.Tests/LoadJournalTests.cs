using MiningFleet.Agent;

namespace MiningFleet.Console.Tests;

/// <summary>
/// Guards the node's record of its own load - the part that has to be right for the log to be
/// worth reading a week later.
///
/// The journal exists because throttling ships off, so a node used to write nothing at all about
/// its own CPU unless somebody had already switched the remedy on. "The machine felt slow an hour
/// ago" then had no answer anywhere, which is exactly the position we were in.
/// </summary>
public sealed class LoadJournalTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static SystemLoad Load(double other, double miner = 50, double memory = 60, bool usable = true) =>
        new(TotalCpuPercent: other + miner, MinerCpuPercent: miner, OtherCpuPercent: other,
            MemoryUsedPercent: memory, Usable: usable);

    [Fact]
    public void A_minute_is_written_once_and_only_when_it_ends()
    {
        var journal = new LoadJournal();

        for (var second = 0; second < 60; second++)
            Assert.Null(journal.Add(Load(10), level: null, Noon.AddSeconds(second)));

        // The line appears on the first sample of the next minute, not on the last of this one:
        // the journal cannot know a minute is over until something arrives from the next.
        var closed = journal.Add(Load(10), level: null, Noon.AddMinutes(1));

        Assert.NotNull(closed);
        Assert.Equal(Noon, closed!.Value.Minute);
        Assert.Equal(60, closed.Value.Samples);
    }

    /// <summary>
    /// The whole reason both numbers are kept. A machine pinned for eight seconds and idle for the
    /// other fifty-two is the shape of a download or a build - the thing somebody reaches for the
    /// off switch over - and its average is unremarkable.
    /// </summary>
    [Fact]
    public void The_peak_survives_an_average_that_would_hide_it()
    {
        var journal = new LoadJournal();

        for (var second = 0; second < 8; second++) journal.Add(Load(95), null, Noon.AddSeconds(second));
        for (var second = 8; second < 60; second++) journal.Add(Load(0), null, Noon.AddSeconds(second));

        var minute = journal.Add(Load(0), null, Noon.AddMinutes(1))!.Value;

        Assert.Equal(95, minute.OtherPeakPercent);
        Assert.InRange(minute.OtherMeanPercent, 12, 13);
    }

    [Fact]
    public void Unusable_samples_are_counted_rather_than_averaged_in_as_quiet()
    {
        var journal = new LoadJournal();

        journal.Add(Load(80), null, Noon);
        journal.Add(Load(0, usable: false), null, Noon.AddSeconds(1));
        journal.Add(Load(0, usable: false), null, Noon.AddSeconds(2));

        var minute = journal.Add(Load(0), null, Noon.AddMinutes(1))!.Value;

        // Averaging a sample the reader itself refused to vouch for would turn "could not tell"
        // into "the machine was idle", which is the opposite claim.
        Assert.Equal(1, minute.Samples);
        Assert.Equal(2, minute.Unusable);
        Assert.Equal(80, minute.OtherMeanPercent);
    }

    [Fact]
    public void A_minute_nobody_could_measure_still_gets_a_line()
    {
        var journal = new LoadJournal();

        for (var second = 0; second < 60; second++)
            journal.Add(Load(0, usable: false), null, Noon.AddSeconds(second));

        var minute = journal.Add(Load(0), null, Noon.AddMinutes(1))!.Value;

        // Silence would be indistinguishable from the agent being down. Zero usable samples is
        // itself a fact worth writing, and ThrottleLog spells it out instead of printing zeros.
        Assert.Equal(0, minute.Samples);
        Assert.Equal(60, minute.Unusable);
    }

    /// <summary>
    /// Throttling off and the miner held at 0% are different states, and the log has to be able to
    /// tell them apart: the first is a machine nobody is watching, the second is one the throttle
    /// deliberately stopped.
    /// </summary>
    [Fact]
    public void Throttling_off_reads_differently_from_a_rung_of_zero()
    {
        var journal = new LoadJournal();

        journal.Add(Load(10), level: null, Noon);
        var off = journal.Add(Load(10), level: 0, Noon.AddMinutes(1))!.Value;

        journal.Add(Load(10), level: 0, Noon.AddMinutes(1).AddSeconds(1));
        var stopped = journal.Add(Load(10), level: 0, Noon.AddMinutes(2))!.Value;

        Assert.Null(off.Level);
        Assert.Equal(0, stopped.Level);
    }

    [Fact]
    public void A_gap_in_sampling_does_not_merge_two_minutes_into_one()
    {
        var journal = new LoadJournal();

        journal.Add(Load(20), null, Noon);
        // The agent was busy, or asleep, or the tick threw. Whatever the reason, the minute that
        // was open must close on its own terms rather than absorb the samples that follow it.
        var closed = journal.Add(Load(70), null, Noon.AddMinutes(5));

        Assert.NotNull(closed);
        Assert.Equal(Noon, closed!.Value.Minute);
        Assert.Equal(20, closed.Value.OtherPeakPercent);
    }

    [Fact]
    public void Memory_is_kept_from_samples_the_cpu_delta_could_not_be_trusted_in()
    {
        var journal = new LoadJournal();

        journal.Add(Load(10, memory: 40), null, Noon);
        journal.Add(Load(0, memory: 91, usable: false), null, Noon.AddSeconds(1));

        var minute = journal.Add(Load(10), null, Noon.AddMinutes(1))!.Value;

        // A CPU percentage is a difference between two samples and can be nonsense; the memory
        // figure is read straight from the OS and is sound whatever the CPU delta did.
        Assert.Equal(91, minute.MemoryPeakPercent);
    }
}
