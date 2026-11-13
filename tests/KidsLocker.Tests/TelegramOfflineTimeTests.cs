using System.Net;
using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Scheduling;
using KidsLocker.Service.Telegram;
using KidsLocker.Service.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KidsLocker.Tests;

[Collection("EnvHome")]
public class TelegramOfflineTimeTests
{
    [Fact]
    public async Task ParentCanDisableControl_WhenTimeApisAreUnavailable()
    {
        var home = Path.Combine(Path.GetTempPath(), "kl-tg-clock-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("KIDSLOCKER_HOME", home);
        try
        {
            var store = new ScheduleStore();
            store.Get();
            using var http = new HttpClient(new Handler());
            var clock = new TrustedClock(new TrustedTimeTests.Source(null));
            using var bot = new TelegramBot(NullLogger<TelegramBot>.Instance,
                new AppConfig { BotToken = "token", ParentChatId = 42 }, new TelegramClient("token", http),
                new CommandHandler(store, new Enforcer(NullLogger<Enforcer>.Instance, false)), clock);
            await bot.StartAsync(default);
            try
            {
                for (var i = 0; i < 20 && store.Get().Enabled; i++) await Task.Delay(50);
                Assert.False(store.Get().Enabled);
                Assert.Null(clock.UtcNow);
            }
            finally { await bot.StopAsync(default); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIDSLOCKER_HOME", null);
            if (Directory.Exists(home)) Directory.Delete(home, true);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private bool _sent;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = "{\"ok\":true}";
            if (request.RequestUri!.AbsolutePath.EndsWith("getUpdates"))
            {
                if (_sent) await Task.Delay(Timeout.Infinite, ct);
                _sent = true;
                body = "{\"ok\":true,\"result\":[{\"update_id\":1,\"message\":{\"date\":1794679200,\"chat\":{\"id\":42},\"text\":\"/off\"}}]}";
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            response.Headers.Date = new DateTimeOffset(2026, 11, 14, 18, 0, 0, TimeSpan.Zero);
            return response;
        }
    }
}
