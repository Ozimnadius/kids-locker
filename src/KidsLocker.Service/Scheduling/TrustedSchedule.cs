namespace KidsLocker.Service.Scheduling;

/// <summary>Единая оценка расписания для службы, пульта и резервной проверки.</summary>
public static class TrustedSchedule
{
    public static readonly TimeSpan MoscowOffset = TimeSpan.FromHours(3);

    internal static async Task<Evaluation> EvaluateCurrentAsync(
        Func<Schedule> readSchedule, Func<Task<DateTimeOffset?>> readUtc)
    {
        var schedule = readSchedule();
        if (!schedule.Enabled) return Evaluate(schedule, null);
        var utc = await readUtc();
        // За время ожидания родитель мог выключить контроль или изменить расписание.
        // Для разовой задачи нужен новый снимок с диска, а не кеш начала проверки.
        return Evaluate(readSchedule(), utc);
    }

    public static DateTime LocalNow(Schedule schedule, DateTimeOffset? utc)
    {
        if (utc is null) throw new InvalidOperationException("Время ещё не подтверждено через интернет. Контроль запрещает работу.");
        return utc.Value.ToOffset(MoscowOffset).DateTime;
    }

    public static Evaluation Evaluate(Schedule schedule, DateTimeOffset? utc)
    {
        if (!schedule.Enabled) return new(false, "Контроль выключен родителем");
        if (utc is null) return new(true, "Время не подтверждено через интернет");
        var result = ScheduleEvaluator.Evaluate(schedule, LocalNow(schedule, utc));
        return new(result.Blocked, result.Blocked ? "Сейчас вне разрешённых часов (Москва, UTC+3)" : "Сейчас работа разрешена");
    }
}
