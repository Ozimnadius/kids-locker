using System.Diagnostics;
using System.Runtime.Versioning;
using KidsLocker.Service.Notifications;
using Microsoft.Extensions.Logging;

namespace KidsLocker.Service.Enforcement;

/// <summary>
/// Carries out the enforcement action: an immediate, forced shutdown.
/// A <c>live</c> flag gates the real shutdown so the same code can be exercised on a dev
/// machine in dry-run without powering it off.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Enforcer
{
    private readonly ILogger<Enforcer> _log;
    private readonly Notifier _notifier;
    private readonly bool _live;

    public Enforcer(ILogger<Enforcer> log, Notifier notifier, bool live)
    {
        _log = log;
        _notifier = notifier;
        _live = live;
    }

    public bool IsLive => _live;

    /// <summary>Immediately force a shutdown (or log the intent in dry-run mode).</summary>
    public void ShutdownNow(string reason)
    {
        if (!_live)
        {
            _log.LogWarning("[DRY-RUN] Would shut down now. Reason: {Reason}", reason);
            return;
        }

        _log.LogWarning("Forcing shutdown. Reason: {Reason}", reason);
        try
        {
            // /s shutdown, /f force-close apps, /t 0 immediately.
            var psi = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/s /f /t 0 /c \"KidsLocker: bedtime\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "shutdown.exe failed; falling back to InitiateSystemShutdown");
            NativeShutdown.TryForce();
        }
    }
}
