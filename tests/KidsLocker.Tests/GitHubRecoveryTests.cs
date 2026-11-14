using System.Security.Cryptography;
using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using KidsLocker.Service.Setup;
using Xunit;

namespace KidsLocker.Tests;

public class GitHubRecoveryTests
{
    [Fact]
    public void Repair_PreservesOpenMainFileAndRemainsEnabledAfterFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "kl-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var main = Path.Combine(root, "KidsLocker.exe");
            File.WriteAllText(main, "исправная основная программа");
            var store = new RecoveryStore(main, Path.Combine(root, "Recovery"));
            store.Publish();
            File.Delete(store.BackupPath);
            var stopped = false;
            // Такой дескриптор позволяет читать, но запрещает перезапись и удаление, как открытый exe.
            using var opened = new FileStream(main, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Throws<IOException>(() => GitHubRecovery.Repair(store, main, () => { }, () => stopped = true,
                () => throw new IOException("Временный сбой настройки службы")));
            Assert.True(stopped);
            Assert.True(store.Enabled);
            Assert.True(store.BackupMatches());
            var retried = false;
            GitHubRecovery.Repair(store, main, () => { }, () => throw new Exception("Исправную копию не останавливаем"), () => retried = true);
            Assert.True(retried);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DownloadTask_DoesNotDependOnEitherExecutable()
    {
        var method = typeof(Installer).GetMethod("GitHubTaskXml", BindingFlags.NonPublic | BindingFlags.Static)!;
        var doc = XDocument.Parse((string)method.Invoke(null, null)!);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(GitHubRecovery.PowerShellPath, doc.Descendants(ns + "Command").Single().Value);
        Assert.Contains(GitHubRecovery.ScriptPath, doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("S-1-5-18", doc.Descendants(ns + "UserId").Single().Value);
        Assert.Equal("PT1M", doc.Descendants(ns + "Interval").Single().Value);
        Assert.Single(doc.Descendants(ns + "BootTrigger"));
    }

    [Fact]
    public async Task BootstrapScript_WorksInWindowsPowerShellWithPinnedHashAndCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "kl-script-" + Guid.NewGuid().ToString("N"));
        try
        {
            GitHubRecovery.WriteFiles(root, "0.5.0");
            var script = Path.Combine(root, "GitHubRecovery.ps1");
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(script).Take(3).ToArray());
            var start = new ProcessStartInfo(GitHubRecovery.PowerShellPath)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(AppContext.BaseDirectory, "GitHubRecoveryScriptTests.ps1"), "-Source", script }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { process.Kill(entireProcessTree: true); throw; }
            Assert.True(process.ExitCode == 0, (await output) + (await error));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("0.5.0+abc123", "0.5.0")]
    [InlineData("0.5.0-rc.1+abc", "0.5.0-rc.1")]
    public void ReleaseVersion_PinsInstalledVersion(string value, string expected) =>
        Assert.Equal(expected, GitHubRecovery.ReleaseVersion(value));

    [Theory]
    [InlineData("latest")]
    [InlineData("../other")]
    [InlineData("0.5.0/other")]
    [InlineData("0.5.0\n")]
    public void InvalidVersion_CannotChangeDownloadAddress(string value) =>
        Assert.Throws<ArgumentException>(() => GitHubRecovery.ReleaseVersion(value));

    [Fact]
    public void DownloadedRepair_RequiresCurrentManifestAndMatchingExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "kl-github-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var exe = Path.Combine(root, "download.exe");
            File.WriteAllText(exe, "проверенный файл");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe)));
            var store = new RecoveryStore(exe, Path.Combine(root, "Recovery"));
            store.Publish();
            Assert.True(GitHubRecovery.MayRepair(store, exe, hash));
            var download = Path.Combine(root, "staged.exe");
            File.Copy(exe, download);
            Assert.False(GitHubRecovery.MayRepair(store, exe, new string('0', 64)));
            File.AppendAllText(exe, "подмена");
            Assert.False(GitHubRecovery.MayRepair(store, exe, hash));
            Assert.False(GitHubRecovery.MayRepair(store, download, hash)); // не откатывает существующий другой exe
            store.Disable();
            Assert.False(GitHubRecovery.MayRepair(store, exe, hash));
        }
        finally { Directory.Delete(root, true); }
    }
}
