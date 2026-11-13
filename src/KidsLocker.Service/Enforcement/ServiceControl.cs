using System.Runtime.Versioning;
using System.ServiceProcess;
using KidsLocker.Service.Config;

namespace KidsLocker.Service.Enforcement;

/// <summary>
/// Помощники для проверки и починки службы Windows, используются режимом самовосстановления
/// <c>--guard</c>. Пересоздание удалённой службы идёт через sc.exe (API управления службами для
/// создания неудобно вызывать через P/Invoke, а sc.exe есть всегда).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceControl
{
    public const string ServiceName = "KidsLockerSvc";

    public enum State { Missing, Stopped, Running, Other }

    public static State GetState()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => State.Running,
                ServiceControllerStatus.Stopped => State.Stopped,
                ServiceControllerStatus.StartPending => State.Running,
                _ => State.Other,
            };
        }
        catch (InvalidOperationException)
        {
            return State.Missing; // служба не установлена
        }
        catch
        {
            return State.Other;
        }
    }

    public static bool TryStart()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryStop()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Применить правку файла конфига/расписания так, чтобы работающая служба приняла её без
    /// срабатывания детектора подмены: стоп, запись, старт. Если служба не запущена — просто запись.
    /// </summary>
    public static void ApplyWithReload(Action write)
    {
        var running = GetState() == State.Running;
        if (running)
        {
            StopMarker.WriteExpected(); // это наш стоп — подавляем алерт «контроль остановлен»
            TryStop();
        }
        write();
        if (running) TryStart();
    }

    /// <summary>
    /// Собрать значение <c>binPath=</c> в кавычках для sc.exe. Путь к exe содержит пробелы
    /// (Program Files), поэтому его нужно обернуть экранированными внутренними кавычками внутри
    /// внешних: <c>"\"C:\…\KidsLocker.exe\" --dry-run"</c>. Одна пара кавычек была бы разбита
    /// разборщиком командной строки (на "C:\Program" и "Files\…"), оставив службе битый путь.
    /// </summary>
    public static string BuildBinPath(string exe, bool dryRun) =>
        dryRun ? $"\"\\\"{exe}\\\" --dry-run\"" : $"\"\\\"{exe}\\\"\"";

    /// <summary>Пересоздать службу, указывающую на этот же exe, с автозапуском и авто-восстановлением.
    /// Воссоздаёт её в ИСХОДНОМ режиме установки (тест или бой), записанном установщиком, чтобы
    /// тестовую установку, воскрешённую хранителем, не начало молча выключать ПК по-настоящему.</summary>
    public static bool TryRecreate()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        var dryRun = InstalledDryRun();
        var okCreate = RunSc($"create {ServiceName} binPath= {BuildBinPath(exe, dryRun)} start= auto obj= LocalSystem DisplayName= KidsLocker");
        RunSc($"description {ServiceName} \"KidsLocker parental time control\"");
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
        RunSc($"failureflag {ServiceName} 1");
        try { Setup.SafeBootRegistration.Register(); }
        catch { return false; }
        var okStart = RunSc($"start {ServiceName}");
        return okCreate || okStart;
    }

    /// <summary>
    /// Прочитать режим установки, записанный установщиком. Если маркер отсутствует или не читается,
    /// считаем режимом по умолчанию тест (dry-run): автоматическое пересоздание службы, режим которой
    /// мы не можем подтвердить, НЕ должно само начать выключать ПК (хранитель к тому же шлёт родителю
    /// алерт о пересоздании службы, так что настоящую боевую установку можно применить заново осознанно).
    /// Метод публичный: тем же маркером пользуется кнопка «Выключить сейчас» в пульте, чтобы в тесте не гасить ПК.
    /// </summary>
    public static bool InstalledDryRun()
    {
        try
        {
            if (File.Exists(AppPaths.ServiceModeFile))
                return !string.Equals(File.ReadAllText(AppPaths.ServiceModeFile).Trim(), "live", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* ушли к безопасному значению по умолчанию */ }
        return true;
    }

    private static bool RunSc(string arguments) => ProcessRunner.Run("sc.exe", arguments, 20000).code == 0;
}
