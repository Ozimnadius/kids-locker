using KidsLocker.Service.Scheduling;
using Xunit;

namespace KidsLocker.Tests;

public class TimeParsingTests
{
    [Theory]
    [InlineData("07:00", 7, 0)]
    [InlineData("7:00", 7, 0)]
    [InlineData("23:30", 23, 30)]
    [InlineData("00:00", 0, 0)]
    public void Parse_ValidTimes(string input, int h, int m)
    {
        Assert.Equal(new TimeSpan(h, m, 0), TimeParsing.Parse(input));
    }

    [Fact]
    public void Parse_2400_IsEndOfDay()
    {
        Assert.Equal(TimeSpan.FromDays(1), TimeParsing.Parse("24:00"));
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("23:60")]
    [InlineData("24:30")]
    [InlineData("abc")]
    [InlineData("12")]
    [InlineData("")]
    public void Parse_Invalid_Throws(string input)
    {
        Assert.Throws<FormatException>(() => TimeParsing.Parse(input));
    }

    [Theory]
    [InlineData("07:00", "07:00")]
    [InlineData("24:00", "24:00")]
    [InlineData("9:05", "09:05")]
    public void Format_RoundTrips(string input, string expected)
    {
        Assert.Equal(expected, TimeParsing.Format(TimeParsing.Parse(input)));
    }
}
