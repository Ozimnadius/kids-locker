using KidsLocker.Service.Setup;
using Xunit;

namespace KidsLocker.Tests;

public class SafeModeRecoveryTests
{
    [Fact]
    public void WindowsService_HasSeparateIdentityAndNoRequiredEventLog()
    {
        using var service = new RecoveryWindowsService();
        Assert.Equal("KidsLockerRecoverySvc", service.ServiceName);
        Assert.False(service.AutoLog);
        Assert.True(service.CanStop);
        Assert.True(service.CanShutdown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registration_UsesSeparateBackupAndAutomaticSystemService(bool exists)
    {
        var commands = new List<string>();
        var registered = false;
        const string backup = @"C:\ProgramData\KidsLocker\Recovery\KidsLocker.Recovery.exe";
        RecoveryServiceRegistration.Configure(exists, backup, command => { commands.Add(command); return (0, ""); },
            () => registered = true);
        Assert.StartsWith(exists ? "config " : "create ", commands[0]);
        Assert.Contains(RecoveryServiceRegistration.Name, commands[0]);
        Assert.Contains(RecoveryServiceRegistration.BinPath(backup), commands[0]);
        Assert.Contains("--recovery-service", commands[0]);
        Assert.Contains("start= auto obj= LocalSystem", commands[0]);
        Assert.DoesNotContain("depend=", commands[0]); // нет цикла ожидания основной службы
        Assert.Contains("restart/5000", commands[1]);
        Assert.True(registered);
    }

    [Fact]
    public void RegistrationFailure_DoesNotReportSuccess()
    {
        var registered = false;
        Assert.Throws<IOException>(() => RecoveryServiceRegistration.Configure(false, "backup.exe",
            _ => (5, "Отказано в доступе"), () => registered = true));
        Assert.False(registered);
    }

    [Theory]
    [InlineData("Minimal")]
    [InlineData("Network")]
    public void RecoveryService_HasItsOwnSafeBootEntry(string mode)
    {
        Assert.EndsWith($@"\SafeBoot\{mode}\KidsLockerRecoverySvc",
            SafeBootRegistration.PathFor(mode, RecoveryServiceRegistration.Name));
        Assert.NotEqual(SafeBootRegistration.PathFor(mode, Installer.ServiceName),
            SafeBootRegistration.PathFor(mode, RecoveryServiceRegistration.Name));
    }

    [Fact]
    public void Loop_StartsImmediatelyAndRepeatsWithoutSchedulerOrNetwork()
    {
        var time = new TimerSource();
        var calls = 0;
        using var loop = new RecoveryLoop(() => calls++, _ => { }, time);
        Assert.Equal(TimeSpan.Zero, time.Due);
        Assert.Equal(TimeSpan.FromSeconds(30), time.Period);
        time.Fire(); time.Fire();
        Assert.Equal(2, calls);
    }

    [Fact]
    public void StoppedLoop_IgnoresAlreadyQueuedCallback()
    {
        var time = new TimerSource();
        var calls = 0;
        var loop = new RecoveryLoop(() => calls++, _ => { }, time);
        loop.Dispose();
        time.Fire();
        Assert.Equal(0, calls);
        Assert.True(time.Disposed);
    }

    [Fact]
    public void FailedRecoveryOrLogging_DoesNotKillLoop()
    {
        var time = new TimerSource();
        var calls = 0;
        using var loop = new RecoveryLoop(() => { calls++; throw new IOException("Копия временно недоступна"); },
            _ => throw new IOException("Журнал недоступен"), time);
        time.Fire(); time.Fire();
        Assert.Equal(2, calls);
    }

    [Fact]
    public void SlowRecovery_DoesNotOverlapNextTick()
    {
        var time = new TimerSource();
        var calls = 0;
        using var loop = new RecoveryLoop(() => { calls++; time.Fire(); }, _ => { }, time);
        time.Fire();
        Assert.Equal(1, calls);
    }

    private sealed class TimerSource : TimeProvider, ITimer
    {
        public TimeSpan Due, Period;
        public bool Disposed;
        private TimerCallback? _callback;
        private object? _state;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback; _state = state; Change(dueTime, period); return this;
        }
        public void Fire() => _callback!(_state);
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime; Period = period; return true; }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
