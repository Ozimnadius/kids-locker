using System.Globalization;

namespace KidsLocker.Service.Scheduling;

/// <summary>
/// Помощники разбора границ интервалов "HH:mm". Вынесены отдельно и без побочных эффектов, чтобы
/// логику расписания можно было полностью покрыть модульными тестами без зависимости от Windows.
/// </summary>
public static class TimeParsing
{
    /// <summary>Целые сутки в виде TimeSpan; используется как невключаемая верхняя граница "24:00".</summary>
    public static readonly TimeSpan EndOfDay = TimeSpan.FromDays(1);

    /// <summary>
    /// Разобрать "HH:mm" (или "H:mm") в TimeSpan — смещение от полуночи.
    /// Особое значение "24:00" превращается в <see cref="EndOfDay"/>, чтобы интервал мог
    /// длиться до самого конца дня. На мусоре бросает <see cref="FormatException"/>.
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
