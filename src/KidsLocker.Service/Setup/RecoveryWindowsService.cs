using System.ServiceProcess;
using KidsLocker.Service.Notifications;

namespace KidsLocker.Service.Setup;

/// <summary>Независимая служба из резервного exe; не требует сети, планировщика или общего хоста.</summary>
internal sealed class RecoveryWindowsService : ServiceBase
{
    private RecoveryLoop? _loop;

    public RecoveryWindowsService()
    {
        ServiceName = RecoveryServiceRegistration.Name;
        AutoLog = false; // журнал событий в безопасном режиме может отсутствовать
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        // OnStart быстро возвращается: запуск основной службы из фонового цикла не блокирует SCM.
        _loop = new RecoveryLoop(ExecutableRecovery.Run,
            ex => WindowsEventLog.TryWrite("Ошибка резервной службы KidsLocker: " + ex.Message));
    }

    protected override void OnStop() { _loop?.Dispose(); _loop = null; }
    protected override void OnShutdown() => OnStop();
    protected override void Dispose(bool disposing) { if (disposing) OnStop(); base.Dispose(disposing); }
}
