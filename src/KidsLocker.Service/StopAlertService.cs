using System.Runtime.Versioning;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Notifications;
using Microsoft.Extensions.Hosting;

namespace KidsLocker.Service;

/// <summary>
/// Sends a Telegram alert when the service is stopped in a way that is NOT a normal Windows
/// shutdown and NOT initiated by the app itself — i.e. when someone deliberately stops the
/// control. Runs in the host's stop path (StopAsync is awaited), so the alert gets out before
/// the process exits. A forced kill (taskkill /f) skips this, but the guardian task still
/// reports it within a minute.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StopAlertService : IHostedService
{
    private readonly Notifier _notifier;

    public StopAlertService(Notifier notifier) => _notifier = notifier;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (SystemBootMode.IsSystemShuttingDown) return; // OS shutting down (incl. bedtime shutdown)
            if (StopMarker.ConsumeIsExpected()) return;       // the app stopped us on purpose
            await _notifier.AlertAsync("⚠ Контроль KidsLocker выключен: службу остановили.");
        }
        catch { /* never let the stop path throw */ }
    }
}
