using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidsLocker.Service.Telegram;

/// <summary>Одно входящее обновление Telegram (только используемые нами поля).</summary>
public sealed class TgUpdate
{
    [JsonPropertyName("update_id")] public long UpdateId { get; set; }
    [JsonPropertyName("message")] public TgMessage? Message { get; set; }
}

public sealed class TgMessage
{
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
    [JsonPropertyName("date")] public long Date { get; set; } // момент отправки сообщения в секундах unix-времени
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
/// Минимальный клиент Telegram Bot API поверх HttpClient — без сторонних зависимостей, чтобы
/// самодостаточный exe оставался небольшим и проверяемым. Долгим опросом вызывает getUpdates и отправляет сообщения.
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

    /// <summary>Отправить сообщение и при неудаче вернуть описание ошибки от Telegram (для тестовой команды).</summary>
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

    /// <summary>Проверить токен через getMe. Возвращает @username бота или null, если токен неверный.</summary>
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
    /// Результат долгого опроса: обновления плюс признак того, что вызов НЕ УДАЛСЯ (ошибка сети/HTTP),
    /// в отличие от случая, когда обновлений просто нет. Цикл бота по <see cref="Failed"/> отступает,
    /// а не долбит API, когда токен неверный, работает второй опрашивающий (409) и т. п.
    /// </summary>
    public readonly record struct UpdatesPoll(List<TgUpdate> Updates, bool Failed);

    /// <summary>
    /// Долгий опрос обновлений новее <paramref name="offset"/>. Отличает «обновлений нет» от
    /// «вызов не удался», чтобы вызывающий мог регулировать темп. Вызывающий сам сдвигает offset за
    /// наибольший UpdateId. Исключений не бросает, кроме как при отмене.
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
    /// Разовый удобный вызов для пульта/CLI (проверка токена, определение id чата): возвращает только
    /// обновления и считает любой сбой случаем «обновлений нет». Не для цикла долгого опроса — тот
    /// использует <see cref="PollUpdatesAsync"/>, чтобы отличать сбой от пустоты и отступать.
    /// </summary>
    public async Task<List<TgUpdate>> GetUpdatesAsync(long offset, int timeoutSec, CancellationToken ct)
        => (await PollUpdatesAsync(offset, timeoutSec, ct)).Updates;
}
