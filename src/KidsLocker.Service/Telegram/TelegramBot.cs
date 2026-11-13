using System.Runtime.Versioning;
using KidsLocker.Service.Config;
using KidsLocker.Service.Time;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KidsLocker.Service.Telegram;

/// <summary>
/// Долгим опросом получает из Telegram команды родителя и передаёт их в
/// <see cref="CommandHandler"/>. Выполняются только сообщения из настроенного чата родителя;
/// всё остальное игнорируется. Регистрируется, только если настроены токен и id чата.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TelegramBot : BackgroundService
{
    private readonly ILogger<TelegramBot> _log;
    private readonly AppConfig _config;
    private readonly TelegramClient _client;
    private readonly CommandHandler _handler;
    private readonly TrustedClock _clock;

    public TelegramBot(ILogger<TelegramBot> log, AppConfig config, TelegramClient client, CommandHandler handler, TrustedClock clock)
    {
        _log = log;
        _config = config;
        _client = client;
        _handler = handler;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Telegram bot started; parent chat id {ChatId}", _config.ParentChatId);
        await _client.SendMessageAsync(_config.ParentChatId, "✅ KidsLocker запущен. /help — команды.", stoppingToken);

        // Настоящий долгий опрос должен какое-то время висеть на сервере; нулевой или короткий таймаут
        // при сбоящем адресе и превращает цикл в холостую молотилку, поэтому держим разумный минимум.
        var pollSeconds = Math.Clamp(_config.TelegramLongPollSeconds, 20, 90);
        var startedStamp = Stopwatch.GetTimestamp();
        DateTimeOffset? startedUtc = null;
        long offset = LoadOffset();
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var poll = await _client.PollUpdatesAsync(offset, pollSeconds, stoppingToken);
                if (poll.Failed)
                {
                    // Нет сети, неверный или отозванный токен (401), второй опрашивающий или вебхук (409),
                    // ограничение частоты (429)… Отступаем экспоненциально (2→4→…→60 с), а не крутим
                    // процессор и не заваливаем API запросами. Успешный путь сам темпирован долгим опросом,
                    // так что там задержка не нужна.
                    failures++;
                    var backoff = TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(failures, 6)));
                    if (failures == 1 || failures % 10 == 0)
                        _log.LogWarning("Telegram getUpdates failing; backing off {Seconds}s (attempt {Attempt})", backoff.TotalSeconds, failures);
                    try { await Task.Delay(backoff, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                // Date приходит от HTTPS API Telegram и служит только для свежести команд.
                // В расписание он не попадает; /off и /schedule остаются доступны при отказе API времени.
                if (startedUtc is null)
                {
                    var reference = _clock.UtcNow ?? poll.ServerUtc;
                    if (reference is null)
                    {
                        await Task.Delay(2000, stoppingToken);
                        continue;
                    }
                    startedUtc = reference - Stopwatch.GetElapsedTime(startedStamp);
                }

                failures = 0;
                foreach (var u in poll.Updates)
                {
                    offset = Math.Max(offset, u.UpdateId + 1);
                    SaveOffset(offset); // сохраняем ДО выполнения, чтобы перезапуск (или убивший нас /shutdown) не выполнил команду повторно
                    if (IsStale(u.Message, startedUtc.Value))
                        continue; // команда накопилась, пока ПК был выключен, — сейчас её не выполняем
                    await Dispatch(u, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Telegram poll error");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task Dispatch(TgUpdate u, CancellationToken ct)
    {
        var msg = u.Message;
        if (msg?.Chat is null || string.IsNullOrWhiteSpace(msg.Text))
            return;

        if (msg.Chat.Id != _config.ParentChatId)
        {
            _log.LogWarning("Ignoring message from unauthorized chat {ChatId}", msg.Chat.Id);
            return;
        }

        string reply;
        try
        {
            reply = _handler.Handle(msg.Text);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Command handling failed");
            reply = "Ошибка при обработке команды.";
        }

        await _client.SendMessageAsync(msg.Chat.Id, reply, ct);
    }

    private static long LoadOffset()
    {
        try
        {
            if (File.Exists(AppPaths.TelegramOffsetFile)
                && long.TryParse(File.ReadAllText(AppPaths.TelegramOffsetFile).Trim(), out var v) && v > 0)
                return v;
        }
        catch { /* по возможности — откатываемся к 0 */ }
        return 0;
    }

    private static void SaveOffset(long offset)
    {
        try
        {
            AppPaths.EnsureRoot();
            File.WriteAllText(AppPaths.TelegramOffsetFile, offset.ToString());
        }
        catch { /* по возможности */ }
    }

    /// <summary>Сообщение, отправленное задолго до запуска бота (ПК был выключен или без сети), сейчас
    /// выполнять нельзя — например, вечерний /shutdown не должен выключить машину на следующее утро.</summary>
    private static bool IsStale(TgMessage? msg, DateTimeOffset startedUtc)
    {
        if (msg is null || msg.Date <= 0) return false;
        return DateTimeOffset.FromUnixTimeSeconds(msg.Date) < startedUtc - TimeSpan.FromMinutes(5);
    }
}
