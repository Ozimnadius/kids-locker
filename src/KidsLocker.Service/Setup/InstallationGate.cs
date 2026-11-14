using System.Security.AccessControl;
using System.Security.Principal;

namespace KidsLocker.Service.Setup;

/// <summary>Одна блокировка для установщика, удаления, хранителей и самовосстановления.</summary>
internal sealed class InstallationGate : IDisposable
{
    private readonly Mutex _mutex;
    private InstallationGate(Mutex mutex) => _mutex = mutex;

    public static InstallationGate? TryEnter(TimeSpan wait, string name = @"Global\KidsLocker.Installation.v1", MutexSecurity? security = null)
    {
        if (security is null)
        {
            security = new MutexSecurity();
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(sid, null), MutexRights.FullControl, AccessControlType.Allow));
        }
        var mutex = MutexAcl.Create(false, name, out _, security);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(wait); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return new InstallationGate(mutex);
            mutex.Dispose();
            return null;
        }
        catch { mutex.Dispose(); throw; }
    }

    public static InstallationGate Enter() => TryEnter(TimeSpan.FromSeconds(60))
        ?? throw new InvalidOperationException("Установка или восстановление уже выполняется. Повторите через минуту.");

    // Mutex принадлежит потоку: внутри этой области нельзя использовать await.
    public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
