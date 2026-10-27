namespace KidsLocker.Service.Scheduling;

/// <summary>
/// Расписание разрешённого времени. Всё, что ВНЕ разрешённых интервалов текущего дня, —
/// запрещённое время (время сна), в которое ПК выключается.
/// </summary>
public sealed class Schedule
{
    /// <summary>Главный выключатель. Если false, контроль полностью выключен.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Разрешённые интервалы по дням недели. Ключи — "sun","mon","tue","wed","thu","fri","sat"
    /// (в порядке <see cref="DayOfWeek"/>). Отсутствующий или пустой день означает запрет на весь день.
    /// </summary>
    public Dictionary<string, List<Interval>> Allowed { get; set; } = new();

    /// <summary>Разовые дополнительные разрешённые интервалы на конкретную дату (например, «+1 час сегодня вечером»).</summary>
    public List<DatedInterval> Extras { get; set; } = new();

    /// <summary>Если задано и сейчас &lt; этого локального времени, контроль на паузе (всё разрешено).</summary>
    public DateTime? PauseUntil { get; set; }

    /// <summary>Последний замеченный id локального часового пояса; нужен, чтобы заметить подмену часового пояса.</summary>
    public string? LastTimeZoneId { get; set; }

    public static readonly string[] DayKeys = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

    public static string KeyFor(DayOfWeek d) => DayKeys[(int)d];

    /// <summary>Разумное начальное расписание: 07:00–23:00 перед учебными днями, в выходные — дольше.</summary>
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

/// <summary>Интервал внутри дня [From, To). Время в формате "HH:mm"; "24:00" означает конец дня.</summary>
public sealed class Interval
{
    public string From { get; set; } = "00:00";
    public string To { get; set; } = "00:00";

    public Interval() { }
    public Interval(string from, string to) { From = from; To = to; }
}

/// <summary>Дополнительный разрешённый интервал, привязанный к одной календарной дате ("yyyy-MM-dd").</summary>
public sealed class DatedInterval
{
    public string Date { get; set; } = "";
    public string From { get; set; } = "00:00";
    public string To { get; set; } = "00:00";

    public DatedInterval() { }
    public DatedInterval(string date, string from, string to) { Date = date; From = from; To = to; }
}
