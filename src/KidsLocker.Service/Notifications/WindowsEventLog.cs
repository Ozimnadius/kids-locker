using System.Diagnostics;
using System.Runtime.Versioning;

namespace KidsLocker.Service.Notifications;

/// <summary>
/// Thin, best-effort wrapper over the Windows Event Log. Creating the event source
/// requires admin (done once by the installer); if it is missing we silently no-op so
/// nothing crashes when running unelevated on a dev machine.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsEventLog
{
    public const string Source = "KidsLocker";
    public const string LogName = "Application";

    public static void TryWrite(string message, EventLogEntryType type = EventLogEntryType.Warning)
    {
        try
        {
            if (!EventLog.SourceExists(Source))
                return; // source not registered (needs admin) — skip quietly
            EventLog.WriteEntry(Source, message, type);
        }
        catch
        {
            // Never let logging failures affect enforcement.
        }
    }

    /// <summary>Called by the installer (elevated) to register the event source once.</summary>
    public static void EnsureSource()
    {
        if (!EventLog.SourceExists(Source))
            EventLog.CreateEventSource(new EventSourceCreationData(Source, LogName));
    }
}
