using System.Diagnostics;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Setup;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KidsLocker.Tests;

public class SafeModeReliabilityTests
{
    [Fact]
    public void ShutdownProcessFailure_UsesNativeFallback()
    {
        var nativeCalled = false;
        NativeShutdown.Execute(() => false, () => { nativeCalled = true; return true; });
        Assert.True(nativeCalled);
    }

    [Fact]
    public void BothShutdownMethodsFail_ReportsFailure()
    {
        Assert.Throws<InvalidOperationException>(() => NativeShutdown.Execute(() => false, () => false));
    }

    [Fact]
    public void LoggingFailure_DoesNotPreventShutdown()
    {
        var called = false;
        var enforcer = new Enforcer(new BrokenLogger<Enforcer>(), true, _ => called = true);
        try { enforcer.ShutdownNow("Тест"); } catch (InvalidOperationException) { }
        Assert.True(called);
    }

    [Fact]
    public void SafeBootWrongValue_IsRepairedInBothModes()
    {
        var values = new Dictionary<string, string?> { ["Minimal"] = "не Service" };
        SafeBootRegistration.Ensure(mode => values.GetValueOrDefault(mode), mode => values[mode] = "Service");
        Assert.Equal("Service", values["Minimal"]);
        Assert.Equal("Service", values["Network"]);
    }

    [Fact]
    public void SafeBootWriteFailure_CannotReportSuccessfulRegistration()
    {
        Assert.Throws<InvalidOperationException>(() => SafeBootRegistration.Ensure(_ => null, _ => { }));
    }

    [Fact]
    public void ProcessWithOpenOutputPipe_DoesNotBypassTimeout()
    {
        // Прямой запуск лёгкого процесса исключает загрузку PowerShell.
        // Без ограничения чтение открытого stdout заняло бы около 30 секунд;
        // запас до 10 секунд учитывает нагрузку сервера сборки и завершение дерева.
        var ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
        var timer = Stopwatch.StartNew();
        var result = ProcessRunner.Run(ping, "-n 31 -w 1000 127.0.0.1", 150);
        Assert.Equal(-1, result.code);
        Assert.Contains("150 мс", result.output);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"Ожидание заняло {timer.Elapsed}");
    }

    private sealed class BrokenLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex, Func<TState, Exception?, string> format)
            => throw new InvalidOperationException("Журнал событий недоступен в безопасном режиме");
    }
}
