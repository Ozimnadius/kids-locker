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

        using var gate = InstallationGate.Enter();
        ValidateRecoveryDirectories();
        ExecutableRecovery.Store.Disable();
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к exe.");

        // Освобождаем установленный exe ПЕРЕД записью поверх него: сначала убираем задачи (чтобы
        // хранитель не воскресил службу посреди установки), затем останавливаем и удаляем службу.
        RemoveTasks();
        StopAndDeleteService(L);
        RecoveryServiceRegistration.Stop(delete: false);

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
        RecoveryProtection.SecureDirectory(Path.GetDirectoryName(ExecutableRecovery.Store.BackupPath)!);

        // 3) Источник журнала событий.
        try { WindowsEventLog.EnsureSource(); L("Источник журнала зарегистрирован."); }
        catch (Exception ex) { L("Журнал (не критично): " + ex.Message); }

        // 4) Служба.
        CreateService(dryRun, L);
        RegisterSafeBoot(L);
        ExecutableRecovery.Store.Publish();
        GitHubRecovery.Publish();
        RecoveryServiceRegistration.Configure();
        L("Новая резервная копия exe сохранена и проверена.");

        // 5) Задачи планировщика.
        CreateTasks(dryRun, L);

        // 6) Запуск.
        RecoveryServiceRegistration.EnsureRunning();
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

        using var gate = InstallationGate.Enter();
        ValidateRecoveryDirectories();
        // Даже уже запущенная задача после освобождения блокировки не воскресит удалённую программу.
        ExecutableRecovery.Store.Disable();

        // Сначала останавливаем службу, чтобы она не самовосстановилась (пересоздала задачи/ключи), пока мы их убираем.
        RemoveTasks();
        StopAndDeleteService(L);
        RecoveryServiceRegistration.Stop(delete: true);
        RemoveTasks();          // ещё раз — вдруг служба пересоздала задачу в окне остановки
        UnregisterSafeBoot();
        L("Служба и задачи удалены.");

        try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, recursive: true); } catch { }
        try { if (Directory.Exists(AppPaths.RootDir)) Directory.Delete(AppPaths.RootDir, recursive: true); } catch { }
        L("Данные удалены.");
    }

    internal static void RepairFromGitHub(string expectedHash)
    {
        if (!IsElevated()) throw new InvalidOperationException("Для восстановления требуются права администратора.");
        using var gate = InstallationGate.Enter();
        var exe = Environment.ProcessPath ?? throw new IOException("Не найден путь скачанного exe.");
        if (!GitHubRecovery.MayRepair(ExecutableRecovery.Store, exe, expectedHash)) return;
        // Основной exe может быть открыт пультом. Проверенную существующую копию не заменяем,
        // основную службу не удаляем и манифест не отключаем даже при неудачном ремонте.
        GitHubRecovery.Repair(ExecutableRecovery.Store, exe, ExecutableRecovery.EnsureService,
            () => RecoveryServiceRegistration.Stop(delete: false), () =>
            {
                GitHubRecovery.EnsureFiles();
                RecoveryServiceRegistration.Configure();
                RecoveryServiceRegistration.EnsureRunning();
                CreateTasks(ServiceControl.InstalledDryRun(), _ => { });
            });
        OperationalLog.Write("Программа восстановлена проверенным установщиком; настройки и режим сохранены.");
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
        RunProcess("schtasks.exe", $"/delete /tn \"{GitHubRecovery.TaskName}\" /f");
        RunProcess("schtasks.exe", $"/delete /tn \"{ExecutableRecovery.TaskName}\" /f");
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
        foreach (var service in new[] { ServiceName, RecoveryServiceRegistration.Name })
            RunProcess("reg.exe",
                $"delete \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\SafeBoot\\{set}\\{service}\" /f");
    }

    /// <summary>
    /// Самовосстановление: периодически вызывается работающей службой (от SYSTEM). Пересоздаёт
    /// задачи планировщика и запись Safe Mode, если их удалили. Возвращает описание того, что
    /// восстановлено (пустая строка, если ничего), чтобы вызывающий код мог оповестить родителя.
    /// Саму службу поддерживают живой задача-хранитель и восстановление SCM.
    /// </summary>
    public static string EnsureHardening(bool dryRun)
    {
        using var gate = InstallationGate.TryEnter(TimeSpan.Zero);
        if (gate is null || !ExecutableRecovery.Store.Enabled) return "";
        var restored = new List<string>();

        TryRepairComponent("резервная копия exe", () => ExecutableRecovery.Store.EnsureBackup(), restored,
            message => OperationalLog.Write(message));
        TryRepairComponent("загрузчик GitHub", GitHubRecovery.EnsureFiles, restored,
            message => OperationalLog.Write(message));

        // SafeBoot проверяем первым: планировщик может быть недоступен в безопасном режиме.
        if (!SafeBootExists("Minimal") || !SafeBootExists("Network"))
        {
            RegisterSafeBoot(_ => { });
            restored.Add("безопасный режим");
        }
        TryRepairComponent("резервная служба", () =>
        {
            SafeBootRegistration.Register(RecoveryServiceRegistration.Name);
            return RecoveryServiceRegistration.EnsureRunning();
        }, restored, message => OperationalLog.Write(message));

        if (!SystemBootMode.IsSafeMode && (!TaskExists(GuardianTask) || !TaskExists(EnforceTask)
            || (ExecutableRecovery.Store.Enabled && (!TaskExists(ExecutableRecovery.TaskName) || !TaskExists(GitHubRecovery.TaskName)))))
        {
            CreateTasks(dryRun, _ => { });
            restored.Add("задачи планировщика");
        }

        return string.Join(", ", restored);
    }

    internal static void TryRepairComponent(string component, Func<bool> repair, List<string> restored, Action<string> report)
    {
        try { if (repair()) restored.Add(component); }
        catch (Exception ex) { report($"Ошибка восстановления ({component}): " + ex.Message); }
        // Ошибка копии не должна отменять последующие проверки SafeBoot и задач.
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
        if (ExecutableRecovery.Store.Enabled)
        {
            var recovery = RegisterTaskFromXml(ExecutableRecovery.TaskName, RecoveryTaskXml());
            if (!recovery.ok) throw new InvalidOperationException("Не удалось создать задачу восстановления exe: " + recovery.output);
            var download = RegisterTaskFromXml(GitHubRecovery.TaskName, GitHubTaskXml());
            if (!download.ok) throw new InvalidOperationException("Не удалось создать задачу загрузки с GitHub: " + download.output);
            L("Задача восстановления exe создана: при загрузке и каждую минуту.");
            L("Независимый загрузчик GitHub установлен.");
        }
        if (g.ok && e.ok)
            L("Задачи планировщика созданы (guardian + enforce).");
        else
            L($"⚠ Задачи планировщика созданы не полностью — guardian: {(g.ok ? "ок" : "ошибка")}, enforce: {(e.ok ? "ок" : "ошибка")}. {g.output} {e.output}".Trim());
    }

    private static string RecoveryTaskXml() => TaskXml(ExecutableRecovery.Store.BackupPath, "--recover",
        new[] { BootTrigger(), TimeTriggerRepeating("PT1M") });

    private static string GitHubTaskXml() => TaskXml(GitHubRecovery.PowerShellPath, GitHubRecovery.Arguments,
        new[] { BootTrigger(), TimeTriggerRepeating("PT1M") });

    private static void ValidateRecoveryDirectories()
    {
        RecoveryProtection.RejectRedirect(AppPaths.RootDir);
        RecoveryProtection.RejectRedirect(Path.GetDirectoryName(ExecutableRecovery.Store.BackupPath)!);
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
        RecoveryProtection.SecureDirectory(AppPaths.RootDir);
        L("Владелец и права папки данных проверены.");
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
