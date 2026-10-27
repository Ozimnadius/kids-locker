using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Notifications;

namespace KidsLocker.Service.Setup;

/// <summary>
/// Installs / removes KidsLocker entirely from code (no external scripts), so the GUI can
/// do everything with buttons. Copies this exe into Program Files, registers the service and
/// the redundant SYSTEM scheduled tasks, sets folder ACLs, and registers the Event Log source.
/// All steps require the current process to be elevated (the GUI declares requireAdministrator).
/// </summary>
[SupportedOSPlatform("windows")]
public static class Installer
{
    public const string ServiceName = "KidsLockerSvc";
    public const string GuardianTask = "KidsLocker-Guardian";
    public const string EnforceTask = "KidsLocker-Enforce";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KidsLocker");

    public static string InstalledExe => Path.Combine(InstallDir, "KidsLocker.exe");

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static ServiceControl.State ServiceState() => ServiceControl.GetState();

    public static bool IsInstalled() => ServiceControl.GetState() != ServiceControl.State.Missing;

    /// <summary>Full install. <paramref name="log"/> receives progress lines for the GUI.</summary>
    public static void Install(bool dryRun, Action<string>? log = null)
    {
        void L(string m) => log?.Invoke(m);
        if (!IsElevated()) throw new InvalidOperationException("Требуются права администратора.");

        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к exe.");

        // Free the installed exe BEFORE copying over it: remove the tasks (so the guardian
        // can't revive the service mid-install), then stop and delete the running service.
        RemoveTasks();
        StopAndDeleteService(L);

        // 1) Copy this exe into Program Files (skip if we are already running from there).
        Directory.CreateDirectory(InstallDir);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            CopyWithRetry(source, InstalledExe);
            L($"Скопирован exe → {InstalledExe}");
        }

        // 2) Data folder + ACL.
        AppPaths.EnsureRoot();
        LockDownDataFolder(L);

        // 3) Event Log source.
        try { WindowsEventLog.EnsureSource(); L("Источник журнала зарегистрирован."); }
        catch (Exception ex) { L("Журнал (не критично): " + ex.Message); }

        // 4) Service.
        CreateService(dryRun, L);
        RegisterSafeBoot(L);

        // 5) Scheduled tasks.
        CreateTasks(dryRun, L);

        // 6) Start.
        RunProcess("sc.exe", $"start {ServiceName}");
        L("Служба запущена.");
        L(dryRun ? "Режим ТЕСТ: реального выключения не будет." : "Режим БОЕВОЙ: выключение включено.");
    }

    public static void Uninstall(bool keepData, Action<string>? log = null)
    {
        void L(string m) => log?.Invoke(m);
        if (!IsElevated()) throw new InvalidOperationException("Требуются права администратора.");

        // Stop the service first so it can't self-heal (recreate tasks/keys) while we remove them.
        RemoveTasks();
        StopAndDeleteService(L);
        RemoveTasks();          // again, in case the service recreated a task during the stop window
        UnregisterSafeBoot();
        L("Служба и задачи удалены.");

        try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, recursive: true); } catch { }
        if (!keepData)
        {
            try { if (Directory.Exists(AppPaths.RootDir)) Directory.Delete(AppPaths.RootDir, recursive: true); } catch { }
            L("Данные удалены.");
        }
    }

    private static void CreateService(bool dryRun, Action<string> L)
    {
        if (ServiceControl.GetState() != ServiceControl.State.Missing)
            RunProcess("sc.exe", $"delete {ServiceName}");

        // Remember the mode so the guardian re-creates a deleted service identically (not always live).
        try { File.WriteAllText(AppPaths.ServiceModeFile, dryRun ? "dry" : "live"); } catch { /* best effort */ }

        RunProcess("sc.exe", $"create {ServiceName} binPath= {ServiceControl.BuildBinPath(InstalledExe, dryRun)} start= auto obj= LocalSystem DisplayName= KidsLocker");
        RunProcess("sc.exe", $"description {ServiceName} \"KidsLocker parental time control\"");
        RunProcess("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
        RunProcess("sc.exe", $"failureflag {ServiceName} 1");
        L("Служба создана (автозапуск + авто-восстановление).");
    }

    private static void RemoveTasks()
    {
        RunProcess("schtasks.exe", $"/delete /tn \"{GuardianTask}\" /f");
        RunProcess("schtasks.exe", $"/delete /tn \"{EnforceTask}\" /f");
    }

    /// <summary>
    /// Make the service start in Safe Mode too, by listing it under the SafeBoot control keys.
    /// Without this, third-party services don't run in Safe Mode — a common bypass.
    /// </summary>
    private static void RegisterSafeBoot(Action<string> L)
    {
        foreach (var set in new[] { "Minimal", "Network" })
            RunProcess("reg.exe",
                $"add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\SafeBoot\\{set}\\{ServiceName}\" /ve /t REG_SZ /d Service /f");
        L("Служба включена и в безопасном режиме (Safe Mode).");
    }

    private static void UnregisterSafeBoot()
    {
        foreach (var set in new[] { "Minimal", "Network" })
            RunProcess("reg.exe",
                $"delete \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\SafeBoot\\{set}\\{ServiceName}\" /f");
    }

    /// <summary>
    /// Self-heal: called periodically by the running service (as SYSTEM). Recreates the
    /// scheduled tasks and Safe Mode registration if they were deleted. Returns a description
    /// of what was restored (empty string if nothing), so the caller can alert the parent.
    /// The service itself is kept alive by the guardian task and SCM recovery.
    /// </summary>
    public static string EnsureHardening(bool dryRun)
    {
        var restored = new List<string>();

        if (!TaskExists(GuardianTask) || !TaskExists(EnforceTask))
        {
            CreateTasks(dryRun, _ => { });
            restored.Add("задачи планировщика");
        }

        if (!SafeBootExists("Minimal") || !SafeBootExists("Network"))
        {
            RegisterSafeBoot(_ => { });
            restored.Add("режим Safe Mode");
        }

        return string.Join(", ", restored);
    }

    private static bool TaskExists(string name) =>
        RunProcess("schtasks.exe", $"/query /tn \"{name}\"").code == 0;

    private static bool SafeBootExists(string set) =>
        RunProcess("reg.exe", $"query \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\SafeBoot\\{set}\\{ServiceName}\"").code == 0;

    /// <summary>Stop and remove the service, waiting for the SCM to drop it and the process to exit.</summary>
    private static void StopAndDeleteService(Action<string> L)
    {
        if (ServiceControl.GetState() == ServiceControl.State.Missing) return;

        StopMarker.WriteExpected(); // our stop — don't trigger the "control stopped" alert
        ServiceControl.TryStop();
        RunProcess("sc.exe", $"delete {ServiceName}");
        for (var i = 0; i < 20 && ServiceControl.GetState() != ServiceControl.State.Missing; i++)
            Thread.Sleep(250);
        L("Старая служба остановлена.");
    }

    /// <summary>
    /// Copy the exe, retrying briefly: right after the service is deleted its process may still
    /// hold the file handle for a moment, which would otherwise throw "used by another process".
    /// </summary>
    private static void CopyWithRetry(string source, string dest)
    {
        Exception? last = null;
        for (var i = 0; i < 20; i++)
        {
            try { File.Copy(source, dest, overwrite: true); return; }
            catch (IOException ex) { last = ex; Thread.Sleep(500); }
            catch (UnauthorizedAccessException ex) { last = ex; Thread.Sleep(500); }
        }
        throw last ?? new IOException("Не удалось скопировать exe.");
    }

    private static void CreateTasks(bool dryRun, Action<string> L)
    {
        var enforceArgs = dryRun ? "--enforce-once" : "--enforce-once --live";

        var guardianXml = TaskXml(InstalledExe, "--guard", new[]
        {
            BootTrigger(),
            TimeTriggerRepeating("PT1M"),
        });
        var enforceXml = TaskXml(InstalledExe, enforceArgs, new[]
        {
            LogonTrigger(),
            BootTrigger(),
            TimeTriggerRepeating("PT10M"),
        });

        RegisterTaskFromXml(GuardianTask, guardianXml);
        RegisterTaskFromXml(EnforceTask, enforceXml);
        L("Задачи планировщика созданы (guardian + enforce).");
    }

    private static void RegisterTaskFromXml(string name, string xml)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kl-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(tmp, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            RunProcess("schtasks.exe", $"/create /tn \"{name}\" /xml \"{tmp}\" /f");
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static void LockDownDataFolder(Action<string> L)
    {
        var dir = AppPaths.RootDir;
        // SYSTEM + Administrators full; Users read+execute; break inheritance.
        RunProcess("icacls.exe", $"\"{dir}\" /inheritance:r /grant:r \"*S-1-5-18:(OI)(CI)F\" \"*S-1-5-32-544:(OI)(CI)F\" \"*S-1-5-32-545:(OI)(CI)RX\"");
        L("ACL на папку данных установлены.");
    }

    // --- Task XML building ---

    private static string TaskXml(string command, string arguments, IEnumerable<string> triggers)
    {
        var start = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
        sb.Append("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
        sb.Append("<RegistrationInfo><Description>KidsLocker</Description></RegistrationInfo>");
        sb.Append("<Triggers>");
        foreach (var t in triggers) sb.Append(t.Replace("{START}", start));
        sb.Append("</Triggers>");
        sb.Append("<Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>");
        sb.Append("<Settings>");
        sb.Append("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        sb.Append("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        sb.Append("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        sb.Append("<AllowHardTerminate>true</AllowHardTerminate>");
        sb.Append("<StartWhenAvailable>true</StartWhenAvailable>");
        sb.Append("<Enabled>true</Enabled>");
        sb.Append("<ExecutionTimeLimit>PT5M</ExecutionTimeLimit>");
        sb.Append("</Settings>");
        sb.Append("<Actions Context=\"Author\">");
        sb.Append($"<Exec><Command>{Xml(command)}</Command><Arguments>{Xml(arguments)}</Arguments></Exec>");
        sb.Append("</Actions>");
        sb.Append("</Task>");
        return sb.ToString();
    }

    private static string LogonTrigger() => "<LogonTrigger><Enabled>true</Enabled></LogonTrigger>";
    private static string BootTrigger() => "<BootTrigger><Enabled>true</Enabled></BootTrigger>";

    private static string TimeTriggerRepeating(string interval) =>
        "<TimeTrigger>" +
        "<StartBoundary>{START}</StartBoundary>" +
        "<Enabled>true</Enabled>" +
        $"<Repetition><Interval>{interval}</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>" +
        "</TimeTrigger>";

    private static string Xml(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");

    // --- process helper ---

    public static (int code, string output) RunProcess(string file, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return (p.ExitCode, (so + se).Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
