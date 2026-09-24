using System.Text.Json;

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
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var json = File.ReadAllText(AppPaths.ConfigFile);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
            }
        }
        catch
        {
            // Fall through to defaults — a bad/absent config must not crash enforcement.
        }
        return new AppConfig();
    }

    public void Save()
    {
        AppPaths.EnsureRoot();
        File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}
