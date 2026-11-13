using System.Diagnostics;
using System.Runtime.Versioning;
using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Notifications;
using KidsLocker.Service.Scheduling;
using KidsLocker.Service.Setup;
using KidsLocker.Service.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace KidsLocker.Service;

/// <summary>
/// Основной цикл контроля. Каждые несколько секунд перечитывает расписание, проверяет,
/// не подкручивали ли часы или часовой пояс, и — если сейчас запрещённое время —
/// принудительно выключает компьютер. Работает внутри службы LocalSystem, а также в режимах <c>--console</c>/<c>--dry-run</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EnforcementWorker : BackgroundService
{
    private readonly ILogger<EnforcementWorker> _log;
    private readonly ScheduleStore _store;
    private readonly Enforcer _enforcer;
    private readonly Notifier _notifier;
    private readonly AppConfig _config;

    private bool? _wasBlocked;
    private DateTime _lastWallUtc;
    private long _lastStamp;
    private long _lastHardenStamp = Stopwatch.GetTimestamp();
    private readonly TrustedClock _clock;
    private Task? _refreshTask;
    private Task? _hardeningTask;
    private string? _observedZone;
    private bool _corruptAlerted;

    public EnforcementWorker(
        ILogger<EnforcementWorker> log,
        ScheduleStore store,
        Enforcer enforcer,
        Notifier notifier,
        AppConfig config,
        TrustedClock clock)
    {
        _log = log;
        _store = store;
        _enforcer = enforcer;
        _notifier = notifier;
        _config = config;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _notifier.Info($"Контроль KidsLocker запущен. Режим: {(_enforcer.IsLive ? "боевой" : "тестовый")}, интервал: {_config.PollSeconds} с.");

        if (SystemBootMode.IsSafeMode)
            _notifier.Alert($"Внимание: система загружена в БЕЗОПАСНОМ режиме ({SystemBootMode.Describe}). Проверяю время и расписание; при отсутствии доверенного времени работа запрещена.");

        _lastWallUtc = DateTime.UtcNow;
        _lastStamp = Stopwatch.GetTimestamp();

        await _clock.InitializeAsync(stoppingToken);
        _notifier.Info(_clock.UtcNow is null
            ? "Не удалось подтвердить время через оба источника. При включённом контроле работа запрещена."
            : $"Время подтверждено: {_clock.UtcNow:O}");

        var period = TimeSpan.FromSeconds(Math.Clamp(_config.PollSeconds, 3, 120));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_clock.RefreshDue && (_refreshTask is null || _refreshTask.IsCompleted))
                    _refreshTask = RefreshClockAsync(stoppingToken);
                try { await TickAsync(stoppingToken); }
                catch (Exception ex) { _log.LogError(ex, "Enforcement tick failed"); }

                try { await Task.Delay(period, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            if (_refreshTask is not null)
                try { await _refreshTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task RefreshClockAsync(CancellationToken ct)
    {
        try { await _clock.RefreshAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _notifier.Info("Не удалось обновить время: " + ex.Message); }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        // Вспомогательные шаги обёрнуты каждый в свою защиту: сбой любого из них (например, файл
        // расписания сделали только для чтения и запись при откате подмены бросает исключение) НИКОГДА
        // не должен пропустить критичный путь «оценить и выключить» ниже — именно так контроль
        // тихо отключился бы.
        try
        {
            if (_store.SyncAndDetectTamper())
                _notifier.Alert("Файл расписания был изменён вручную — восстановил прежний. Проверьте /status.");
        }
        catch (Exception ex) { _log.LogError(ex, "Schedule tamper check failed"); }

        Schedule schedule;
        try
        {
            schedule = _store.Get();
        }
        catch (Exception ex)
        {
            // Хранилище не должно здесь бросать исключения, но если бросит — безопасно блокируем всё
            // (Enabled, без разрешённых интервалов), а не ослабляем контроль и не прерываем такт.
            _log.LogError(ex, "Schedule load failed; blocking until it can be read");
            schedule = new Schedule { Enabled = true };
        }

        if (_store.ScheduleFileCorrupt && !_corruptAlerted)
        {
            _corruptAlerted = true;
            _notifier.Alert("Файл расписания повреждён и не читается — временно блокирую ВСЁ время. Откройте пульт и сохраните расписание заново.");
        }

        try { DetectClockJump(); } catch (Exception ex) { _log.LogError(ex, "Clock-jump check failed"); }
        try { DetectTimeZoneChange(); } catch (Exception ex) { _log.LogError(ex, "Time-zone check failed"); }

        var utc = _clock.UtcNow;
        var eval = TrustedSchedule.Evaluate(schedule, utc);

        if (eval.Blocked)
        {
            try
            {
                if (_wasBlocked != true)
                {
                    using var alertCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    alertCts.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        await _notifier.AlertAsync($"Работа запрещена: {eval.Reason}. Выключаю компьютер.", alertCts.Token);
                    }
                    catch (Exception ex) { _notifier.Info("Уведомление перед выключением не доставлено: " + ex.Message); }
                }
            }
            finally { _enforcer.ShutdownNow(eval.Reason); }
        }
        else if (_wasBlocked == true)
        {
            _notifier.Info("Снова наступило разрешённое время.");
        }

        _wasBlocked = eval.Blocked;

        if (_hardeningTask is null || _hardeningTask.IsCompleted)
            _hardeningTask = Task.Run(SelfHeal, ct);
    }

    /// <summary>
    /// Периодически (когда работает как установленная служба) заново утверждает задачи планировщика
    /// и регистрацию в безопасном режиме: пересоздаёт всё удалённое и предупреждает родителя. Вместе
    /// с задачей-стражем, которая поднимает службу, это значит, что для разборки защиты нужно убрать
    /// все её части быстрее этого цикла — куда сложнее, чем удалить что-то одно.
    /// </summary>
    private void SelfHeal()
    {
        if (!WindowsServiceHelpers.IsWindowsService()) return; // при запуске разработчиком или в консоли задачи и реестр не трогаем
        if (Stopwatch.GetElapsedTime(_lastHardenStamp) < TimeSpan.FromMinutes(5)) return;

        _lastHardenStamp = Stopwatch.GetTimestamp();
        try
        {
            var restored = Installer.EnsureHardening(dryRun: !_enforcer.IsLive);
            if (!string.IsNullOrEmpty(restored))
                _notifier.Alert($"Кто-то удалял защиту ({restored}) — восстановил.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Self-heal failed");
        }
    }

    /// <summary>Сравнивает приращение системных часов с монотонным таймером, чтобы заметить подкрутку часов.</summary>
    private void DetectClockJump()
    {
        var nowUtc = DateTime.UtcNow;
        var nowStamp = Stopwatch.GetTimestamp();

        var monotonic = TimeSpan.FromSeconds((nowStamp - _lastStamp) / (double)Stopwatch.Frequency);
        var wall = nowUtc - _lastWallUtc;
        var drift = wall - monotonic;

        // QPC в Windows учитывает сон. Перевод часов влияет только на уведомление,
        // а разрешение работы всегда вычисляется по доверенному UTC.
        if (_wasBlocked.HasValue && Math.Abs(drift.TotalSeconds) > 120)
            _notifier.Alert($"Системное время изменено примерно на {drift.TotalMinutes:+0;-0} мин.");

        _lastWallUtc = nowUtc;
        _lastStamp = nowStamp;
    }

    private void DetectTimeZoneChange()
    {
        TimeZoneInfo.ClearCachedData();
        var zone = TimeZoneInfo.Local.Id;
        if (_observedZone is not null && _observedZone != zone)
            _notifier.Alert($"Часовой пояс Windows изменён: {_observedZone} → {zone}. Пояс расписания остаётся прежним.");
        _observedZone = zone;
    }
}
