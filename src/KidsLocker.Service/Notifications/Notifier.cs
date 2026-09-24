using KidsLocker.Service.Config;
using KidsLocker.Service.Telegram;
using Microsoft.Extensions.Logging;

namespace KidsLocker.Service.Notifications;

/// <summary>
/// Central place for user-facing events. Every alert goes to three sinks:
/// the app log (via ILogger), the Windows Event Log, and the parent's Telegram.
/// A failure in any one sink never blocks the others.
/// </summary>
public sealed class Notifier
{
    private readonly ILogger<Notifier> _log;
    private readonly AppConfig _config;
    private readonly TelegramClient? _telegram;

    public Notifier(ILogger<Notifier> log, AppConfig config, TelegramClient? telegram)
    {
        _log = log;
        _config = config;
        _telegram = telegram;
    }

    public void Info(string message) => _log.LogInformation("{Message}", message);

    /// <summary>Fire-and-forget alert for the long-running worker (does not block the loop).</summary>
    public void Alert(string message)
    {
        LogLocally(message);
        if (CanSend) _ = SendTelegramAsync(message, CancellationToken.None);
    }

    /// <summary>
    /// Awaitable alert for short-lived modes (enforce-once, guard) that must finish sending
    /// before the process exits or the machine powers off.
    /// </summary>
    public async Task AlertAsync(string message, CancellationToken ct = default)
    {
        LogLocally(message);
        if (CanSend) await SendTelegramAsync(message, ct);
    }

    private bool CanSend => _telegram is not null && _config.IsTelegramConfigured;

    private void LogLocally(string message)
    {
        _log.LogWarning("ALERT: {Message}", message);
        WindowsEventLog.TryWrite(message);
    }

    private async Task SendTelegramAsync(string message, CancellationToken ct)
    {
        try
        {
            await _telegram!.SendMessageAsync(_config.ParentChatId, "⚠ " + message, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to send Telegram alert");
        }
    }
}
