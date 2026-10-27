using KidsLocker.Service.Config;
using Xunit;

namespace KidsLocker.Tests;

[Collection("EnvHome")]
public class AppConfigTests : IDisposable
{
    private readonly string _home;

    public AppConfigTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "klcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("KIDSLOCKER_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("KIDSLOCKER_HOME", null);
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public void Save_FromInstanceWithoutPassword_PreservesStoredPassword()
    {
        // Parent set a master password and Telegram once.
        new AppConfig { MasterPasswordHash = "h", MasterPasswordSalt = "s", BotToken = "tok", ParentChatId = 42 }.Save();

        // A later "save Telegram" carries no password (as the GUI's does, and as a failed Load would).
        new AppConfig { BotToken = "newtok", ParentChatId = 99 }.Save();

        var loaded = AppConfig.Load();
        Assert.Equal("h", loaded.MasterPasswordHash);   // password survived
        Assert.Equal("s", loaded.MasterPasswordSalt);
        Assert.Equal("newtok", loaded.BotToken);        // token updated
        Assert.Equal(99, loaded.ParentChatId);
    }

    [Fact]
    public void TryLoad_CorruptFile_ReturnsFalse()
    {
        File.WriteAllText(AppPaths.ConfigFile, "{ this is not valid json ");
        Assert.False(AppConfig.TryLoad(out var cfg, out var error));
        Assert.NotNull(error);
        Assert.False(cfg.HasMasterPassword);
    }

    [Fact]
    public void TryLoad_NoFile_ReturnsTrueFirstRun()
    {
        Assert.True(AppConfig.TryLoad(out _, out var error));
        Assert.Null(error);
    }
}
