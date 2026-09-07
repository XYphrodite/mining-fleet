namespace XmrigFleet.Agent;

/// <summary>
/// Decides how many mining threads a node may run, from its temperature and its operator's
/// ceiling. Pure and clock-injected, like <see cref="ThrottleLadder"/> and
/// <see cref="GpuPauseRule"/>, so the part that decides can be tested without a hot CPU.
///
/// Thread count is the actuator because the alternatives were measured and are worse. A hard CPU
/// cap keeps 27.6% of the hashrate at half throttle, since freezing threads costs RandomX its
/// scratchpad. Lowering priority on a hybrid CPU costs 85%, because Windows parks a background
/// process on the E-cores. Reserving cores by affinity does not cool anything: it moves the same
/// work onto fewer cores, and the package ran 4 C *hotter* for it. Dropping threads does less
/// work and spreads what is left — on an i7-12700KF, 12 threads give 7,547 H/s at 99.7 C and 8
/// give 6,428 at 89.3 C.
///
/// Down quickly, up slowly. Heat is cumulative and its cost is paid in silicon, so a node that has
/// just been too hot waits out a long quiet stretch before asking for another thread, while one
/// that is too hot now gives one up on the next tick it is allowed to.
/// </summary>
public sealed class ThermalGovernor
{
    /// <summary>
    /// How far below the ceiling it must sit before a thread is worth asking for again. Without a
    /// margin a node settles exactly on its limit and then oscillates across it forever, paying a
    /// thread change for every crossing.
    /// </summary>
    public const double Margin = 3.0;

    private static readonly TimeSpan StepDownAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StepUpAfter = TimeSpan.FromMinutes(10);

    private DateTimeOffset? _changedAt;
    private DateTimeOffset? _coolSince;

    /// <summary>Why the thread count is what it is, in the words the log and the console show.</summary>
    public string Reason { get; private set; } = "no temperature limit set";

    /// <summary>
    /// The thread count the miner should be running.
    /// </summary>
    /// <param name="current">Threads the miner is running now.</param>
    /// <param name="ceiling">The most the operator's percentage allows; also the starting point.</param>
    /// <param name="temperature">CPU package temperature, or null when the node cannot read one.</param>
    /// <param name="limit">The operator's ceiling in Celsius, or null for no thermal governing.</param>
    public int Decide(int current, int ceiling, double? temperature, double? limit, DateTimeOffset now)
    {
        if (ceiling <= 0) return current;

        if (limit is not { } max)
        {
            // No thermal rule: the percentage ceiling is the whole answer, and a node that was
            // stepped down under an older setting has to be let back up in one go rather than a
            // thread every ten minutes.
            _coolSince = null;
            Reason = "no temperature limit set";
            return ceiling;
        }

        if (temperature is not { } now_c)
        {
            // A node that cannot read its temperature holds what it has. Stepping up would be
            // guessing that it is cool, and stepping down would be guessing that it is hot; the
            // same three-way shape SystemLoadReader uses for a sample it cannot trust.
            _coolSince = null;
            Reason = $"holding {current} thread(s): no CPU temperature to read";
            return current;
        }

        if (now_c > max)
        {
            _coolSince = null;

            if (current <= 1)
            {
                Reason = $"{now_c:0.#}C is over {max:0.#}C but one thread is the floor";
                return current;
            }
            if (_changedAt is { } last && now - last < StepDownAfter)
            {
                Reason = $"{now_c:0.#}C is over {max:0.#}C, waiting for the last change to settle";
                return current;
            }

            _changedAt = now;
            Reason = $"{now_c:0.#}C is over {max:0.#}C, dropping to {current - 1} thread(s)";
            return current - 1;
        }

        if (current >= ceiling)
        {
            _coolSince = null;
            Reason = $"{now_c:0.#}C is under {max:0.#}C at the full {ceiling} thread(s)";
            return current;
        }

        if (now_c > max - Margin)
        {
            // Under the limit but not under it by enough to be worth testing another thread.
            _coolSince = null;
            Reason = $"{now_c:0.#}C is within {Margin:0.#}C of {max:0.#}C, holding {current} thread(s)";
            return current;
        }

        _coolSince ??= now;
        var cooled = now - _coolSince.Value;
        if (cooled < StepUpAfter)
        {
            Reason = $"{now_c:0.#}C is cool, {(StepUpAfter - cooled).TotalSeconds:0}s before trying {current + 1} thread(s)";
            return current;
        }

        _coolSince = null;
        _changedAt = now;
        Reason = $"{now_c:0.#}C has been under {max - Margin:0.#}C for {StepUpAfter.TotalMinutes:0} minutes, trying {current + 1} thread(s)";
        return current + 1;
    }
}
