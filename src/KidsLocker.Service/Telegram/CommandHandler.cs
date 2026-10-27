using System.Globalization;
using System.Text;
using System.Runtime.Versioning;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Scheduling;

namespace KidsLocker.Service.Telegram;

/// <summary>
/// Разбирает и выполняет команды родителя из Telegram над расписанием. Почти без побочных
/// эффектов: единственные из них — запись расписания и (для /shutdown) Enforcer. Возвращает
/// текст ответа. Не занимается вводом-выводом Telegram, чтобы его можно было тестировать напрямую.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CommandHandler
{
    private readonly ScheduleStore _store;
    private readonly Enforcer _enforcer;
    private readonly Func<DateTime> _now;

    public CommandHandler(ScheduleStore store, Enforcer enforcer, Func<DateTime>? nowProvider = null)
    {
        _store = store;
        _enforcer = enforcer;
        _now = nowProvider ?? (() => DateTime.Now);
    }

    public string Handle(string rawText)
    {
        var text = (rawText ?? "").Trim();
        if (text.Length == 0) return Help();

        var parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].TrimStart('/').ToLowerInvariant();
        var args = parts.Skip(1).ToArray();

        try
        {
            return cmd switch
            {
                "status" or "статус" => Status(),
                "schedule" or "расписание" or "sched" => ShowSchedule(),
                "allow" or "разрешить" => SetAllowed(args),
                "extra" or "плюс" or "add" => Extra(args),
                "pause" or "пауза" => Pause(args),
                "resume" or "продолжить" or "unpause" => Resume(),
                "on" or "вкл" or "enable" => SetEnabled(true),
                "off" or "выкл" or "disable" => SetEnabled(false),
                "shutdown" or "выключить" or "off_now" => ShutdownNow(),
                "help" or "помощь" or "start" => Help(),
                _ => $"Не понял команду «{cmd}».\n\n" + Help(),
            };
        }
        catch (Exception ex)
        {
            return "Ошибка: " + ex.Message;
        }
    }

    private string Status()
    {
        var s = _store.Load();
        var now = _now();
        var eval = ScheduleEvaluator.Evaluate(s, now);
        var next = ScheduleEvaluator.NextChange(s, now);

        var sb = new StringBuilder();
        sb.AppendLine($"Сейчас: {(eval.Blocked ? "🔴 ЗАПРЕЩЕНО" : "🟢 разрешено")}  ({now:HH:mm})");
        sb.AppendLine($"Контроль: {(s.Enabled ? "включён" : "ВЫКЛЮЧЕН")}");
        if (s.PauseUntil is DateTime pu && pu > now)
            sb.AppendLine($"Пауза до: {pu:yyyy-MM-dd HH:mm}");
        if (next is DateTime nc)
            sb.AppendLine($"Следующая граница: {nc:ddd HH:mm}");
        sb.AppendLine();
        sb.Append(TodayLine(s, now));
        return sb.ToString().TrimEnd();
    }

    private static string TodayLine(Schedule s, DateTime now)
    {
        var ivs = ScheduleEvaluator.AllowedFor(s, now)
            .OrderBy(r => r.From)
            .Select(r => $"{TimeParsing.Format(r.From)}–{TimeParsing.Format(r.To)}")
            .ToList();
        return "Сегодня разрешено: " + (ivs.Count == 0 ? "— (весь день запрещён)" : string.Join(", ", ivs));
    }

    private string ShowSchedule()
    {
        var s = _store.Load();
        var sb = new StringBuilder();
        sb.AppendLine("Недельное расписание (разрешённое время):");
        string[] ru = { "Вс", "Пн", "Вт", "Ср", "Чт", "Пт", "Сб" };
        for (var i = 1; i <= 7; i++)
        {
            var idx = i % 7; // Пн..Вс, Вс последним
            var key = Schedule.DayKeys[idx];
            var ivs = s.Allowed.TryGetValue(key, out var list) && list is not null
                ? string.Join(", ", list.Select(iv => $"{iv.From}–{iv.To}"))
                : "";
            sb.AppendLine($"{ru[idx]}: {(string.IsNullOrEmpty(ivs) ? "— запрещено весь день" : ivs)}");
        }
        if (s.Extras.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Разовые добавки:");
            foreach (var e in s.Extras.OrderBy(e => e.Date))
                sb.AppendLine($"{e.Date}: {e.From}–{e.To}");
        }
        return sb.ToString().TrimEnd();
    }

    private string SetAllowed(string[] args)
    {
        if (args.Length < 2)
            return "Формат: /allow <день> <ЧЧ:ММ-ЧЧ:ММ[,ЧЧ:ММ-ЧЧ:ММ]>\nНапр.: /allow будни 07:00-23:00";

        var days = ResolveDays(args[0]);
        if (days.Count == 0) return $"Не понял день «{args[0]}». Дни: пн вт ср чт пт сб вс, будни, выходные, все.";

        // Всё, что после дня, — это описание интервалов. Команда делится по пробелам, поэтому описание
        // вида "07:00-12:00, 14:00-23:00" (именно так его печатают /schedule и пульт) приходит
        // несколькими аргументами — склеиваем их обратно, иначе второй интервал молча потеряется.
        var spec = string.Join(" ", args.Skip(1));
        var clear = spec.Equals("off", StringComparison.OrdinalIgnoreCase)
                    || spec.Equals("нет", StringComparison.OrdinalIgnoreCase)
                    || spec.Equals("-", StringComparison.Ordinal);
        var intervals = ScheduleText.ParseIntervals(spec);
        if (!clear && intervals.Count == 0)
            return "Не разобрал интервалы. Пример: 07:00-23:00 или 07:00-12:00,14:00-23:00";

        _store.Update(s =>
        {
            foreach (var d in days)
                s.Allowed[d] = clear ? new List<Interval>() : intervals;
        });
        return "Готово.\n\n" + ShowSchedule();
    }

    private string Extra(string[] args)
    {
        if (args.Length < 1 || !TryParseMinutes(args[0], bareIsHours: false, out var minutes) || minutes <= 0)
            return "Формат: /extra <минуты>. Напр.: /extra 60 — добавить час сегодня.";

        var now = _now();
        var extra = ScheduleEvaluator.BuildExtra(_store.Load(), now, minutes);
        if (extra is null)
            return "Сегодня уже разрешено до конца суток — добавлять нечего.";

        _store.Update(sc => sc.Extras.Add(extra));
        return $"Добавил сегодня {extra.From}–{extra.To}.";
    }

    private string Pause(string[] args)
    {
        if (args.Length < 1 || !TryParseMinutes(args[0], bareIsHours: true, out var minutes) || minutes <= 0)
            return "Формат: /pause <длительность>. Напр.: /pause 2h, /pause 90m, /pause 2.";

        var until = _now().AddMinutes(minutes);
        _store.Update(s => s.PauseUntil = until);
        return $"Пауза контроля до {until:yyyy-MM-dd HH:mm}.";
    }

    private string Resume()
    {
        _store.Update(s => s.PauseUntil = null);
        return "Пауза снята, контроль активен.";
    }

    private string SetEnabled(bool on)
    {
        _store.Update(s => s.Enabled = on);
        return on ? "Контроль включён." : "Контроль ВЫКЛЮЧЕН (комп не будет выключаться).";
    }

    private string ShutdownNow()
    {
        _enforcer.ShutdownNow("manual /shutdown from parent");
        return _enforcer.IsLive ? "Выключаю компьютер сейчас." : "[DRY-RUN] Здесь был бы выключен компьютер.";
    }

    private static string Help() =>
        "Команды KidsLocker:\n" +
        "/status — что сейчас\n" +
        "/schedule — недельное расписание\n" +
        "/allow <день> <ЧЧ:ММ-ЧЧ:ММ,...> — задать разрешённое время\n" +
        "   дни: пн вт ср чт пт сб вс, будни, выходные, все\n" +
        "   очистить день: /allow пн off\n" +
        "/extra <минуты> — добавить время сегодня (напр. /extra 60)\n" +
        "/pause <2h|90m> — приостановить контроль\n" +
        "/resume — снять паузу\n" +
        "/off — выключить контроль, /on — включить\n" +
        "/shutdown — выключить компьютер сейчас";

    // --- помощники разбора ---

    private static List<string> ResolveDays(string token)
    {
        var t = token.ToLowerInvariant();
        switch (t)
        {
            case "будни" or "weekdays": return new() { "mon", "tue", "wed", "thu", "fri" };
            case "выходные" or "weekend": return new() { "sat", "sun" };
            case "все" or "all" or "everyday" or "ежедневно":
                return new() { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };
        }
        var map = new Dictionary<string, string>
        {
            ["пн"] = "mon", ["mon"] = "mon",
            ["вт"] = "tue", ["tue"] = "tue",
            ["ср"] = "wed", ["wed"] = "wed",
            ["чт"] = "thu", ["thu"] = "thu",
            ["пт"] = "fri", ["fri"] = "fri",
            ["сб"] = "sat", ["sat"] = "sat",
            ["вс"] = "sun", ["sun"] = "sun",
        };
        return map.TryGetValue(t, out var d) ? new() { d } : new();
    }

    /// <summary>
    /// Разобрать "90m", "2h", "1h30m", "1h30" или голое число (часы или минуты — по
    /// <paramref name="bareIsHours"/>). Цифры после единицы «час» — это минуты, так что "1h30" — это 90.
    /// Защищено от переполнения (сумма копится в long) и ограничено неделей, чтобы огромное число не
    /// вызвало переполнение и не поставило контроль на паузу на годы.
    /// </summary>
    private static bool TryParseMinutes(string token, bool bareIsHours, out int minutes)
    {
        minutes = 0;
        var t = token.Trim().ToLowerInvariant();
        if (t.Length == 0) return false;

        long total = 0;
        bool any = false, sawHour = false;
        var num = new StringBuilder();

        foreach (var ch in t)
        {
            if (char.IsDigit(ch)) { num.Append(ch); continue; }
            if (num.Length == 0) return false;
            if (!long.TryParse(num.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return false;
            num.Clear();
            if (ch is 'h' or 'ч') { total += n * 60; sawHour = true; any = true; }
            else if (ch is 'm' or 'м') { total += n; any = true; }
            else return false;
        }

        if (num.Length > 0)
        {
            if (!long.TryParse(num.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var tail)) return false;
            // Число в конце после явно указанных часов — это минуты ("1h30"); иначе действует правило
            // для голого числа (часы для /pause, минуты для /extra).
            total += sawHour ? tail : (bareIsHours ? tail * 60 : tail);
            any = true;
        }

        if (!any) return false;
        total = Math.Clamp(total, 0, 7 * 24 * 60); // не больше недели; защищает от переполнения int и абсурдных значений
        minutes = (int)total;
        return minutes > 0;
    }
}
