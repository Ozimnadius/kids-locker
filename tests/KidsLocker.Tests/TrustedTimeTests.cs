using KidsLocker.Service.Scheduling;
using KidsLocker.Service.Time;
using Xunit;

namespace KidsLocker.Tests;

public class TrustedTimeTests
{
    private static readonly DateTimeOffset Initial = new(2026, 11, 14, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RefreshBecomesDueAfterFifteenMinutesOfMonotonicTime()
    {
        var timer = new Timer();
        var clock = new TrustedClock(new Source(Initial), timer);
        Assert.True(clock.RefreshDue);
        await clock.RefreshAsync(default);
        timer.Advance(TimeSpan.FromMinutes(14));
        Assert.False(clock.RefreshDue);
        timer.Advance(TimeSpan.FromMinutes(1));
        Assert.True(clock.RefreshDue);
    }

    [Fact]
    public async Task FailedRefresh_DoesNotHammerApisEveryEnforcementTick()
    {
        var timer = new Timer();
        var clock = new TrustedClock(new Source(null), timer);
        await clock.RefreshAsync(default);
        timer.Advance(TimeSpan.FromSeconds(10));
        Assert.False(clock.RefreshDue);
    }

    [Fact]
    public async Task NoNetworkAfterStart_BlocksEvenWithPause()
    {
        var clock = new TrustedClock(new Source(null), new Timer());
        await clock.RefreshAsync(default);
        var s = Schedule.Default();
        s.PauseUntil = new DateTime(2099, 1, 1);
        Assert.Null(clock.UtcNow);
        Assert.True(TrustedSchedule.Evaluate(s, clock.UtcNow).Blocked);
    }

    [Fact]
    public async Task NetworkLostAfterSync_ClockKeepsAdvancingAcrossSleep()
    {
        var source = new Source(Initial);
        var timer = new Timer();
        var clock = new TrustedClock(source, timer);
        await clock.RefreshAsync(default);
        source.Value = null;
        // Таймер Windows учитывает сон; системные календарные часы для расчёта не читаются.
        timer.Advance(TimeSpan.FromHours(3));
        await clock.RefreshAsync(default);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 21, 0, 0, TimeSpan.Zero), clock.UtcNow);
        Assert.True(TrustedSchedule.Evaluate(Schedule.Default(), clock.UtcNow).Blocked);
    }

    [Fact]
    public async Task NewClock_DoesNotInheritTrustFromPreviousProcess()
    {
        var source = new Source(Initial);
        var clock = new TrustedClock(source, new Timer());
        await clock.RefreshAsync(default);
        source.Value = null;
        var restarted = new TrustedClock(source, new Timer());
        await restarted.RefreshAsync(default);
        Assert.Null(restarted.UtcNow);
    }

    [Fact]
    public async Task NetworkCorrection_CannotMoveTrustedTimeBackwards()
    {
        var source = new Source(Initial);
        var timer = new Timer();
        var clock = new TrustedClock(source, timer);
        await clock.RefreshAsync(default);
        timer.Advance(TimeSpan.FromMinutes(30));
        source.Value = Initial.AddHours(-2);
        await clock.RefreshAsync(default);
        Assert.Equal(Initial.AddMinutes(30), clock.UtcNow);
    }

    [Fact]
    public void PinnedTimezone_UsesMoscowEvenIfLastObservedZoneChanges()
    {
        var s = Schedule.Default();
        s.LastTimeZoneId = "Pacific Standard Time";
        Assert.Equal(new DateTime(2026, 11, 14, 21, 0, 0), TrustedSchedule.LocalNow(s, Initial));
        Assert.False(TrustedSchedule.Evaluate(s, Initial).Blocked);
        Assert.True(TrustedSchedule.Evaluate(s, Initial.AddHours(3)).Blocked);
    }

    [Fact]
    public void InvalidLegacyTimezone_DoesNotChangeMoscowSchedule()
    {
        var s = Schedule.Default();
        s.LastTimeZoneId = "несуществующий пояс";
        Assert.False(TrustedSchedule.Evaluate(s, Initial).Blocked);
        Assert.Equal(new DateTime(2026, 11, 14, 21, 0, 0), TrustedSchedule.LocalNow(s, Initial));
    }

    [Fact]
    public void ParentDisabledControl_RemainsDisabledWithoutNetwork()
    {
        Assert.False(TrustedSchedule.Evaluate(new Schedule { Enabled = false }, null).Blocked);
    }

    internal sealed class Source(DateTimeOffset? value) : IInternetTimeSource
    {
        public DateTimeOffset? Value { get; set; } = value;
        public Task<DateTimeOffset?> GetUtcAsync(CancellationToken ct) => Task.FromResult(Value);
    }

    internal sealed class Timer : TimeProvider
    {
        private long _stamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _stamp;
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Часы Windows читать нельзя");
        public void Advance(TimeSpan span) => _stamp += span.Ticks;
    }
}
