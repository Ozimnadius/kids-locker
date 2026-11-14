using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Notifications;

namespace KidsLocker.Service.Setup;

internal static class ExecutableRecovery
{
    public const string TaskName = "KidsLocker-Recovery";
    public static RecoveryStore Store => new(Installer.InstalledExe, Path.Combine(AppPaths.RootDir, "Recovery"));

    public static void Run()
    {
        RunOnce(Store, EnsureService, () => InstallationGate.TryEnter(TimeSpan.Zero),
            () => OperationalLog.Write("Основной exe восстановлен из резервной копии; запуск службы проверен."),
            ex => OperationalLog.Write("Ошибка восстановления exe: " + ex.Message));
        GitHubRecovery.TryStart();
    }

    internal static bool RunOnce(RecoveryStore store, Action ensureService, Func<InstallationGate?> enter,
        Action? onRestored = null, Action<Exception>? onError = null)
    {
        using var gate = enter();
        if (gate is null) return false;
        try
        {
            var restored = store.Recover(ensureService);
            // Журнал тоже под блокировкой: после удаления он не должен заново создавать папку данных.
            if (restored) onRestored?.Invoke();
            return restored;
        }
        catch (Exception ex) when (onError is not null) { onError(ex); return false; }
    }

    internal static void EnsureService()
    {
        // Основная служба могла остаться в SCM, но потерять разрешение SafeBoot.
        SafeBootRegistration.Register();
        var state = ServiceControl.GetState();
        var ok = state switch
        {
            ServiceControl.State.Running => true,
            ServiceControl.State.Missing => ServiceControl.TryRecreate(),
            ServiceControl.State.Stopped => ServiceControl.TryStart(),
            _ => false
        };
        if (!ok) throw new IOException("Основной файл существует, но служба пока не запустилась. Проверка будет повторена.");
    }
}
