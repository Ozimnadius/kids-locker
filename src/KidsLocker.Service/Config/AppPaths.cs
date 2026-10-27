namespace KidsLocker.Service.Config;

/// <summary>
/// Well-known filesystem locations. Everything lives under %ProgramData%\KidsLocker,
/// which the installer ACL-locks so that only SYSTEM and Administrators may write.
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
    public static string StateFile => Path.Combine(RootDir, "state.json");
    public static string LogDir => Path.Combine(RootDir, "logs");

    /// <summary>Marker the app writes right before it stops the service on purpose, so the
    /// service's stop-alert can tell an app-initiated restart from someone killing it.</summary>
    public static string ExpectedStopFile => Path.Combine(RootDir, ".expected_stop");

    /// <summary>Records whether the service was installed in dry-run or live mode, so the
    /// guardian can re-create a deleted service in the SAME mode instead of defaulting to live.</summary>
    public static string ServiceModeFile => Path.Combine(RootDir, ".service_mode");

    /// <summary>Last processed Telegram update id + 1, persisted so a restart doesn't replay commands
    /// (re-run a /shutdown, double a /extra) that Telegram still holds as unconfirmed.</summary>
    public static string TelegramOffsetFile => Path.Combine(RootDir, ".tg_offset");

    public static void EnsureRoot()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(LogDir);
    }
}
