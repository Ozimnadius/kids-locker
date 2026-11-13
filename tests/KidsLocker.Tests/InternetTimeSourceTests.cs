using System.Net;
using KidsLocker.Service.Time;
using Xunit;

namespace KidsLocker.Tests;

public class InternetTimeSourceTests
{
    private static readonly DateTimeOffset Expected = new(2026, 11, 14, 18, 0, 0, TimeSpan.Zero);
    private const string Primary = "{\"dateTime\":\"2026-11-14T18:00:00\",\"timeZone\":\"UTC\"}";
    private const string Backup = "h=www.cloudflare.com\nts=1794679200.000\n";

    [Fact]
    public async Task Primary_ParsesUtcWithoutUsingLocalClock()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(Primary))));
        Assert.Equal(Expected, await new InternetTimeSource(http).GetUtcAsync(default));
    }

    [Theory]
    [InlineData("{}")] [InlineData("не JSON")]
    [InlineData("{\"dateTime\":\"2026-11-14T18:00:00\",\"timeZone\":\"Europe/Moscow\"}")]
    [InlineData("{\"dateTime\":\"2020-01-01T18:00:00\",\"timeZone\":\"UTC\"}")]
    public async Task BadPrimary_UsesIndependentBackup(string body)
    {
        using var http = new HttpClient(new Handler((r, _) => Task.FromResult(
            Response(r.RequestUri!.Host == "timeapi.io" ? body : Backup))));
        Assert.Equal(Expected, await new InternetTimeSource(http).GetUtcAsync(default));
    }

    [Fact]
    public async Task PrimaryTimeout_UsesBackup()
    {
        using var http = new HttpClient(new Handler(async (r, ct) =>
        {
            if (r.RequestUri!.Host == "timeapi.io") await Task.Delay(Timeout.Infinite, ct);
            return Response(Backup);
        }));
        var source = new InternetTimeSource(http, TimeSpan.FromMilliseconds(50));
        Assert.Equal(Expected, await source.GetUtcAsync(default));
    }

    [Fact]
    public async Task BothSourcesUnavailable_ReturnsNoTime()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new HttpRequestException("Нет сети")));
        Assert.Null(await new InternetTimeSource(http).GetUtcAsync(default));
    }

    [Fact]
    public async Task CachedOrOversizedResponses_AreRejected()
    {
        using var http = new HttpClient(new Handler((r, _) =>
        {
            var response = Response(r.RequestUri!.Host == "timeapi.io" ? Primary : new string('x', 20_000));
            response.Headers.Age = TimeSpan.FromHours(1);
            return Task.FromResult(response);
        }));
        Assert.Null(await new InternetTimeSource(http).GetUtcAsync(default));
    }

    [Fact]
    public async Task Cancellation_IsNotConvertedToSuccessfulTime()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(Primary))));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InternetTimeSource(http).GetUtcAsync(cts.Token));
    }

    private static HttpResponseMessage Response(string body)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        r.Headers.Date = Expected;
        return r;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => send(r, ct);
    }
}
