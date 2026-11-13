using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace KidsLocker.Service.Time;

/// <summary>Два независимых HTTPS-источника. Ошибка никогда не заменяется временем Windows.</summary>
public sealed class InternetTimeSource(HttpClient http, TimeSpan? requestTimeout = null) : IInternetTimeSource
{
    private static readonly string[] Endpoints =
    {
        "https://timeapi.io/api/time/current/zone?timeZone=UTC",
        "https://www.cloudflare.com/cdn-cgi/trace"
    };

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        // Служба не наследует пользовательский прокси. Проверка TLS остаётся стандартной.
        UseProxy = false
    }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 16_384 };

    public async Task<DateTimeOffset?> GetUtcAsync(CancellationToken ct)
    {
        foreach (var endpoint in Endpoints)
        {
            ct.ThrowIfCancellationRequested();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(4));
            try
            {
                // Уникальный запрос и запрет кеша исключают повтор старой отметки CDN/прокси.
                var url = endpoint + (endpoint.Contains('?') ? "&" : "?") + "kl=" + Guid.NewGuid().ToString("N");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                response.EnsureSuccessStatusCode();
                if (response.Headers.Date is not { } serverDate || response.Headers.Age > TimeSpan.FromSeconds(2)) continue;
                await response.Content.LoadIntoBufferAsync(16_384, budget.Token);
                var body = await response.Content.ReadAsStringAsync(budget.Token);
                var utc = endpoint.Contains("timeapi.io") ? ParseTimeApi(body) : ParseCloudflare(body);
                // Сравнение только с ответом сервера, а не с потенциально подменённой датой ПК.
                if (utc.Year < 2024 || utc.Year > 2100 || Math.Abs((utc - serverDate).TotalSeconds) > 5) continue;
                return utc;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                or JsonException or FormatException or InvalidOperationException or ArgumentException or IOException)
            {
                // Следующий независимый источник; если оба не ответили, доверенного времени нет.
            }
        }
        return null;
    }

    private static DateTimeOffset ParseTimeApi(string body)
    {
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("timeZone", out var zone) || zone.GetString() != "UTC"
            || !json.RootElement.TryGetProperty("dateTime", out var value))
            throw new FormatException("API не вернул время UTC");
        var utc = DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        if (utc.Offset != TimeSpan.Zero) throw new FormatException("API вернул другой часовой пояс");
        return utc;
    }

    private static DateTimeOffset ParseCloudflare(string body)
    {
        var stamp = body.Split('\n').SingleOrDefault(line => line.StartsWith("ts=", StringComparison.Ordinal));
        if (stamp is null || !decimal.TryParse(stamp[3..].Trim(), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var seconds) || seconds < 0 || seconds > 4_133_980_800)
            throw new FormatException("Cloudflare не вернул корректную серверную отметку времени");
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
    }
}
