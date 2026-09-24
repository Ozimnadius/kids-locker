using KidsLocker.Service.Enforcement;
using Xunit;

namespace KidsLocker.Tests;

[Collection("EnvHome")]
public class StopMarkerTests : IDisposable
{
    private readonly string _home;

    public StopMarkerTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "klstop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("KIDSLOCKER_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("KIDSLOCKER_HOME", null);
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public void NoMarker_IsNotExpected()
    {
        Assert.False(StopMarker.ConsumeIsExpected());
    }

    [Fact]
    public void WrittenMarker_IsExpected_Once()
    {
        StopMarker.WriteExpected();
        Assert.True(StopMarker.ConsumeIsExpected());   // fresh marker → expected
        Assert.False(StopMarker.ConsumeIsExpected());  // consumed → gone
    }
}
