using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Notifications;
using KidsLocker.Service.Scheduling;
using KidsLocker.Service.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KidsLocker.Tests;

/// <summary>
/// Exercises the real command path (parsing + schedule writes) through <see cref="CommandHandler"/>.
/// Points KIDSLOCKER_HOME at a throwaway dir so %ProgramData% is never touched.
/// </summary>
[Collection("EnvHome")]
public class CommandHandlerTests : IDisposable
{
    private readonly string _home;

    public CommandHandlerTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "klcmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("KIDSLOCKER_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("KIDSLOCKER_HOME", null);
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private static CommandHandler Make(ScheduleStore store, DateTime now)
    {
        var notifier = new Notifier(NullLogger<Notifier>.Instance, new AppConfig(), null);
        var enforcer = new Enforcer(NullLogger<Enforcer>.Instance, notifier, live: false);
        return new CommandHandler(store, enforcer, () => now);
    }

    // 2024-01-01 is a Monday; 2024-01-05 a Friday.
    private static readonly DateTime MonMorning = new(2024, 1, 1, 10, 0, 0);

    [Fact]
    public void Pause_1h30_Is90Minutes()
    {
        var store = new ScheduleStore();
        Make(store, MonMorning).Handle("/pause 1h30");
        Assert.Equal(MonMorning.AddMinutes(90), store.Get().PauseUntil);
    }

    [Fact]
    public void Pause_BareNumber_IsHours()
    {
        var store = new ScheduleStore();
        Make(store, MonMorning).Handle("/pause 2");
        Assert.Equal(MonMorning.AddHours(2), store.Get().PauseUntil);
    }

    [Fact]
    public void Pause_HugeNumber_CappedAtAWeek_NoOverflow()
    {
        var store = new ScheduleStore();
        Make(store, MonMorning).Handle("/pause 99999999999m");
        Assert.Equal(MonMorning.AddMinutes(7 * 24 * 60), store.Get().PauseUntil);
    }

    [Fact]
    public void Allow_MultipleIntervalsWithSpace_AllSaved()
    {
        var store = new ScheduleStore();
        // Exactly how /schedule and the GUI print it — space after the comma.
        Make(store, MonMorning).Handle("/allow пн 07:00-12:00, 14:00-23:00");
        var mon = store.Get().Allowed["mon"];
        Assert.Equal(2, mon.Count);
        Assert.Equal("14:00", mon[1].From);
        Assert.Equal("23:00", mon[1].To);
    }

    [Fact]
    public void Extra_DayAllowedToMidnight_AddsNothing_AndSaysSo()
    {
        var friNight = new DateTime(2024, 1, 5, 22, 0, 0);
        var store = new ScheduleStore();
        store.Update(s => s.Allowed["fri"] = new List<Interval> { new("07:00", "24:00") });
        var reply = Make(store, friNight).Handle("/extra 60");
        Assert.Empty(store.Get().Extras);
        Assert.Contains("нечего", reply);
    }

    [Fact]
    public void Extra_AddsIntervalFromLastAllowed()
    {
        var monNight = new DateTime(2024, 1, 1, 22, 0, 0);
        var store = new ScheduleStore();
        store.Update(s => s.Allowed["mon"] = new List<Interval> { new("07:00", "23:00") });
        var reply = Make(store, monNight).Handle("/extra 60");
        var extras = store.Get().Extras;
        var e = Assert.Single(extras);
        Assert.Equal("2024-01-01", e.Date);
        Assert.Equal("23:00", e.From);
        Assert.Equal("24:00", e.To);
        Assert.Contains("Добавил", reply);
    }
}
