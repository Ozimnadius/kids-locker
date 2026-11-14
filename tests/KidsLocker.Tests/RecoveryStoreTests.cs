using KidsLocker.Service.Setup;
using Xunit;

namespace KidsLocker.Tests;

public sealed class RecoveryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kl-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly RecoveryStore _store;

    public RecoveryStoreTests()
    {
        _store = new RecoveryStore(Path.Combine(_root, "installed", "KidsLocker.exe"), Path.Combine(_root, "data", "Recovery"));
        Directory.CreateDirectory(Path.GetDirectoryName(_store.InstalledPath)!);
        File.WriteAllText(_store.InstalledPath, "версия один");
    }

    [Fact]
    public void DeletedInstallation_IsRestoredBeforeStartingService()
    {
        _store.Publish();
        Directory.Delete(Path.GetDirectoryName(_store.InstalledPath)!, true);
        var started = false;
        Assert.True(_store.Recover(() =>
        {
            Assert.Equal("версия один", File.ReadAllText(_store.InstalledPath));
            started = true;
        }));
        Assert.True(started);
    }

    [Fact]
    public void ExistingExecutable_IsNeverOverwrittenByOldBackup()
    {
        _store.Publish();
        File.WriteAllText(_store.InstalledPath, "версия два");
        Assert.False(_store.Recover(() => { }));
        Assert.Equal("версия два", File.ReadAllText(_store.InstalledPath));
    }

    [Fact]
    public void Update_ReplacesBackupWithNewVersion()
    {
        _store.Publish();
        _store.Disable();
        File.WriteAllText(_store.InstalledPath, "версия два");
        _store.Publish();
        File.Delete(_store.InstalledPath);
        Assert.True(_store.Recover(() => { }));
        Assert.Equal("версия два", File.ReadAllText(_store.InstalledPath));
    }

    [Fact]
    public void DisabledRecovery_DoesNotUndoUninstall()
    {
        _store.Publish();
        _store.Disable();
        File.Delete(_store.InstalledPath);
        Assert.False(_store.Recover(() => throw new Exception("Служба не должна запускаться")));
        Assert.False(File.Exists(_store.InstalledPath));
        Assert.False(_store.EnsureBackup());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrCorruptBackup_DoesNotPublishExecutable(bool corrupt)
    {
        _store.Publish();
        File.Delete(_store.InstalledPath);
        if (corrupt) File.WriteAllText(_store.BackupPath, "повреждённая копия");
        else File.Delete(_store.BackupPath);
        Assert.ThrowsAny<IOException>(() => _store.Recover(() => throw new Exception("Служба не должна запускаться")));
        Assert.False(File.Exists(_store.InstalledPath));
    }

    [Fact]
    public void MissingBackup_CanBeRepairedOnlyFromMatchingInstalledVersion()
    {
        _store.Publish();
        File.Delete(_store.BackupPath);
        Assert.True(_store.EnsureBackup());
        Assert.False(_store.EnsureBackup());
        File.Delete(_store.BackupPath);
        File.WriteAllText(_store.InstalledPath, "непроверенная версия");
        Assert.ThrowsAny<IOException>(() => _store.EnsureBackup());
        Assert.False(File.Exists(_store.BackupPath));
    }

    [Fact]
    public void Recovery_PreservesSettingsAndRetriesServiceStart()
    {
        var config = Path.Combine(_root, "data", "config.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "настройки и пароль");
        var mode = Path.Combine(_root, "data", ".service_mode");
        File.WriteAllText(mode, "dry");
        _store.Publish();
        File.Delete(_store.InstalledPath);
        Assert.Throws<InvalidOperationException>(() => _store.Recover(() => throw new InvalidOperationException("Пока не запустилась")));
        var started = false;
        _store.Recover(() => started = true);
        Assert.True(started);
        Assert.Equal("настройки и пароль", File.ReadAllText(config));
        Assert.Equal("dry", File.ReadAllText(mode));
    }

    [Fact]
    public void CorruptManifest_DoesNotPublishOrStart()
    {
        _store.Publish();
        File.WriteAllText(_store.ManifestPath, "не хеш");
        File.Delete(_store.InstalledPath);
        Assert.ThrowsAny<IOException>(() => _store.Recover(() => throw new Exception("Запуск запрещён")));
        Assert.False(File.Exists(_store.InstalledPath));
    }

    [Fact]
    public void FailedUpdate_LeavesOldRecoveryDisabled()
    {
        _store.Publish();
        File.Delete(_store.InstalledPath);
        Assert.ThrowsAny<IOException>(() => _store.Publish());
        Assert.False(_store.Enabled);
        Assert.False(_store.Recover(() => throw new Exception("Старая версия не должна устанавливаться")));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
