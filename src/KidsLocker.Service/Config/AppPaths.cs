namespace KidsLocker.Service.Config;

/// <summary>
/// Известные пути в файловой системе. Всё лежит в %ProgramData%\KidsLocker; установщик
/// закрывает эту папку ACL так, что писать могут только SYSTEM и администраторы.
/// </summary>
public static class AppPaths
{
    public static string RootDir =>
        Environment.GetEnvironmentVariable("KIDSLOCKER_HOME")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "KidsLocker");

    public static string ConfigFile => Path.Combine(RootDir, "config.local.json");
    public static string ScheduleFile => Path.Combine(RootDir, "schedule.json");

    /// <summary>Маркер, который приложение пишет прямо перед тем, как само остановит службу, —
    /// чтобы stop-алерт службы отличил наш перезапуск от того, что её кто-то убил.</summary>
    public static string ExpectedStopFile => Path.Combine(RootDir, ".expected_stop");

    /// <summary>Записывает, в каком режиме поставили службу (тест или бой), чтобы хранитель
    /// пересоздавал удалённую службу в ТОМ ЖЕ режиме, а не по умолчанию в боевом.</summary>
    public static string ServiceModeFile => Path.Combine(RootDir, ".service_mode");

    /// <summary>Последний обработанный id обновления Telegram + 1, хранится на диске: чтобы перезапуск
    /// не повторял команды (повторный /shutdown, дубль /extra), которые Telegram ещё держит неподтверждёнными.</summary>
    public static string TelegramOffsetFile => Path.Combine(RootDir, ".tg_offset");

    public static void EnsureRoot()
    {
        Directory.CreateDirectory(RootDir);
    }
}
