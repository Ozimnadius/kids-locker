using System.Security.Cryptography;

namespace KidsLocker.Service.Setup;

/// <summary>Файловая часть восстановления. Вызывается под общей блокировкой установки.</summary>
internal sealed class RecoveryStore(string installedPath, string recoveryDirectory)
{
    public string InstalledPath => installedPath;
    public string BackupPath => Path.Combine(recoveryDirectory, "KidsLocker.Recovery.exe");
    public string ManifestPath => Path.Combine(recoveryDirectory, "enabled.sha256");
    public bool Enabled => File.Exists(ManifestPath);

    public void Publish()
    {
        Disable();
        var hash = Hash(InstalledPath);
        CopyVerified(InstalledPath, BackupPath, hash, overwrite: true);
        var temporary = ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, hash);
            File.Move(temporary, ManifestPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public void Disable()
    {
        try { File.Delete(ManifestPath); }
        catch (DirectoryNotFoundException) { } // первая установка или уже удалённые данные
    }

    public bool EnsureBackup()
    {
        if (!Enabled) return false;
        var hash = ReadHash();
        if (File.Exists(BackupPath) && Hash(BackupPath) == hash) return false;
        CopyVerified(InstalledPath, BackupPath, hash, overwrite: true);
        return true;
    }

    public bool Recover(Action ensureService)
    {
        if (!Enabled) return false;
        var restored = RestoreFrom(BackupPath);
        // Повторяем запуск и после неудачного старта: файл уже мог быть возвращён ранее.
        ensureService();
        return restored;
    }

    public bool RestoreFrom(string source)
    {
        if (!Enabled || File.Exists(InstalledPath)) return false;
        CopyVerified(source, InstalledPath, ReadHash(), overwrite: false);
        return true;
    }

    public bool BackupMatches() => Enabled && File.Exists(BackupPath) && Hash(BackupPath) == ReadHash();

    private string ReadHash()
    {
        var hash = File.ReadAllText(ManifestPath).Trim().ToUpperInvariant();
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new IOException("Манифест резервной копии повреждён.");
        return hash;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void CopyVerified(string source, string destination, string hash, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            if (Hash(temporary) != hash) throw new IOException("Контрольная сумма копии не совпала. Восстановление отменено.");
            // Опубликованный exe всегда полный. При восстановлении существующий файл не заменяем.
            // При обновлении резервный процесс мог только что освободить mutex, но ещё завершаться.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, destination, overwrite); break; }
                catch (IOException) when (overwrite && attempt < 19) { Thread.Sleep(250); }
            }
        }
        finally { File.Delete(temporary); }
    }
}
