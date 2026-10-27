namespace KidsLocker.Service.Scheduling;

/// <summary>Result of evaluating the schedule at a moment in time.</summary>
public readonly record struct Evaluation(bool Blocked, string Reason);

/// <summary>
/// Pure decision logic: given a <see cref="Schedule"/> and a local time, decide whether
/// the PC should currently be blocked (bedtime). No I/O, no Windows calls — unit-testable.
/// </summary>
public static class ScheduleEvaluator
{
    public static Evaluation Evaluate(Schedule s, DateTime nowLocal)
    {
        if (!s.Enabled)
            return new Evaluation(false, "schedule disabled");

        if (s.PauseUntil is DateTime until && nowLocal < until)
            return new Evaluation(false, $"paused until {until:yyyy-MM-dd HH:mm}");

        var now = nowLocal.TimeOfDay;

        foreach (var (from, to) in AllowedFor(s, nowLocal))
            if (now >= from && now < to)
                return new Evaluation(false, "within allowed time");

        return new Evaluation(true, "outside allowed time");
    }

    /// <summary>Convenience wrapper.</summary>
    public static bool IsBlocked(Schedule s, DateTime nowLocal) => Evaluate(s, nowLocal).Blocked;

    /// <summary>
    /// All allowed intervals in effect for the given local date: the weekday's base
    /// intervals plus any dated extras. Malformed entries are skipped, never crash.
    /// </summary>
    public static IEnumerable<(TimeSpan From, TimeSpan To)> AllowedFor(Schedule s, DateTime localDate)
    {
        var dayKey = Schedule.KeyFor(localDate.DayOfWeek);
        if (s.Allowed is not null && s.Allowed.TryGetValue(dayKey, out var intervals) && intervals is not null)
        {
            foreach (var iv in intervals)
                if (TryRange(iv.From, iv.To, out var r))
                    yield return r;
        }

        if (s.Extras is not null)
        {
            var dateStr = localDate.ToString("yyyy-MM-dd");
            foreach (var ex in s.Extras)
                if (string.Equals(ex.Date, dateStr, StringComparison.Ordinal)
                    && TryRange(ex.From, ex.To, out var r))
                    yield return r;
        }
    }

    private static bool TryRange(string from, string to, out (TimeSpan From, TimeSpan To) range)
    {
        range = default;
        if (!TimeParsing.TryParse(from, out var f) || !TimeParsing.TryParse(to, out var t))
            return false;
        if (t <= f) return false; // ignore zero/negative/inverted intervals
        range = (f, t);
        return true;
    }

    /// <summary>
    /// The next local time at which the blocked/allowed state flips, searching forward
    /// from <paramref name="nowLocal"/> up to <paramref name="horizonDays"/> ahead.
    /// Returns null if no change is found within the horizon (e.g. always-allowed).
    /// Used to schedule an exact enforcement wake-up instead of relying only on polling.
    /// </summary>
    public static DateTime? NextChange(Schedule s, DateTime nowLocal, int horizonDays = 8)
    {
        var current = Evaluate(s, nowLocal).Blocked;

        // Candidate instants: every interval edge for each day in the horizon, plus the
        // pause boundary. We step to the first candidate whose state differs from now.
        var candidates = new SortedSet<DateTime>();

        if (s.PauseUntil is DateTime until && until > nowLocal)
            candidates.Add(until);

        for (var d = 0; d <= horizonDays; d++)
        {
            var day = nowLocal.Date.AddDays(d);
            foreach (var (from, to) in AllowedFor(s, day))
            {
                Add(candidates, day + from, nowLocal);
                Add(candidates, day + (to >= TimeParsing.EndOfDay ? TimeParsing.EndOfDay - TimeSpan.FromTicks(1) : to), nowLocal);
            }
        }

        foreach (var c in candidates)
            if (Evaluate(s, c).Blocked != current)
                return c;

        return null;
    }

    private static void Add(SortedSet<DateTime> set, DateTime instant, DateTime after)
    {
        if (instant > after) set.Add(instant);
    }
}
