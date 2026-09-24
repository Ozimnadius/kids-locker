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

    public static void EnsureRoot()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(LogDir);
    }
}
