using System.Runtime.Versioning;
using KidsLocker.Service.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KidsLocker.Service.Telegram;

/// <summary>
/// Long-polls Telegram for the parent's commands and dispatches them to
/// <see cref="CommandHandler"/>. Only messages from the configured parent chat id are
/// obeyed; anything else is ignored. Registered only when a token+chat id are configured.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TelegramBot : BackgroundService
{
    private readonly ILogger<TelegramBot> _log;
    private readonly AppConfig _config;
    private readonly TelegramClient _client;
    private readonly CommandHandler _handler;

    public TelegramBot(ILogger<TelegramBot> log, AppConfig config, TelegramClient client, CommandHandler handler)
    {
        _log = log;
        _config = config;
        _client = client;
        _handler = handler;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Telegram bot started; parent chat id {ChatId}", _config.ParentChatId);
        await _client.SendMessageAsync(_config.ParentChatId, "✅ KidsLocker запущен. /help — команды.", stoppingToken);

        // A real long poll must sit on the server a while; a 0/short timeout against a failing
        // endpoint is what turns the loop hot, so hold it at a sane minimum.
        var pollSeconds = Math.Clamp(_config.TelegramLongPollSeconds, 20, 90);
        var startedUtc = DateTimeOffset.UtcNow;
        long offset = LoadOffset();
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var poll = await _client.PollUpdatesAsync(offset, pollSeconds, stoppingToken);
                if (poll.Failed)
                {
                    // Network down, bad/revoked token (401), a second poller or a webhook (409), rate
                    // limit (429)… Back off exponentially (2→4→…→60s) instead of spinning the CPU and
                    // flooding the API. The long poll paces the success path, so no delay is needed there.
                    failures++;
                    var backoff = TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(failures, 6)));
                    if (failures == 1 || failures % 10 == 0)
                        _log.LogWarning("Telegram getUpdates failing; backing off {Seconds}s (attempt {Attempt})", backoff.TotalSeconds, failures);
                    try { await Task.Delay(backoff, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                failures = 0;
                foreach (var u in poll.Updates)
                {
                    offset = Math.Max(offset, u.UpdateId + 1);
                    SaveOffset(offset); // persist BEFORE acting, so a restart (or a /shutdown that kills us) can't replay it
                    if (IsStale(u.Message, startedUtc))
                        continue; // a command that piled up while the PC was off — don't act on it now
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
        catch { /* best effort — fall back to 0 */ }
        return 0;
    }

    private static void SaveOffset(long offset)
    {
        try
        {
            AppPaths.EnsureRoot();
            File.WriteAllText(AppPaths.TelegramOffsetFile, offset.ToString());
        }
        catch { /* best effort */ }
    }

    /// <summary>A message sent well before the bot started (PC was off/offline) must not be acted on
    /// now — e.g. a bedtime /shutdown should not power the machine off the next morning.</summary>
    private static bool IsStale(TgMessage? msg, DateTimeOffset startedUtc)
    {
        if (msg is null || msg.Date <= 0) return false;
        return DateTimeOffset.FromUnixTimeSeconds(msg.Date) < startedUtc - TimeSpan.FromMinutes(5);
    }
}
