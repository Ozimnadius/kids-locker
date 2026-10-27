namespace KidsLocker.Service.Scheduling;

/// <summary>Результат оценки расписания на заданный момент времени.</summary>
public readonly record struct Evaluation(bool Blocked, string Reason);

/// <summary>
/// Чистая логика решения: по <see cref="Schedule"/> и локальному времени решает, должен ли
/// ПК сейчас быть заблокирован (время сна). Без ввода-вывода и вызовов Windows — покрывается модульными тестами.
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

    /// <summary>Удобная обёртка.</summary>
    public static bool IsBlocked(Schedule s, DateTime nowLocal) => Evaluate(s, nowLocal).Blocked;

    /// <summary>
    /// Все разрешённые интервалы, действующие в заданную локальную дату: базовые интервалы дня
    /// недели плюс дополнительные на эту дату. Некорректные записи пропускаются и никогда не приводят к падению.
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
        if (t <= f) return false; // пропускаем нулевые, отрицательные и перевёрнутые интервалы
        range = (f, t);
        return true;
    }

    /// <summary>
    /// Ближайшее локальное время, когда состояние «запрещено/разрешено» сменится; поиск идёт вперёд
    /// от <paramref name="nowLocal"/> не дальше чем на <paramref name="horizonDays"/> дней.
    /// Возвращает null, если в пределах горизонта смены нет (например, всегда разрешено).
    /// Нужно, чтобы запланировать точное пробуждение контроля, а не полагаться только на опрос.
    /// </summary>
    public static DateTime? NextChange(Schedule s, DateTime nowLocal, int horizonDays = 8)
    {
        var current = Evaluate(s, nowLocal).Blocked;

        // Моменты-кандидаты: каждая граница интервала для каждого дня в пределах горизонта плюс
        // граница паузы. Берём первый кандидат, состояние в котором отличается от текущего.
        var candidates = new SortedSet<DateTime>();

        if (s.PauseUntil is DateTime until && until > nowLocal)
            candidates.Add(until);

        for (var d = 0; d <= horizonDays; d++)
        {
            var day = nowLocal.Date.AddDays(d);
            foreach (var (from, to) in AllowedFor(s, day))
            {
                Add(candidates, day + from, nowLocal);
                // `to` — первый запрещённый момент после интервала. Для конца 24:00 это 00:00 следующего
                // дня — настоящая смена «разрешено→запрещено». Если обрезать его до 23:59:59.9 (ещё
                // разрешено), NextChange совсем пропускал бы границу полуночи.
                Add(candidates, day + to, nowLocal);
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

    /// <summary>
    /// Вычислить разовый дополнительный разрешённый интервал на <paramref name="minutes"/> минут на
    /// сегодня: он начинается с более позднего из «сейчас» и конца последнего разрешённого блока за
    /// сегодня и обрезается концом дня. Возвращает null, когда добавить нечего (число минут не больше
    /// нуля или сегодня уже разрешено до самой полуночи), чтобы вызывающий честно сказал об этом
    /// пользователю, а не записал молча пустой интервал 24:00–24:00. Общий для <c>/extra</c> в Telegram и кнопки в пульте.
    /// </summary>
    public static DatedInterval? BuildExtra(Schedule s, DateTime now, int minutes)
    {
        if (minutes <= 0) return null;

        var latestTo = AllowedFor(s, now).Select(r => r.To).DefaultIfEmpty(now.TimeOfDay).Max();
        var start = now.TimeOfDay > latestTo ? now.TimeOfDay : latestTo;
        if (start >= TimeParsing.EndOfDay) return null; // уже разрешено до конца дня

        var end = start + TimeSpan.FromMinutes(minutes);
        if (end > TimeParsing.EndOfDay) end = TimeParsing.EndOfDay;
        if (end <= start) return null;

        return new DatedInterval(now.ToString("yyyy-MM-dd"), TimeParsing.Format(start), TimeParsing.Format(end));
    }
}
