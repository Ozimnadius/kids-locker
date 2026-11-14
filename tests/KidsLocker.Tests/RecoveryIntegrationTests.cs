using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Linq;
using KidsLocker.Service.Setup;
using Xunit;

namespace KidsLocker.Tests;

public class RecoveryIntegrationTests
{
    [Fact]
    public void DirectoryPolicy_ReplacesOwnerAndAllWritePermissions()
    {
        var policy = RecoveryProtection.DirectoryPolicy();
        Assert.True(policy.AreAccessRulesProtected);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), policy.GetOwner(typeof(SecurityIdentifier)));
        var rules = policy.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.Equal(3, rules.Length);
        Assert.All(rules, rule => Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags));
        var users = Assert.Single(rules, rule => rule.IdentityReference.Value == "S-1-5-32-545");
        Assert.Equal(0, (int)(users.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions)));
    }

    [Fact]
    public void FailedBackupRepair_DoesNotAbortOtherHardeningChecks()
    {
        var restored = new List<string>();
        string? error = null;
        Installer.TryRepairComponent("резервная копия exe", () => throw new IOException("Нет места"), restored, message => error = message);
        Assert.Empty(restored);
        Assert.Contains("Нет места", error);
    }

    [Fact]
    public void RecoveryTask_RunsBackupIndependentlyAtBootAndEveryMinute()
    {
        var build = typeof(Installer).GetMethod("RecoveryTaskXml", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(build);
        var doc = XDocument.Parse((string)build.Invoke(null, null)!);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(ExecutableRecovery.Store.BackupPath, doc.Descendants(ns + "Command").Single().Value);
        Assert.NotEqual(Installer.InstalledExe, doc.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--recover", doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("S-1-5-18", doc.Descendants(ns + "UserId").Single().Value);
        Assert.Single(doc.Descendants(ns + "BootTrigger"));
        Assert.Equal("PT1M", doc.Descendants(ns + "Interval").Single().Value);
        Assert.Equal("false", doc.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
    }

    [Fact]
    public void InstallationGate_BlocksOtherThreadAndReleasesAfterMaintenance()
    {
        var name = "Local\\KidsLocker-Test-" + Guid.NewGuid().ToString("N");
        // Тесты запускаются без повышения прав; боевой mutex доступен только SYSTEM/администраторам.
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User!, MutexRights.FullControl, AccessControlType.Allow));
        bool TryFromOtherThread()
        {
            bool acquired = false;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { using var gate = InstallationGate.TryEnter(TimeSpan.Zero, name, security); acquired = gate is not null; }
                catch (Exception ex) { failure = ex; }
            });
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            if (failure is not null) throw failure;
            return acquired;
        }
        using (var owner = InstallationGate.TryEnter(TimeSpan.Zero, name, security))
        {
            Assert.NotNull(owner);
            Assert.False(TryFromOtherThread());
        }
        Assert.True(TryFromOtherThread());
    }

    [Fact]
    public void Uninstall_ConcurrentRecoverySkipsAndCannotResurrectAfterRelease()
    {
        var root = Path.Combine(Path.GetTempPath(), "kl-uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new RecoveryStore(Path.Combine(root, "KidsLocker.exe"), Path.Combine(root, "Recovery"));
        File.WriteAllText(store.InstalledPath, "версия");
        store.Publish();
        var name = "Local\\KidsLocker-Test-" + Guid.NewGuid().ToString("N");
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User!, MutexRights.FullControl, AccessControlType.Allow));
        var starts = 0;
        bool RunRecovery()
        {
            var result = false;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    result = ExecutableRecovery.RunOnce(store, () => starts++,
                        () => InstallationGate.TryEnter(TimeSpan.Zero, name, security));
                }
                catch (Exception ex) { error = ex; }
            });
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            if (error is not null) throw error;
            return result;
        }
        try
        {
            using (var installer = InstallationGate.TryEnter(TimeSpan.Zero, name, security))
            {
                Assert.NotNull(installer);
                File.Delete(store.InstalledPath);
                Assert.False(RunRecovery()); // не мешает временному отсутствию exe при обновлении
                store.Disable(); // штатное удаление
            }
            Assert.False(RunRecovery()); // запоздалый запуск оставшейся задачи
            Assert.Equal(0, starts);
            Assert.False(File.Exists(store.InstalledPath));
        }
        finally { Directory.Delete(root, true); }
    }
}
