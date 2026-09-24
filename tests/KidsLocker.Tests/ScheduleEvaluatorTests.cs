using KidsLocker.Service.Scheduling;
using Xunit;

namespace KidsLocker.Tests;

public class ScheduleEvaluatorTests
{
    // Reliable weekday anchors: 2024-01-01 is a Monday.
    private static readonly DateTime Mon = new(2024, 1, 1);
    private static readonly DateTime Sat = new(2024, 1, 6);

    private static Schedule OneDay(string day, params (string from, string to)[] intervals)
    {
        var s = new Schedule();
        s.Allowed[day] = intervals.Select(i => new Interval(i.from, i.to)).ToList();
        return s;
    }

    [Fact]
    public void WithinAllowed_NotBlocked()
    {
        var s = OneDay("mon", ("07:00", "23:00"));
        Assert.False(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(22)));   // 22:00
    }

    [Fact]
    public void OutsideAllowed_Blocked()
    {
        var s = OneDay("mon", ("07:00", "23:00"));
        Assert.True(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(23).AddMinutes(30))); // 23:30
        Assert.True(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(6)));                 // 06:00
    }

    [Fact]
    public void IntervalBoundaries_FromInclusive_ToExclusive()
    {
        var s = OneDay("mon", ("07:00", "23:00"));
        Assert.False(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(7)));   // 07:00 exactly -> allowed
        Assert.True(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(23)));   // 23:00 exactly -> blocked
    }

    [Fact]
    public void EndOfDay2400_AllowsUntilMidnight()
    {
        var s = OneDay("sat", ("09:00", "24:00"));
        Assert.False(ScheduleEvaluator.IsBlocked(s, Sat.AddHours(23).AddMinutes(59)));
    }

    [Fact]
    public void EmptyDay_BlockedAllDay()
    {
        var s = new Schedule(); // no entries at all
        Assert.True(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(12)));
    }

    [Fact]
    public void Disabled_NeverBlocked()
    {
        var s = OneDay("mon", ("07:00", "23:00"));
        s.Enabled = false;
        Assert.False(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(3))); // deep night, but disabled
    }

    [Fact]
    public void PauseUntil_SuppressesBlockingBeforeExpiry()
    {
        var s = OneDay("mon", ("07:00", "23:00"));
        s.PauseUntil = Mon.AddHours(23).AddMinutes(30);          // paused past bedtime start
        Assert.False(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(23).AddMinutes(15))); // still paused
        Assert.True(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(23).AddMinutes(45)));  // pause expired
    }

    [Fact]
    public void Extra_ForDate_ExtendsAllowed()
    {
        var s = OneDay("mon", ("07:00", "23:00"));
        var date = Mon.ToString("yyyy-MM-dd");
        s.Extras.Add(new DatedInterval(date, "23:00", "24:00"));
        Assert.False(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(23).AddMinutes(30))); // now allowed
        // Extra is date-scoped: the following Monday is unaffected.
        Assert.True(ScheduleEvaluator.IsBlocked(s, Mon.AddDays(7).AddHours(23).AddMinutes(30)));
    }

    [Fact]
    public void MultipleIntervals_GapIsBlocked()
    {
        var s = OneDay("mon", ("07:00", "12:00"), ("14:00", "23:00"));
        Assert.False(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(10)));  // morning window
        Assert.True(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(13)));   // lunch gap
        Assert.False(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(15)));  // afternoon window
    }

    [Fact]
    public void NextChange_FromAllowed_IsBlockStart()
    {
        var s = OneDay("mon", ("07:00", "23:00"));
        var next = ScheduleEvaluator.NextChange(s, Mon.AddHours(22));
        Assert.Equal(Mon.AddHours(23), next);
    }

    [Fact]
    public void DefaultSchedule_BlocksLateNight()
    {
        var s = Schedule.Default();
        // Monday 23:30 should be bedtime under the default (allowed 07:00-23:00 on Mon).
        Assert.True(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(23).AddMinutes(30)));
        Assert.False(ScheduleEvaluator.IsBlocked(s, Mon.AddHours(20)));
    }
}
