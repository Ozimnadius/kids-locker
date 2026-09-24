using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace KidsLocker.Service.Enforcement;

/// <summary>
/// Helpers for inspecting and repairing the Windows service, used by the <c>--guard</c>
/// self-healing mode. Recreation of a deleted service shells out to sc.exe (the service
/// control APIs for creation are awkward to P/Invoke and sc.exe is always present).
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
            return State.Missing; // service not installed
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
    /// Apply a change to a config/schedule file so the running service adopts it without
    /// its tamper-detection firing: stop, write, start. If the service isn't running, just write.
    /// </summary>
    public static void ApplyWithReload(Action write)
    {
        var running = GetState() == State.Running;
        if (running)
        {
            StopMarker.WriteExpected(); // this stop is ours — suppress the "control stopped" alert
            TryStop();
        }
        write();
        if (running) TryStart();
    }

    /// <summary>Recreate the service pointing at this same executable, with auto-start and recovery.</summary>
    public static bool TryRecreate()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        var okCreate = RunSc($"create {ServiceName} binPath= \"{exe}\" start= auto obj= LocalSystem DisplayName= KidsLocker");
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
        RunSc($"failureflag {ServiceName} 1");
        var okStart = RunSc($"start {ServiceName}");
        return okCreate || okStart;
    }

    private static bool RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(20000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
