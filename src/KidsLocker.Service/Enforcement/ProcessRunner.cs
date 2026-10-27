using System.Diagnostics;

namespace KidsLocker.Service.Enforcement;

/// <summary>
/// Единый запуск внешней программы: скрытое окно, перехват stdout/stderr, ожидание с
/// таймаутом. Общий код для установщика (sc.exe, schtasks, reg, icacls) и восстановления
/// службы, чтобы не держать две почти одинаковые обёртки над <see cref="Process"/>.
/// </summary>
internal static class ProcessRunner
{
    public static (int code, string output) Run(string file, string arguments, int timeoutMs = 30000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "");
            // Читаем потоки ДО WaitForExit: иначе большой вывод может переполнить буфер и привести к взаимной блокировке.
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            p.WaitForExit(timeoutMs);
            return (p.ExitCode, (so + se).Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
