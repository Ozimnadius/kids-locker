using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Notifications;
using KidsLocker.Service.Scheduling;
using KidsLocker.Service.Setup;
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
    private DateTime _lastHardenUtc = DateTime.UtcNow;
    private volatile bool _resumedSinceLastTick;
    private bool _corruptAlerted;

    public EnforcementWorker(
        ILogger<EnforcementWorker> log,
        ScheduleStore store,
        Enforcer enforcer,
        Notifier notifier,
        AppConfig config)
    {
        _log = log;
        _store = store;
        _enforcer = enforcer;
        _notifier = notifier;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _notifier.Info($"KidsLocker enforcement started. Mode={(_enforcer.IsLive ? "LIVE" : "DRY-RUN")}, poll={_config.PollSeconds}s");

        if (SystemBootMode.IsSafeMode)
            _notifier.Alert($"Внимание: система загружена в БЕЗОПАСНОМ режиме ({SystemBootMode.Describe}). Контроль активен, выключение работает.");

        _lastWallUtc = DateTime.UtcNow;
        _lastStamp = Stopwatch.GetTimestamp();

        // При выходе из сна/гибернации системные часы прыгают вперёд, а монотонный таймер стоял на месте,
        // и детектор скачков времени иначе принял бы это за «время изменили». Запоминаем пробуждение,
        // чтобы пропустить следующую проверку. По возможности: если подписаться на событие питания
        // здесь не удалось, поведение просто остаётся прежним (с тем же ложным алертом, что и раньше).
        try { SystemEvents.PowerModeChanged += OnPowerModeChanged; } catch { /* нет цикла обработки сообщений */ }

        var period = TimeSpan.FromSeconds(Math.Clamp(_config.PollSeconds, 3, 120));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(stoppingToken); }
                catch (Exception ex) { _log.LogError(ex, "Enforcement tick failed"); }

                try { await Task.Delay(period, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _resumedSinceLastTick = true;
    }

    private async Task TickAsync(CancellationToken ct)
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
        try { DetectTimeZoneChange(schedule); } catch (Exception ex) { _log.LogError(ex, "Time-zone check failed"); }

        var now = DateTime.Now;
        var eval = ScheduleEvaluator.Evaluate(schedule, now);

        if (eval.Blocked)
        {
            if (_wasBlocked != true)
            {
                var who = SessionQuery.ActiveConsoleUser();
                var whoTxt = who is null ? "" : $" (сеанс: {who})";
                // Доставляем алерт ДО того, как принудительное выключение оборвёт сеть, — но ограничиваем
                // ожидание, чтобы недоступный Telegram не откладывал выключение. AlertAsync сначала
                // синхронно пишет в локальный лог и журнал событий, так что эта запись сохранится в любом случае.
                using var alertCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                alertCts.CancelAfter(TimeSpan.FromSeconds(10));
                try { await _notifier.AlertAsync($"Запрещённое время{whoTxt}. Выключаю компьютер. [{now:HH:mm}]", alertCts.Token); }
                catch (Exception ex) { _log.LogError(ex, "Pre-shutdown alert failed"); }
            }
            _enforcer.ShutdownNow(eval.Reason);
        }
        else if (_wasBlocked == true)
        {
            _notifier.Info($"Allowed time resumed at {now:HH:mm}.");
        }

        _wasBlocked = eval.Blocked;

        SelfHeal();
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
        if (DateTime.UtcNow - _lastHardenUtc < TimeSpan.FromMinutes(5)) return;

        _lastHardenUtc = DateTime.UtcNow;
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

        // Только что вышли из сна/гибернации: разрыв — это реально прошедшее время, которое монотонный
        // таймер (Stopwatch/QPC) не учёл, а не смена часов. Пропускаем это сравнение и берём новую точку отсчёта.
        var justResumed = _resumedSinceLastTick;
        _resumedSinceLastTick = false;

        if (!justResumed && _wasBlocked.HasValue && Math.Abs(drift.TotalSeconds) > 120)
            _notifier.Alert($"Системное время изменено примерно на {drift.TotalMinutes:+0;-0} мин.");

        _lastWallUtc = nowUtc;
        _lastStamp = nowStamp;
    }

    private void DetectTimeZoneChange(Schedule schedule)
    {
        // TimeZoneInfo.Local (и DateTime.Now, по которому работает контроль) кэшируются на всё время
        // жизни процесса; без сброса этого кэша смена часового пояса не будет замечена — на неё не
        // отреагирует ни этот алерт, ни оценка расписания.
        TimeZoneInfo.ClearCachedData();
        var tz = TimeZoneInfo.Local.Id;
        if (string.IsNullOrEmpty(schedule.LastTimeZoneId))
        {
            _store.Update(s => s.LastTimeZoneId = tz);
            return;
        }
        if (!string.Equals(schedule.LastTimeZoneId, tz, StringComparison.Ordinal))
        {
            _notifier.Alert($"Часовой пояс изменён: {schedule.LastTimeZoneId} → {tz}.");
            _store.Update(s => s.LastTimeZoneId = tz);
        }
    }
}
