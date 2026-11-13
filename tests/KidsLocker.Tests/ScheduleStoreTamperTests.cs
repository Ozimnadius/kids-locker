using KidsLocker.Service.Scheduling;
using Xunit;

namespace KidsLocker.Tests;

/// <summary>
/// Exercises the tamper-detection path. Points KIDSLOCKER_HOME at a throwaway temp dir so
/// the real %ProgramData% is never touched.
/// </summary>
[Collection("EnvHome")]
public class ScheduleStoreTamperTests : IDisposable
{
    [Fact]
    public void LegacyPauseWithOffset_IsConvertedToMoscow()
    {
        Directory.CreateDirectory(KidsLocker.Service.Config.AppPaths.RootDir);
        // UTC+14 в старом JSON не должен интерпретироваться через текущий пояс Windows.
        File.WriteAllText(KidsLocker.Service.Config.AppPaths.ScheduleFile,
            "{\"LastTimeZoneId\":\"UTC\",\"PauseUntil\":\"2026-11-14T22:00:00+14:00\"}");
        var schedule = new ScheduleStore().Get();
        Assert.Equal(new DateTime(2026, 11, 14, 11, 0, 0), schedule.PauseUntil);
        Assert.Equal(DateTimeKind.Unspecified, schedule.PauseUntil!.Value.Kind);
    }
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

    [Fact]
    public void CorruptFile_BlocksEverything_AndIsNotOverwritten()
    {
        File.WriteAllText(ScheduleFile, "{ broken json ");
        var store = new ScheduleStore();

        var s = store.Get();
        Assert.True(store.ScheduleFileCorrupt);
        // Blocked all day rather than relaxed to the permissive default.
        Assert.True(ScheduleEvaluator.IsBlocked(s, new DateTime(2024, 1, 1, 12, 0, 0)));
        // The corrupt file is preserved (recoverable), not replaced with a default schedule.
        Assert.Equal("{ broken json ", File.ReadAllText(ScheduleFile));
    }
}
