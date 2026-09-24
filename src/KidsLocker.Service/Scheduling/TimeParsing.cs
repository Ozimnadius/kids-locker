using System.Globalization;

namespace KidsLocker.Service.Scheduling;

/// <summary>
/// Parsing helpers for "HH:mm" interval endpoints. Kept separate and pure so the
/// scheduling logic is fully unit-testable without any Windows dependency.
/// </summary>
public static class TimeParsing
{
    /// <summary>Full day as a TimeSpan; used as the exclusive upper bound "24:00".</summary>
    public static readonly TimeSpan EndOfDay = TimeSpan.FromDays(1);

    /// <summary>
    /// Parse "HH:mm" (or "H:mm") into a TimeSpan offset from midnight.
    /// The special value "24:00" maps to <see cref="EndOfDay"/> so an interval can
    /// run to the very end of the day. Throws <see cref="FormatException"/> on garbage.
    /// </summary>
    public static TimeSpan Parse(string value)
    {
        var v = (value ?? "").Trim();
        if (v is "24:00" or "24.00") return EndOfDay;

        var parts = v.Split(':', '.');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            || h < 0 || h > 24 || m < 0 || m > 59
            || (h == 24 && m != 0))
        {
            throw new FormatException($"Invalid time '{value}', expected HH:mm (00:00–24:00).");
        }
        return new TimeSpan(h, m, 0);
    }

    public static bool TryParse(string value, out TimeSpan result)
    {
        try { result = Parse(value); return true; }
        catch { result = TimeSpan.Zero; return false; }
    }

    public static string Format(TimeSpan t) =>
        t >= EndOfDay ? "24:00" : $"{(int)t.TotalHours:D2}:{t.Minutes:D2}";
}
