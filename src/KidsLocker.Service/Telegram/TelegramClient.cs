using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidsLocker.Service.Telegram;

/// <summary>A single incoming Telegram update (only the fields we use).</summary>
public sealed class TgUpdate
{
    [JsonPropertyName("update_id")] public long UpdateId { get; set; }
    [JsonPropertyName("message")] public TgMessage? Message { get; set; }
}

public sealed class TgMessage
{
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
    [JsonPropertyName("date")] public long Date { get; set; } // unix seconds when the message was sent
    [JsonPropertyName("chat")] public TgChat? Chat { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
}

public sealed class TgChat
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("first_name")] public string? FirstName { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }

    public string DisplayName =>
        Username is not null ? "@" + Username : (FirstName ?? Title ?? "");
}

/// <summary>
/// Minimal Telegram Bot API client over HttpClient — no third-party dependency, so the
/// self-contained exe stays small and auditable. Long-polls getUpdates and sends messages.
/// </summary>
public sealed class TelegramClient
{
    private readonly HttpClient _http;
    private readonly string _base;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public TelegramClient(string token, HttpClient http)
    {
        _http = http;
        _base = $"https://api.telegram.org/bot{token}/";
    }

    public async Task<bool> SendMessageAsync(long chatId, string text, CancellationToken ct)
    {
        var (ok, _) = await SendMessageResultAsync(chatId, text, ct);
        return ok;
    }

    /// <summary>Send a message and, on failure, return Telegram's error description (for the test command).</summary>
    public async Task<(bool ok, string detail)> SendMessageResultAsync(long chatId, string text, CancellationToken ct)
    {
        try
        {
            var payload = new { chat_id = chatId, text, disable_web_page_preview = true };
            using var resp = await _http.PostAsJsonAsync(_base + "sendMessage", payload, ct);
            if (resp.IsSuccessStatusCode) return (true, "");
            var body = await resp.Content.ReadAsStringAsync(ct);
            return (false, ExtractDescription(body) ?? $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Verify the token via getMe. Returns the bot's @username, or null if the token is bad.</summary>
    public async Task<string?> GetMeAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(_base + "getMe", ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean()
                && root.TryGetProperty("result", out var res)
                && res.TryGetProperty("username", out var un))
                return un.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractDescription(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Result of a long-poll: the updates, plus whether the call FAILED (network/HTTP error) as
    /// opposed to simply returning no updates. The bot loop uses <see cref="Failed"/> to back off
    /// instead of hammering the API when the token is bad, a second poller is active (409), etc.
    /// </summary>
    public readonly record struct UpdatesPoll(List<TgUpdate> Updates, bool Failed);

    /// <summary>
    /// Long-poll for updates newer than <paramref name="offset"/>. Distinguishes "no updates" from
    /// "the call failed" so the caller can pace itself. Caller advances the offset past the highest
    /// UpdateId. Never throws except on cancellation.
    /// </summary>
    public async Task<UpdatesPoll> PollUpdatesAsync(long offset, int timeoutSec, CancellationToken ct)
    {
        try
        {
            var t = Math.Clamp(timeoutSec, 0, 120);
            var url = $"{_base}getUpdates?timeout={t}&offset={offset}&allowed_updates=[\"message\"]";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return new(new(), Failed: true);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("result", out var result)) return new(new(), Failed: true);

            var list = new List<TgUpdate>();
            foreach (var item in result.EnumerateArray())
            {
                var u = item.Deserialize<TgUpdate>(JsonOpts);
                if (u is not null) list.Add(u);
            }
            return new(list, Failed: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(new(), Failed: true);
        }
    }

    /// <summary>
    /// One-shot convenience for the GUI/CLI (token check, chat-id detection): returns just the
    /// updates and treats any failure as "no updates". Not for the long-poll loop — that uses
    /// <see cref="PollUpdatesAsync"/> so it can tell failure from emptiness and back off.
    /// </summary>
    public async Task<List<TgUpdate>> GetUpdatesAsync(long offset, int timeoutSec, CancellationToken ct)
        => (await PollUpdatesAsync(offset, timeoutSec, ct)).Updates;
}
