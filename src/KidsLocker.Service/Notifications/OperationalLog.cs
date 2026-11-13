using System.Diagnostics;
using System.Text.RegularExpressions;
using KidsLocker.Service.Config;

namespace KidsLocker.Service.Notifications;

/// <summary>Журнал для диагностики загрузки, в том числе без службы журнала событий Windows.</summary>
internal static class OperationalLog
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(AppPaths.RootDir, "service.log");

    public static string Redact(string text) => Regex.Replace(text, @"\b\d{5,}:[A-Za-z0-9_-]{20,}", "[токен скрыт]");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                AppPaths.EnsureRoot();
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".previous", overwrite: true);
                // Дата Windows только для диагностики; последовательность уточняют PID и монотонная отметка.
                File.AppendAllText(FilePath, $"{DateTimeOffset.UtcNow:O} PID={Environment.ProcessId} QPC={Stopwatch.GetTimestamp()} {Redact(message)}{Environment.NewLine}");
            }
        }
        catch { /* журнал никогда не должен прерывать контроль */ }
    }
}
