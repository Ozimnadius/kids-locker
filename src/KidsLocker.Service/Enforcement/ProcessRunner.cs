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
            using var budget = new CancellationTokenSource(timeoutMs);
            // Синхронный ReadToEnd до WaitForExit делал таймаут фиктивным: зависший процесс
            // держал stdout открытым бесконечно. Ограничиваем и процесс, и чтение обоих потоков.
            var so = p.StandardOutput.ReadToEndAsync(budget.Token);
            var se = p.StandardError.ReadToEndAsync(budget.Token);
            try
            {
                Task.WhenAll(p.WaitForExitAsync(budget.Token), so, se)
                    .WaitAsync(budget.Token).GetAwaiter().GetResult();
                return (p.ExitCode, (so.Result + se.Result).Trim());
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, $"Команда не завершилась за {timeoutMs} мс");
            }
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
