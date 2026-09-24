namespace KidsLocker.Service.Scheduling;

/// <summary>
/// The allowed-time schedule. Anything OUTSIDE the allowed intervals for the current
/// day is a blocked (bedtime) window during which the PC is shut down.
/// </summary>
public sealed class Schedule
{
    /// <summary>Master switch. When false, enforcement is off entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Allowed intervals per weekday. Keys are "sun","mon","tue","wed","thu","fri","sat"
    /// (matching <see cref="DayOfWeek"/> order). A missing/empty day means blocked all day.
    /// </summary>
    public Dictionary<string, List<Interval>> Allowed { get; set; } = new();

    /// <summary>One-off extra allowed intervals for a specific date (e.g. "+1 hour tonight").</summary>
    public List<DatedInterval> Extras { get; set; } = new();

    /// <summary>When set and now &lt; this local time, enforcement is paused (all allowed).</summary>
    public DateTime? PauseUntil { get; set; }

    /// <summary>Last seen local time-zone id; used to detect tampering with the clock zone.</summary>
    public string? LastTimeZoneId { get; set; }

    public static readonly string[] DayKeys = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

    public static string KeyFor(DayOfWeek d) => DayKeys[(int)d];

    /// <summary>A sensible starting schedule: allowed 07:00–23:00 on school nights, later on weekends.</summary>
    public static Schedule Default()
    {
        var s = new Schedule();
        foreach (var d in new[] { "mon", "tue", "wed", "thu" })
            s.Allowed[d] = new List<Interval> { new("07:00", "23:00") };
        s.Allowed["fri"] = new List<Interval> { new("07:00", "24:00") };
        s.Allowed["sat"] = new List<Interval> { new("09:00", "24:00") };
        s.Allowed["sun"] = new List<Interval> { new("09:00", "23:00") };
        return s;
    }
}

/// <summary>A within-day interval [From, To). Times are "HH:mm"; "24:00" means end of day.</summary>
public sealed class Interval
{
    public string From { get; set; } = "00:00";
    public string To { get; set; } = "00:00";

    public Interval() { }
    public Interval(string from, string to) { From = from; To = to; }
}

/// <summary>An extra allowed interval scoped to a single calendar date ("yyyy-MM-dd").</summary>
public sealed class DatedInterval
{
    public string Date { get; set; } = "";
    public string From { get; set; } = "00:00";
    public string To { get; set; } = "00:00";

    public DatedInterval() { }
    public DatedInterval(string date, string from, string to) { Date = date; From = from; To = to; }
}
