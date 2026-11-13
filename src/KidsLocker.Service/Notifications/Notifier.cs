using KidsLocker.Service.Config;
using KidsLocker.Service.Telegram;
using Microsoft.Extensions.Logging;

namespace KidsLocker.Service.Notifications;

/// <summary>
/// Единая точка для событий, видимых пользователю. Каждый алерт уходит в три приёмника:
/// лог приложения (через ILogger), журнал событий Windows и Telegram родителя.
/// Сбой одного приёмника никогда не мешает остальным.
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

    public void Info(string message)
    {
        OperationalLog.Write(message);
        try { _log.LogInformation("{Message}", message); } catch { }
    }

    /// <summary>Алерт «отправил и забыл» для долгоживущего цикла (не блокирует цикл).</summary>
    public void Alert(string message)
    {
        LogLocally(message);
        if (CanSend) _ = SendTelegramAsync(message, CancellationToken.None);
    }

    /// <summary>
    /// Алерт с ожиданием для короткоживущих режимов (enforce-once, guard), которым нужно
    /// закончить отправку до выхода процесса или выключения машины.
    /// </summary>
    public async Task AlertAsync(string message, CancellationToken ct = default)
    {
        LogLocally(message);
        if (CanSend) await SendTelegramAsync(message, ct);
    }

    private bool CanSend => _telegram is not null && _config.IsTelegramConfigured;

    private void LogLocally(string message)
    {
        OperationalLog.Write(message);
        try { _log.LogWarning("Событие: {Message}", message); } catch { }
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
