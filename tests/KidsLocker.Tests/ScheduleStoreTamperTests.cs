using KidsLocker.Service.Scheduling;
using Xunit;

namespace KidsLocker.Tests;

/// <summary>
/// Exercises the tamper-detection path. Points KIDSLOCKER_HOME at a throwaway temp dir so
/// the real %ProgramData% is never touched.
/// </summary>
public class ScheduleStoreTamperTests : IDisposable
{
    private readonly string _home;

    public ScheduleStoreTamperTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "kltest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("KIDSLOCKER_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("KIDSLOCKER_HOME", null);
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private string ScheduleFile => Path.Combine(_home, "schedule.json");

    [Fact]
    public void FirstLoad_CreatesFile_NoTamper()
    {
        var store = new ScheduleStore();
        _ = store.Get();
        Assert.True(File.Exists(ScheduleFile));
        Assert.False(store.SyncAndDetectTamper());
    }

    [Fact]
    public void LegitimateUpdate_IsNotTamper()
    {
        var store = new ScheduleStore();
        store.Update(s => s.Enabled = false);
        Assert.False(store.SyncAndDetectTamper());
        Assert.False(store.Get().Enabled);
    }

    [Fact]
    public void ExternalEdit_IsDetectedAndReverted()
    {
        var store = new ScheduleStore();
        var original = store.Get();
        Assert.True(original.Enabled);

        // Simulate an admin editing the file directly to disable enforcement.
        File.WriteAllText(ScheduleFile, "{ \"Enabled\": false, \"Allowed\": {}, \"Extras\": [] }");

        Assert.True(store.SyncAndDetectTamper());          // detected
        Assert.True(store.Get().Enabled);                  // reverted in memory
        Assert.False(store.SyncAndDetectTamper());         // file restored, now consistent
    }

    [Fact]
    public void ExternalDeletion_IsDetectedAndRestored()
    {
        var store = new ScheduleStore();
        _ = store.Get();
        File.Delete(ScheduleFile);

        Assert.True(store.SyncAndDetectTamper());
        Assert.True(File.Exists(ScheduleFile));
    }
}
