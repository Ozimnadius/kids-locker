using KidsLocker.Service.Scheduling;
using KidsLocker.Service.Security;
using Xunit;

namespace KidsLocker.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Create_Then_Verify_Succeeds()
    {
        var (hash, salt) = PasswordHasher.Create("s3cret!");
        Assert.True(PasswordHasher.Verify("s3cret!", hash, salt));
    }

    [Fact]
    public void Verify_WrongPassword_Fails()
    {
        var (hash, salt) = PasswordHasher.Create("s3cret!");
        Assert.False(PasswordHasher.Verify("wrong", hash, salt));
    }

    [Fact]
    public void Verify_MissingHash_Fails()
    {
        Assert.False(PasswordHasher.Verify("x", null, null));
        Assert.False(PasswordHasher.Verify("x", "", ""));
    }

    [Fact]
    public void Salt_IsRandom_PerCall()
    {
        var a = PasswordHasher.Create("same");
        var b = PasswordHasher.Create("same");
        Assert.NotEqual(a.salt, b.salt);
        Assert.NotEqual(a.hash, b.hash);
    }
}

public class ScheduleTextTests
{
    [Fact]
    public void ParseIntervals_Single()
    {
        var r = ScheduleText.ParseIntervals("07:00-23:00");
        Assert.Single(r);
        Assert.Equal("07:00", r[0].From);
        Assert.Equal("23:00", r[0].To);
    }

    [Fact]
    public void ParseIntervals_Multiple_WithSpaces()
    {
        var r = ScheduleText.ParseIntervals("07:00-12:00, 14:00-23:00");
        Assert.Equal(2, r.Count);
        Assert.Equal("14:00", r[1].From);
    }

    [Fact]
    public void ParseIntervals_EndOfDay()
    {
        var r = ScheduleText.ParseIntervals("09:00-24:00");
        Assert.Single(r);
        Assert.Equal("24:00", r[0].To);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("23:00-07:00")] // inverted
    [InlineData("25:00-26:00")] // invalid times
    [InlineData("")]
    public void ParseIntervals_IgnoresBad(string input)
    {
        Assert.Empty(ScheduleText.ParseIntervals(input));
    }

    [Fact]
    public void Format_RoundTrips()
    {
        var text = "07:00-12:00, 14:00-23:00";
        var parsed = ScheduleText.ParseIntervals(text);
        Assert.Equal("07:00-12:00, 14:00-23:00", ScheduleText.Format(parsed));
    }
}
