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
}
