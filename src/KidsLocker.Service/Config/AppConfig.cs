using System.Text.Json;
using KidsLocker.Service.Notifications;

namespace KidsLocker.Service.Config;

/// <summary>
/// Runtime configuration with secrets (Telegram token, parent chat id).
/// Stored in config.local.json — never committed to source control.
/// </summary>
public sealed class AppConfig
{
    public string BotToken { get; set; } = "";

    /// <summary>The parent's Telegram chat id. Only messages from this id are obeyed.</summary>
    public long ParentChatId { get; set; }

    /// <summary>How often (seconds) the enforcement loop re-evaluates the schedule.</summary>
    public int PollSeconds { get; set; } = 10;

    /// <summary>Telegram long-poll timeout in seconds.</summary>
    public int TelegramLongPollSeconds { get; set; } = 30;

    /// <summary>PBKDF2 hash of the parent's master password that gates the admin GUI (base64).</summary>
    public string? MasterPasswordHash { get; set; }

    /// <summary>Salt for <see cref="MasterPasswordHash"/> (base64).</summary>
    public string? MasterPasswordSalt { get; set; }

    public bool IsTelegramConfigured =>
        !string.IsNullOrWhiteSpace(BotToken) && ParentChatId != 0;

    public bool HasMasterPassword =>
        !string.IsNullOrEmpty(MasterPasswordHash) && !string.IsNullOrEmpty(MasterPasswordSalt);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static AppConfig Load()
    {
        TryLoad(out var config, out _);
        return config;
    }

    /// <summary>
    /// Load the config. Returns true on a clean load or a genuine first run (no file); returns FALSE
    /// when the file exists but can't be read or parsed — a caller that is about to save (the GUI)
    /// must not overwrite in that case, or it would wipe the master-password hash and token. On
    /// failure <paramref name="config"/> is a fresh default and the problem is written to the event log.
    /// </summary>
    public static bool TryLoad(out AppConfig config, out string? error)
    {
        error = null;
        if (!File.Exists(AppPaths.ConfigFile))
        {
            config = new AppConfig();
            return true;
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile), JsonOpts);
            if (parsed is not null)
            {
                config = parsed;
                return true;
            }
            error = "файл пуст или не является корректным JSON";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        WindowsEventLog.TryWrite($"Не удалось прочитать config.local.json ({error}). Настройки (токен, пароль) временно недоступны.");
        config = new AppConfig();
        return false;
    }

    public void Save()
    {
        AppPaths.EnsureRoot();

        // Never lose secrets already on disk just because this instance was built from a failed/empty
        // Load: keep the stored master-password hash and token when we aren't setting them ourselves.
        // (The GUI "save Telegram" path carries no password, so without this a save started after an
        // unreadable Load would erase it.)
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var existing = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile), JsonOpts);
                if (existing is not null)
                {
                    MasterPasswordHash ??= existing.MasterPasswordHash;
                    MasterPasswordSalt ??= existing.MasterPasswordSalt;
                    if (string.IsNullOrWhiteSpace(BotToken)) BotToken = existing.BotToken;
                    if (ParentChatId == 0) ParentChatId = existing.ParentChatId;
                }
            }
        }
        catch { /* can't read existing — just write what we have */ }

        // Atomic write: a crash or power loss mid-write must not leave a truncated, unparseable config.
        var json = JsonSerializer.Serialize(this, JsonOpts);
        var tmp = AppPaths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
    }
}
