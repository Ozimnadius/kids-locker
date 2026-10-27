using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using KidsLocker.Service.Config;

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

    /// <summary>
    /// Build the quoted <c>binPath=</c> value for sc.exe. The executable path contains spaces
    /// (Program Files), so it must be wrapped in escaped inner quotes inside the outer quotes:
    /// <c>"\"C:\…\KidsLocker.exe\" --dry-run"</c>. A single pair of quotes would be split by the
    /// command-line parser (into "C:\Program" and "Files\…"), leaving the service with a broken path.
    /// </summary>
    public static string BuildBinPath(string exe, bool dryRun) =>
        dryRun ? $"\"\\\"{exe}\\\" --dry-run\"" : $"\"\\\"{exe}\\\"\"";

    /// <summary>Recreate the service pointing at this same executable, with auto-start and recovery.
    /// Re-creates it in the ORIGINAL install mode (dry-run vs live) recorded by the installer, so a
    /// test install revived by the guardian does not silently start forcing real shutdowns.</summary>
    public static bool TryRecreate()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        var dryRun = InstalledDryRun();
        var okCreate = RunSc($"create {ServiceName} binPath= {BuildBinPath(exe, dryRun)} start= auto obj= LocalSystem DisplayName= KidsLocker");
        RunSc($"description {ServiceName} \"KidsLocker parental time control\"");
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
        RunSc($"failureflag {ServiceName} 1");
        var okStart = RunSc($"start {ServiceName}");
        return okCreate || okStart;
    }

    /// <summary>
    /// Read the install mode the installer recorded. If the marker is missing or unreadable we
    /// default to dry-run: an unattended re-create of a service whose mode we cannot confirm must
    /// NOT start powering the machine off on its own (the guardian also alerts the parent that it
    /// re-created the service, so a genuine live install can be re-applied knowingly).
    /// </summary>
    private static bool InstalledDryRun()
    {
        try
        {
            if (File.Exists(AppPaths.ServiceModeFile))
                return !string.Equals(File.ReadAllText(AppPaths.ServiceModeFile).Trim(), "live", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* fall through to the safe default */ }
        return true;
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
