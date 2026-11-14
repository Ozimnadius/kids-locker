using System.ServiceProcess;
using KidsLocker.Service.Enforcement;

namespace KidsLocker.Service.Setup;

internal static class RecoveryServiceRegistration
{
    public const string Name = "KidsLockerRecoverySvc";
    public static string BinPath(string exe) => $"\"\\\"{exe}\\\" --recovery-service\"";

    public static ServiceControl.State GetState()
    {
        try
        {
            using var service = new ServiceController(Name);
            return service.Status switch
            {
                ServiceControllerStatus.Running or ServiceControllerStatus.StartPending => ServiceControl.State.Running,
                ServiceControllerStatus.Stopped => ServiceControl.State.Stopped,
                _ => ServiceControl.State.Other
            };
        }
        catch (InvalidOperationException) { return ServiceControl.State.Missing; }
        catch { return ServiceControl.State.Other; }
    }

    public static void Configure()
    {
        Configure(GetState() != ServiceControl.State.Missing, ExecutableRecovery.Store.BackupPath,
            command => ProcessRunner.Run("sc.exe", command, 20000), () => SafeBootRegistration.Register(Name));
    }

    internal static void Configure(bool exists, string exe, Func<string, (int code, string output)> run, Action registerSafeBoot)
    {
        void Required(string args)
        {
            var result = run(args);
            if (result.code != 0) throw new IOException("Не удалось настроить резервную службу: " + result.output);
        }
        Required($"{(exists ? "config" : "create")} {Name} binPath= {BinPath(exe)} start= auto obj= LocalSystem DisplayName= KidsLockerRecovery");
        Required($"failure {Name} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
        Required($"failureflag {Name} 1");
        registerSafeBoot();
    }

    public static bool EnsureRunning()
    {
        if (!ExecutableRecovery.Store.Enabled) return false;
        if (GetState() == ServiceControl.State.Running) return false;
        Configure();
        using var service = new ServiceController(Name);
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        return true;
    }

    public static void Stop(bool delete)
    {
        if (GetState() == ServiceControl.State.Missing) return;
        using (var service = new ServiceController(Name))
        {
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }
        if (delete)
        {
            var result = ProcessRunner.Run("sc.exe", $"delete {Name}", 20000);
            if (result.code != 0) throw new IOException("Не удалось удалить резервную службу: " + result.output);
        }
    }
}
