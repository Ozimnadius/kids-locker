using System.Net;
using KidsLocker.Service.Telegram;
using Xunit;

namespace KidsLocker.Tests;

public class TelegramClientTests
{
    [Fact]
    public async Task PollUpdates_ExposesServerDateForFreshnessWithoutWindowsClock()
    {
        using var http = new HttpClient(new ServerDateHandler());
        var poll = await new TelegramClient("token", http).PollUpdatesAsync(0, 0, default);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 18, 0, 0, TimeSpan.Zero), poll.ServerUtc);
    }

    private sealed class ServerDateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":[]}") };
            response.Headers.Date = new DateTimeOffset(2026, 11, 14, 18, 0, 0, TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        public StubHandler(HttpStatusCode code, string body) { _code = code; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent(_body) });
    }

    private static TelegramClient Client(HttpStatusCode code, string body)
        => new("token", new HttpClient(new StubHandler(code, body)));

    [Fact]
    public async Task PollUpdates_HttpError_IsFailed_NotJustEmpty()
    {
        // The hot-loop bug: a 401/409/429/5xx came back as an empty list indistinguishable from
        // "no updates", so the bot never backed off. Failed must be true here.
        var poll = await Client(HttpStatusCode.Unauthorized, "{\"ok\":false}").PollUpdatesAsync(0, 0, CancellationToken.None);
        Assert.True(poll.Failed);
        Assert.Empty(poll.Updates);
    }

    [Fact]
    public async Task PollUpdates_OkEmpty_IsNotFailed()
    {
        var poll = await Client(HttpStatusCode.OK, "{\"ok\":true,\"result\":[]}").PollUpdatesAsync(0, 0, CancellationToken.None);
        Assert.False(poll.Failed);
        Assert.Empty(poll.Updates);
    }

    [Fact]
    public async Task PollUpdates_OkWithMessage_Parsed()
    {
        var body = "{\"ok\":true,\"result\":[{\"update_id\":5,\"message\":{\"message_id\":1,\"chat\":{\"id\":42},\"text\":\"hi\"}}]}";
        var poll = await Client(HttpStatusCode.OK, body).PollUpdatesAsync(0, 0, CancellationToken.None);
        Assert.False(poll.Failed);
        var u = Assert.Single(poll.Updates);
        Assert.Equal(5, u.UpdateId);
        Assert.Equal(42, u.Message!.Chat!.Id);
    }
}
