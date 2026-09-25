namespace MiningFleet.Agent;

/// <summary>
/// When a wanted miner may be started again. Pure and clock-injected, and the part the
/// tests drive: a miner that exits immediately (bad pool, bad wallet) must not be hammered
/// back to life every tick, but a miner killed once after hours of work should come back
/// on the next tick.
/// </summary>
public static class WatchdogPolicy
{
    /// <summary>How long to wait before attempt number <paramref name="failures"/> (1-based).</summary>
    public static TimeSpan Delay(int failures) => failures switch
    {
        <= 1 => TimeSpan.Zero,
        2 => TimeSpan.FromSeconds(15),
        3 => TimeSpan.FromSeconds(30),
        4 => TimeSpan.FromMinutes(1),
        5 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// Whether a start is due. The first attempt after a death is immediate; each failure
    /// in a row pushes the next one out, up to five minutes. A success resets the count,
    /// so a miner that runs for hours and dies is restarted at once.
    /// </summary>
    public static bool IsDue(int failures, DateTimeOffset? lastAttempt, DateTimeOffset now) =>
        lastAttempt is null || now - lastAttempt.Value >= Delay(failures);

    /// <summary>
    /// One line for the status DTOs, so a node held down by repeated failures says why
    /// instead of sitting idle with nothing to explain it.
    /// </summary>
    public static string Describe(int failures, DateTimeOffset nextAttempt, string lastError) =>
        $"watchdog: restart {failures} failed ({lastError}), next try {nextAttempt:HH:mm:ss}";

    /// <summary>
    /// How long a running miner may report no hashrate before it counts as stuck. Short
    /// enough to matter, long enough to ride out a pool reconnect and a fresh start: both
    /// read zero for a minute or two with nothing wrong.
    /// </summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Folds one observation into the stuck clock. A running miner whose API answers but
    /// reports no hashrate keeps its first-zero time; anything else — stopped, blind API,
    /// healthy rate — clears it. Null API means blind, not stuck: restarting a miner the
    /// agent cannot see could kill one started by hand.
    /// </summary>
    public static DateTimeOffset? NextZeroSince(
        bool running, bool apiOk, double? hashrate, DateTimeOffset? zeroSince, DateTimeOffset now) =>
        running && apiOk && hashrate is 0 ? zeroSince ?? now : null;

    /// <summary>Whether the stuck clock has run out.</summary>
    public static bool IsStuck(DateTimeOffset? zeroSince, DateTimeOffset now) =>
        zeroSince is not null && now - zeroSince.Value >= StuckAfter;

    /// <summary>One line for the status DTOs when a restart is for a stall, not a death.</summary>
    public static string DescribeStuck(TimeSpan silentFor) =>
        $"watchdog: no hashrate for {(int)silentFor.TotalMinutes}m, restarting";
}
