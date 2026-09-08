using MiningFleet.Contracts;

namespace MiningFleet.Agent;

/// <summary>
/// Decides which rung the miner belongs on, and when it is allowed to climb back.
///
/// Kept free of processes, timers and Windows calls so the rule itself can be tested: everything
/// it needs arrives as arguments, including the clock. The parts that touch the machine live in
/// <see cref="ThrottleService"/>.
///
/// Coming down is immediate and going up waits. That asymmetry is the entire behaviour worth
/// having: somebody sitting at the machine must not wait for the miner to notice them, while the
/// miner can afford to wait two minutes to be sure they have really gone.
/// </summary>
public sealed class ThrottleLadder
{
    private DateTimeOffset? _quietSince;

    /// <summary>The rung in force, 0-100. Starts at full speed.</summary>
    public int Current { get; private set; } = 100;

    /// <summary>Why <see cref="Current"/> is what it is, in the words the console shows.</summary>
    public string Reason { get; private set; } = "not throttled";

    /// <summary>When the miner last changed rung, for "held at 25% for 4 minutes".</summary>
    public DateTimeOffset ChangedAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Feeds one load sample in. Returns true when the rung changed and the caller must act.
    /// </summary>
    public bool Update(double otherCpuPercent, IReadOnlyList<ThrottleStepDto> steps, int floorLevel, int rampUpSeconds, DateTimeOffset now)
    {
        var target = LevelFor(steps, otherCpuPercent, floorLevel);

        if (target < Current)
        {
            _quietSince = null;
            Set(target, $"other processes at {otherCpuPercent:0.#}% CPU", now);
            return true;
        }

        if (target > Current)
        {
            // The timestamp is deliberately not reset when the target climbs further: the machine
            // has been quieter than the current rung since that moment, which is what the wait is
            // measuring. Restarting it would let a machine that keeps getting quieter never rise.
            _quietSince ??= now;

            var waited = (now - _quietSince.Value).TotalSeconds;
            if (waited < rampUpSeconds)
            {
                Reason = $"other processes at {otherCpuPercent:0.#}% CPU, {rampUpSeconds - waited:0}s of quiet still needed";
                return false;
            }

            _quietSince = null;
            Set(target, $"quiet for {rampUpSeconds}s, other processes at {otherCpuPercent:0.#}% CPU", now);
            return true;
        }

        _quietSince = null;
        Reason = Current >= 100
            ? $"full speed, other processes at {otherCpuPercent:0.#}% CPU"
            : $"held at {Current}%, other processes at {otherCpuPercent:0.#}% CPU";
        return false;
    }

    /// <summary>Puts the ladder somewhere by hand - an operator's pinned level, or a reset to full.</summary>
    public void Force(int level, string reason, DateTimeOffset now)
    {
        _quietSince = null;
        if (Current != level) Set(level, reason, now);
        else Reason = reason;
    }

    private void Set(int level, string reason, DateTimeOffset now)
    {
        Current = level;
        Reason = reason;
        ChangedAt = now;
    }

    /// <summary>
    /// The rung for a given background load. Steps are read lowest threshold first; the last one
    /// the load reaches wins. A load below every threshold means nobody is in the way.
    /// </summary>
    public static int LevelFor(IReadOnlyList<ThrottleStepDto> steps, double otherCpuPercent, int floorLevel)
    {
        var level = 100;

        // Sorted here rather than trusted: these come from a config file an operator edits, and a
        // ladder listed out of order would otherwise throttle by whichever line happened to be last.
        foreach (var step in steps.OrderBy(s => s.OtherCpuPercent))
            if (otherCpuPercent >= step.OtherCpuPercent)
                level = step.Level;

        return Math.Clamp(Math.Max(level, floorLevel), 0, 100);
    }
}
