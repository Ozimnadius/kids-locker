using System.Diagnostics;
using System.Runtime.Versioning;

namespace KidsLocker.Service.Notifications;

/// <summary>
/// Тонкая обёртка «по возможности» над журналом событий Windows. Создание источника событий
/// требует прав администратора (делает установщик один раз); если источника нет, молча ничего
/// не делаем, чтобы ничего не падало при запуске без повышения прав на машине разработчика.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsEventLog
{
    public const string Source = "KidsLocker";
    public const string LogName = "Application";

    public static void TryWrite(string message, EventLogEntryType type = EventLogEntryType.Warning)
    {
        try
        {
            if (!EventLog.SourceExists(Source))
                return; // источник не зарегистрирован (нужны права администратора) — тихо пропускаем
            EventLog.WriteEntry(Source, message, type);
        }
        catch
        {
            // Сбои логирования никогда не должны влиять на контроль.
        }
    }

    /// <summary>Вызывается установщиком (с повышенными правами), чтобы один раз зарегистрировать источник событий.</summary>
    public static void EnsureSource()
    {
        if (!EventLog.SourceExists(Source))
            EventLog.CreateEventSource(new EventSourceCreationData(Source, LogName));
    }
}
