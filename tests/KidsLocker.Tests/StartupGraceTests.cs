using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using KidsLocker.Service.Scheduling;
using KidsLocker.Service.Setup;
using KidsLocker.Service.Time;
using Xunit;

namespace KidsLocker.Tests;

public class StartupGraceTests
{
    [Fact]
    public async Task ParentDisablesControlDuringGrace_PendingCheckDoesNotShutdown()
    {
        var current = Schedule.Default();
        var response = new TaskCompletionSource<DateTimeOffset?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = TrustedSchedule.EvaluateCurrentAsync(() => current, () => response.Task);
        current = new Schedule { Enabled = false };
        response.SetResult(null);
        Assert.False((await check).Blocked);
    }

    [Fact]
    public async Task UnknownTime_WaitsFiveMinutesAndThenCancelsRequest()
    {
        var timer = new ManualTimer();
        var source = new WaitingSource();
        var clock = new TrustedClock(source, timer);
        using var stop = new CancellationTokenSource();
        var initialize = clock.InitializeAsync(stop.Token);
        try
        {
            timer.Advance(TimeSpan.FromSeconds(299));
            Assert.False(source.Cancelled);
            Assert.False(initialize.IsCompleted);
            timer.Advance(TimeSpan.FromSeconds(1));
            Assert.True(source.Cancelled);
            await initialize.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(clock.UtcNow);
            Assert.True(TrustedSchedule.Evaluate(Schedule.Default(), clock.UtcNow).Blocked);
        }
        finally
        {
            stop.Cancel();
            try { await initialize; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ConfirmedForbiddenTime_DoesNotWaitForGraceToExpire()
    {
        var timer = new ManualTimer();
        var clock = new TrustedClock(new TrustedTimeTests.Source(
            new DateTimeOffset(2026, 11, 14, 21, 0, 0, TimeSpan.Zero)), timer);
        await clock.InitializeAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, timer.GetTimestamp());
        Assert.True(TrustedSchedule.Evaluate(Schedule.Default(), clock.UtcNow).Blocked);
    }

    [Fact]
    public async Task NetworkReturnsDuringGrace_RetryImmediatelyAppliesSchedule()
    {
        var timer = new ManualTimer();
        var source = new TrustedTimeTests.Source(null);
        var clock = new TrustedClock(source, timer);
        using var stop = new CancellationTokenSource();
        var initialize = clock.InitializeAsync(stop.Token);
        try
        {
            Assert.False(initialize.IsCompleted);
            source.Value = new DateTimeOffset(2026, 11, 14, 21, 0, 0, TimeSpan.Zero);
            timer.Advance(TimeSpan.FromSeconds(2));
            await initialize.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.NotNull(clock.UtcNow);
            Assert.True(TrustedSchedule.Evaluate(Schedule.Default(), clock.UtcNow).Blocked);
        }
        finally
        {
            stop.Cancel();
            try { await initialize; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task StoppingService_CancelsGraceWithoutWaitingFiveMinutes()
    {
        var source = new WaitingSource();
        var clock = new TrustedClock(source, new ManualTimer());
        using var stop = new CancellationTokenSource();
        var initialize = clock.InitializeAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize);
        Assert.True(source.Cancelled);
    }

    [Fact]
    public void ScheduledTask_AllowsTimeForGraceAndShutdown()
    {
        // Проверяем генерируемый документ, который получает планировщик Windows.
        var build = typeof(Installer).GetMethod("TaskXml", BindingFlags.NonPublic | BindingFlags.Static)!;
        var xml = (string)build.Invoke(null, new object[] { @"C:\KidsLocker.exe", "--enforce-once --live", Array.Empty<string>() })!;
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var limit = XmlConvert.ToTimeSpan(doc.Root!.Element(ns + "Settings")!.Element(ns + "ExecutionTimeLimit")!.Value);
        Assert.True(limit >= TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30));
    }

    private sealed class WaitingSource : IInternetTimeSource
    {
        private CancellationToken _token;
        // Проверяем состояние токена: отдельный callback мог быть снят при
        // завершении Task.Delay раньше, чем очередь отмены успевала вызвать его.
        public bool Cancelled => _token.IsCancellationRequested;
        public async Task<DateTimeOffset?> GetUtcAsync(CancellationToken ct)
        {
            _token = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    // Виртуальное время ускоряет проверку пяти минут и не меняет часы компьютера.
    private sealed class ManualTimer : TimeProvider
    {
        private readonly List<Alarm> _alarms = new();
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Часы Windows не участвуют в ожидании");
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var alarm = new Alarm(this, callback, state);
            alarm.Change(dueTime, period);
            lock (_alarms) _alarms.Add(alarm);
            return alarm;
        }
        public void Advance(TimeSpan span)
        {
            _ticks += span.Ticks;
            Alarm[] alarms;
            lock (_alarms) alarms = _alarms.ToArray();
            foreach (var alarm in alarms) alarm.FireIfDue();
        }
        private sealed class Alarm(ManualTimer owner, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            private long _period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                _period = period.Ticks;
                return true;
            }
            public void FireIfDue()
            {
                if (owner._ticks < _due) return;
                _due = _period > 0 ? owner._ticks + _period : long.MaxValue;
                callback(state);
            }
            public void Dispose() => _due = long.MaxValue;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
