using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Notifications;

namespace KidsLocker.Service.Setup;

/// <summary>
/// Ставит и удаляет KidsLocker целиком из кода (без внешних скриптов), чтобы пульт мог делать
/// всё кнопками. Копирует этот exe в Program Files, регистрирует службу и дублирующие задачи
/// планировщика от SYSTEM, задаёт ACL на папку и регистрирует источник журнала событий.
/// Все шаги требуют, чтобы текущий процесс был запущен с повышением прав (пульт объявляет
/// requireAdministrator).
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

    /// <summary>Полная установка. <paramref name="log"/> получает строки прогресса для пульта.</summary>
    public static void Install(bool dryRun, Action<string>? log = null)
    {
        void L(string m) => log?.Invoke(m);
        if (!IsElevated()) throw new InvalidOperationException("Требуются права администратора.");

        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к exe.");

        // Освобождаем установленный exe ПЕРЕД записью поверх него: сначала убираем задачи (чтобы
        // хранитель не воскресил службу посреди установки), затем останавливаем и удаляем службу.
        RemoveTasks();
        StopAndDeleteService(L);

        // 1) Копируем этот exe в Program Files (пропускаем, если уже запущены оттуда).
        Directory.CreateDirectory(InstallDir);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            CopyWithRetry(source, InstalledExe);
            L($"Скопирован exe → {InstalledExe}");
        }

        // 2) Папка данных + ACL.
        AppPaths.EnsureRoot();
        LockDownDataFolder(L);

        // 3) Источник журнала событий.
        try { WindowsEventLog.EnsureSource(); L("Источник журнала зарегистрирован."); }
        catch (Exception ex) { L("Журнал (не критично): " + ex.Message); }

        // 4) Служба.
        CreateService(dryRun, L);
        RegisterSafeBoot(L);

        // 5) Задачи планировщика.
        CreateTasks(dryRun, L);

        // 6) Запуск.
        var (startCode, startOut) = RunProcess("sc.exe", $"start {ServiceName}");
        if (startCode == 0)
            L("Служба запущена.");
        else
            L($"⚠ Служба создана, но не запустилась (sc.exe start, код {startCode}): {startOut}");
        L(dryRun ? "Режим ТЕСТ: реального выключения не будет." : "Режим БОЕВОЙ: выключение включено.");
    }

    public static void Uninstall(Action<string>? log = null)
    {
        void L(string m) => log?.Invoke(m);
        if (!IsElevated()) throw new InvalidOperationException("Требуются права администратора.");

        // Сначала останавливаем службу, чтобы она не самовосстановилась (пересоздала задачи/ключи), пока мы их убираем.
        RemoveTasks();
        StopAndDeleteService(L);
        RemoveTasks();          // ещё раз — вдруг служба пересоздала задачу в окне остановки
        UnregisterSafeBoot();
        L("Служба и задачи удалены.");

        try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, recursive: true); } catch { }
        try { if (Directory.Exists(AppPaths.RootDir)) Directory.Delete(AppPaths.RootDir, recursive: true); } catch { }
        L("Данные удалены.");
    }

    private static void CreateService(bool dryRun, Action<string> L)
    {
        if (ServiceControl.GetState() != ServiceControl.State.Missing)
            RunProcess("sc.exe", $"delete {ServiceName}");

        // Запоминаем режим, чтобы хранитель пересоздал удалённую службу так же (не всегда боевой).
        try { File.WriteAllText(AppPaths.ServiceModeFile, dryRun ? "dry" : "live"); } catch { /* по возможности */ }

        var (createCode, createOut) = RunProcess("sc.exe", $"create {ServiceName} binPath= {ServiceControl.BuildBinPath(InstalledExe, dryRun)} start= auto obj= LocalSystem DisplayName= KidsLocker");
        if (createCode != 0)
            throw new InvalidOperationException($"Не удалось создать службу (sc.exe create, код {createCode}): {createOut}");

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
    /// Заставить службу запускаться и в безопасном режиме (Safe Mode), прописав её под ключами
    /// управления SafeBoot. Без этого сторонние службы в Safe Mode не работают — типичный обход.
    /// </summary>
    private static void RegisterSafeBoot(Action<string> L)
    {
        SafeBootRegistration.Register();
        L("Регистрация службы в обоих вариантах безопасного режима проверена.");
    }

    private static void UnregisterSafeBoot()
    {
        foreach (var set in new[] { "Minimal", "Network" })
            RunProcess("reg.exe",
                $"delete \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\SafeBoot\\{set}\\{ServiceName}\" /f");
    }

    /// <summary>
    /// Самовосстановление: периодически вызывается работающей службой (от SYSTEM). Пересоздаёт
    /// задачи планировщика и запись Safe Mode, если их удалили. Возвращает описание того, что
    /// восстановлено (пустая строка, если ничего), чтобы вызывающий код мог оповестить родителя.
    /// Саму службу поддерживают живой задача-хранитель и восстановление SCM.
    /// </summary>
    public static string EnsureHardening(bool dryRun)
    {
        var restored = new List<string>();

        // SafeBoot проверяем первым: планировщик может быть недоступен в безопасном режиме.
        if (!SafeBootExists("Minimal") || !SafeBootExists("Network"))
        {
            RegisterSafeBoot(_ => { });
            restored.Add("безопасный режим");
        }

        if (!SystemBootMode.IsSafeMode && (!TaskExists(GuardianTask) || !TaskExists(EnforceTask)))
        {
            CreateTasks(dryRun, _ => { });
            restored.Add("задачи планировщика");
        }

        return string.Join(", ", restored);
    }

    private static bool TaskExists(string name) =>
        RunProcess("schtasks.exe", $"/query /tn \"{name}\"").code == 0;

    private static bool SafeBootExists(string set) =>
        string.Equals(SafeBootRegistration.Read(set), "Service", StringComparison.OrdinalIgnoreCase);

    /// <summary>Остановить и удалить службу, дождавшись, пока SCM её отпустит, а процесс завершится.</summary>
    private static void StopAndDeleteService(Action<string> L)
    {
        if (ServiceControl.GetState() == ServiceControl.State.Missing) return;

        StopMarker.WriteExpected(); // наш стоп — не поднимаем алерт «контроль остановлен»
        ServiceControl.TryStop();
        RunProcess("sc.exe", $"delete {ServiceName}");
        for (var i = 0; i < 20 && ServiceControl.GetState() != ServiceControl.State.Missing; i++)
            Thread.Sleep(250);
        L("Старая служба остановлена.");
    }

    /// <summary>
    /// Копировать exe с короткими повторами: сразу после удаления службы её процесс ещё может
    /// на мгновение держать дескриптор файла, из-за чего иначе было бы «занято другим процессом».
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

        var g = RegisterTaskFromXml(GuardianTask, guardianXml);
        var e = RegisterTaskFromXml(EnforceTask, enforceXml);
        if (g.ok && e.ok)
            L("Задачи планировщика созданы (guardian + enforce).");
        else
            L($"⚠ Задачи планировщика созданы не полностью — guardian: {(g.ok ? "ок" : "ошибка")}, enforce: {(e.ok ? "ок" : "ошибка")}. {g.output} {e.output}".Trim());
    }

    private static (bool ok, string output) RegisterTaskFromXml(string name, string xml)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kl-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(tmp, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            var (code, output) = RunProcess("schtasks.exe", $"/create /tn \"{name}\" /xml \"{tmp}\" /f");
            return (code == 0, code == 0 ? "" : output);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static void LockDownDataFolder(Action<string> L)
    {
        var dir = AppPaths.RootDir;
        // SYSTEM + администраторы — полный доступ; пользователи — чтение+выполнение; наследование разорвано.
        var (code, output) = RunProcess("icacls.exe", $"\"{dir}\" /inheritance:r /grant:r \"*S-1-5-18:(OI)(CI)F\" \"*S-1-5-32-544:(OI)(CI)F\" \"*S-1-5-32-545:(OI)(CI)RX\"");
        if (code == 0)
            L("ACL на папку данных установлены.");
        else
            L($"⚠ ACL на папку данных не установлены (icacls.exe, код {code}): {output}");
    }

    // --- Сборка XML задачи ---

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
        // Пять минут на проверку времени плюс запас на IPC, уведомление и выключение.
        sb.Append("<ExecutionTimeLimit>PT6M</ExecutionTimeLimit>");
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

    // --- Запуск процессов ---

    private static (int code, string output) RunProcess(string file, string arguments) =>
        ProcessRunner.Run(file, arguments);
}
