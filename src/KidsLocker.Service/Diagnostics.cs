using System.Diagnostics;
using System.Reflection;
using System.Text;
using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Notifications;
using KidsLocker.Service.Setup;
using Microsoft.Win32;

namespace KidsLocker.Service;

internal static class Diagnostics
{
    /// <summary>Только чтение: без установки, запуска службы, сети и выключения компьютера.</summary>
    public static string Collect()
    {
        var text = new StringBuilder();
        text.AppendLine("Диагностика KidsLocker");
        text.AppendLine($"Версия: {Assembly.GetExecutingAssembly().GetName().Version}");
        text.AppendLine($"Режим Windows: {SystemBootMode.Describe}");
        text.AppendLine("Служба: " + (ServiceControl.GetState() switch
        {
            ServiceControl.State.Running => "работает",
            ServiceControl.State.Stopped => "остановлена",
            ServiceControl.State.Missing => "не установлена",
            _ => "состояние неизвестно"
        }));
        text.AppendLine($"Установленный exe существует: {(File.Exists(Installer.InstalledExe) ? "да" : "нет")}");
        text.AppendLine($"Папка данных: {AppPaths.RootDir}");
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceControl.ServiceName}");
            text.AppendLine($"Команда службы: {key?.GetValue("ImagePath") ?? "не зарегистрирована"}");
            text.AppendLine($"Тип запуска: {key?.GetValue("Start") ?? "нет"} (2 — автоматический)");
            foreach (var mode in new[] { "Minimal", "Network" })
                text.AppendLine($"SafeBoot {mode}: {SafeBootRegistration.Read(mode) ?? "нет записи"} (ожидается Service)");
        }
        catch (Exception ex) { text.AppendLine("Не удалось прочитать реестр: " + ex.Message); }
        text.AppendLine("Последние записи приложения (дата Windows могла быть изменена):");
        foreach (var path in new[] { OperationalLog.FilePath + ".previous", OperationalLog.FilePath })
        {
            try { if (File.Exists(path)) foreach (var line in File.ReadLines(path).TakeLast(80)) text.AppendLine(line); }
            catch (Exception ex) { text.AppendLine("Не удалось прочитать журнал: " + ex.Message); }
        }
        foreach (var name in new[] { "System", "Application" })
        {
            try
            {
                using var log = new EventLog(name);
                var found = 0;
                // Ограничиваем просмотр, чтобы диагностическая команда не читала весь журнал за годы.
                for (var i = log.Entries.Count - 1; i >= Math.Max(0, log.Entries.Count - 2000) && found < 20; i--)
                {
                    var entry = log.Entries[i];
                    if (!entry.Source.Contains("KidsLocker", StringComparison.OrdinalIgnoreCase)
                        && entry.EntryType is not (EventLogEntryType.Error or EventLogEntryType.Warning)) continue;
                    var message = entry.Message;
                    if (!message.Contains("KidsLocker", StringComparison.OrdinalIgnoreCase)
                        && !entry.Source.Contains("KidsLocker", StringComparison.OrdinalIgnoreCase)) continue;
                    found++;
                    text.AppendLine($"{name} {entry.TimeGenerated:O} {entry.Source}: {message}");
                }
            }
            catch (Exception ex) { text.AppendLine($"Журнал {name} недоступен: {ex.Message}"); }
        }
        return OperationalLog.Redact(text.ToString());
    }
}
