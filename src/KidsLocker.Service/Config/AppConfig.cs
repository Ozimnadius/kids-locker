using System.Text.Json;
using KidsLocker.Service.Notifications;

namespace KidsLocker.Service.Config;

/// <summary>
/// Рабочая конфигурация с секретами (токен Telegram, id чата родителя).
/// Хранится в config.local.json — никогда не попадает в систему контроля версий.
/// </summary>
public sealed class AppConfig
{
    public string BotToken { get; set; } = "";

    /// <summary>Id чата родителя в Telegram. Выполняются только сообщения с этого id.</summary>
    public long ParentChatId { get; set; }

    /// <summary>Как часто (в секундах) цикл контроля заново оценивает расписание.</summary>
    public int PollSeconds { get; set; } = 10;

    /// <summary>Таймаут долгого опроса Telegram в секундах.</summary>
    public int TelegramLongPollSeconds { get; set; } = 30;

    /// <summary>Хэш PBKDF2 мастер-пароля родителя, закрывающего вход в административный пульт (base64).</summary>
    public string? MasterPasswordHash { get; set; }

    /// <summary>Соль для <see cref="MasterPasswordHash"/> (base64).</summary>
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
    /// Загрузить конфиг. Возвращает true при чистой загрузке или при настоящем первом запуске (файла нет);
    /// возвращает FALSE, когда файл есть, но не читается или не разбирается, — вызывающий, который
    /// собирается сохранять (пульт), в этом случае не должен перезаписывать файл, иначе сотрёт хэш
    /// мастер-пароля и токен. При неудаче <paramref name="config"/> — новый конфиг по умолчанию, а
    /// проблема записывается в журнал событий.
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

        // Никогда не теряем секреты, уже лежащие на диске, только потому, что этот экземпляр собран после
        // неудачного или пустого Load: сохраняем записанные хэш мастер-пароля и токен, если сами их не задаём.
        // (Путь «сохранить Telegram» в пульте пароля не несёт, поэтому без этого сохранение после
        // нечитаемого Load стёрло бы его.)
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
        catch { /* существующий не читается — просто пишем то, что есть */ }

        // Атомарная запись: падение или пропадание питания посреди записи не должны оставить обрезанный, неразбираемый конфиг.
        var json = JsonSerializer.Serialize(this, JsonOpts);
        var tmp = AppPaths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
    }
}
