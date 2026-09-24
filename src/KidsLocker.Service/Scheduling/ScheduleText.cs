namespace KidsLocker.Service.Scheduling;

/// <summary>Parse/format a day's allowed intervals as text ("07:00-23:00, 14:00-18:00").</summary>
public static class ScheduleText
{
    public static List<Interval> ParseIntervals(string token)
    {
        var result = new List<Interval>();
        foreach (var piece in (token ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var hy = piece.Trim().Split('-', '–');
            if (hy.Length != 2) continue;
            if (!TimeParsing.TryParse(hy[0], out var f) || !TimeParsing.TryParse(hy[1], out var t)) continue;
            if (t <= f) continue;
            result.Add(new Interval(TimeParsing.Format(f), TimeParsing.Format(t)));
        }
        return result;
    }

    public static string Format(IEnumerable<Interval>? intervals) =>
        intervals is null ? "" : string.Join(", ", intervals.Select(i => $"{i.From}-{i.To}"));
}
