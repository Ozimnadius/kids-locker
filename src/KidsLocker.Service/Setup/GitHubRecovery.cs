using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KidsLocker.Service.Setup;

internal static class GitHubRecovery
{
    public const string TaskName = "KidsLocker-GitHubRecovery";
    public static string DirectoryPath => Path.GetDirectoryName(ExecutableRecovery.Store.BackupPath)!;
    public static string ScriptPath => Path.Combine(DirectoryPath, "GitHubRecovery.ps1");
    public static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    public static string Arguments => $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ScriptPath}\"";
    private static string Version => ReleaseVersion(typeof(GitHubRecovery).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);

    internal static string ReleaseVersion(string version)
    {
        var value = version.Split('+')[0];
        if (!Regex.IsMatch(value, @"\A[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Некорректная версия релиза");
        return value;
    }

    internal static bool MayRepair(RecoveryStore store, string exe, string expectedHash)
    {
        if (!store.Enabled || !File.Exists(exe) || !Regex.IsMatch(expectedHash, "^[0-9a-fA-F]{64}$")) return false;
        if (!string.Equals(File.ReadAllText(store.ManifestPath).Trim(), expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
        if (File.Exists(store.InstalledPath) && !Matches(store.InstalledPath, expectedHash)) return false;
        return Matches(exe, expectedHash);
    }

    private static bool Matches(string exe, string expectedHash)
    {
        using var stream = File.OpenRead(exe);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    internal static void Repair(RecoveryStore store, string source, Action ensureMain, Action stopBackup, Action finish)
    {
        store.RestoreFrom(source);
        ensureMain();
        if (!store.BackupMatches())
        {
            stopBackup();
            store.EnsureBackup();
        }
        // Не отключаем манифест и не удаляем основную службу: сбой допускает новую попытку.
        finish();
    }

    internal static string ScriptText()
    {
        using var stream = typeof(GitHubRecovery).Assembly.GetManifestResourceStream("KidsLocker.GitHubRecovery.ps1")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static void Publish() => WriteFiles(DirectoryPath, Version);

    internal static void WriteFiles(string directory, string version)
    {
        Directory.CreateDirectory(directory);
        AtomicText(Path.Combine(directory, "GitHubRecovery.ps1"), ScriptText(), new UTF8Encoding(true));
        AtomicText(Path.Combine(directory, "github.json"), JsonSerializer.Serialize(new { Version = ReleaseVersion(version) }), new UTF8Encoding(false));
    }

    public static bool EnsureFiles()
    {
        var metadata = Path.Combine(DirectoryPath, "github.json");
        var expected = JsonSerializer.Serialize(new { Version });
        if (File.Exists(ScriptPath) && File.ReadAllText(ScriptPath) == ScriptText()
            && File.Exists(metadata) && File.ReadAllText(metadata) == expected) return false;
        Publish();
        return true;
    }

    private static void AtomicText(string path, string text, Encoding encoding)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, encoding); File.Move(temporary, path, overwrite: true); }
        finally { File.Delete(temporary); }
    }

    public static void TryStart()
    {
        var store = ExecutableRecovery.Store;
        if (!store.Enabled || !File.Exists(ScriptPath)) return;
        if (File.Exists(store.InstalledPath) && File.Exists(store.BackupPath)) return;
        using var process = Process.Start(new ProcessStartInfo(PowerShellPath, Arguments)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
    }
}
