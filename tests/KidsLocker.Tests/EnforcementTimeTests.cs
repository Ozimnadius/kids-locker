using KidsLocker.Service;
using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Notifications;
using KidsLocker.Service.Scheduling;
using KidsLocker.Service.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KidsLocker.Tests;

[Collection("EnvHome")]
public class EnforcementTimeTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "klclock-" + Guid.NewGuid().ToString("N"));
    public EnforcementTimeTests() => Environment.SetEnvironmentVariable("KIDSLOCKER_HOME", _home);
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("KIDSLOCKER_HOME", null);
        if (Directory.Exists(_home)) Directory.Delete(_home, true);
    }

    [Fact]
    public async Task Worker_NoVerifiedTime_RequestsShutdownDespitePause()
    {
        var store = new ScheduleStore();
        var schedule = Schedule.Default();
        schedule.PauseUntil = new DateTime(2099, 1, 1);
        store.Save(schedule);
        var clock = new TrustedClock(new TrustedTimeTests.Source(null));
        var shutdowns = new List<string>();
        using var worker = Worker(store, clock, shutdowns);
        await worker.TickAsync(default);
        Assert.Single(shutdowns);
    }

    [Fact]
    public async Task Worker_UsesVerifiedTime_WhenWindowsShowsSomethingElse()
    {
        var store = new ScheduleStore();
        store.Save(Schedule.Default());
        // Суббота 21:00 по Москве — разрешено; 00:00 воскресенья — запрещено.
        var timer = new TrustedTimeTests.Timer();
        var clock = new TrustedClock(new TrustedTimeTests.Source(new DateTimeOffset(2026, 11, 14, 18, 0, 0, TimeSpan.Zero)), timer);
        await clock.RefreshAsync(default);
        var shutdowns = new List<string>();
        using var worker = Worker(store, clock, shutdowns);
        await worker.TickAsync(default);
        Assert.Empty(shutdowns);
        timer.Advance(TimeSpan.FromHours(3));
        await worker.TickAsync(default);
        Assert.Single(shutdowns);
    }

    private static EnforcementWorker Worker(ScheduleStore store, TrustedClock clock, List<string> shutdowns)
    {
        var cfg = new AppConfig();
        return new EnforcementWorker(NullLogger<EnforcementWorker>.Instance, store,
            new Enforcer(NullLogger<Enforcer>.Instance, true, shutdowns.Add),
            new Notifier(NullLogger<Notifier>.Instance, cfg, null), cfg, clock);
    }
}
