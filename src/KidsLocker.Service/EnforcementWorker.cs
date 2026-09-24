using System.Diagnostics;
using System.Runtime.Versioning;
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
/// The steady-state enforcement loop. Every few seconds it re-reads the schedule, checks
/// for clock/time-zone tampering, and — if the current moment is a blocked window —
/// forces a shutdown. Runs inside the LocalSystem service and as <c>--console</c>/<c>--dry-run</c>.
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

        var period = TimeSpan.FromSeconds(Math.Clamp(_config.PollSeconds, 3, 120));

        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { _log.LogError(ex, "Enforcement tick failed"); }

            try { await Task.Delay(period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (_store.SyncAndDetectTamper())
            _notifier.Alert("Файл расписания был изменён вручную — восстановил прежний. Проверьте /status.");

        var schedule = _store.Get();
        DetectClockJump();
        DetectTimeZoneChange(schedule);

        var now = DateTime.Now;
        var eval = ScheduleEvaluator.Evaluate(schedule, now);

        if (eval.Blocked)
        {
            if (_wasBlocked != true)
            {
                var who = SessionQuery.ActiveConsoleUser();
                var whoTxt = who is null ? "" : $" (сеанс: {who})";
                _notifier.Alert($"Запрещённое время{whoTxt}. Выключаю компьютер. [{now:HH:mm}]");
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
    /// Periodically (as the installed service) re-assert the scheduled tasks and Safe Mode
    /// registration, recreating anything that was deleted and alerting the parent. Combined
    /// with the guardian task reviving the service, dismantling requires removing every piece
    /// faster than this cycle — much harder than deleting one thing.
    /// </summary>
    private void SelfHeal()
    {
        if (!WindowsServiceHelpers.IsWindowsService()) return; // never touch tasks/registry in dev/console runs
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

    /// <summary>Compare wall-clock delta against a monotonic timer to spot clock tampering.</summary>
    private void DetectClockJump()
    {
        var nowUtc = DateTime.UtcNow;
        var nowStamp = Stopwatch.GetTimestamp();

        var monotonic = TimeSpan.FromSeconds((nowStamp - _lastStamp) / (double)Stopwatch.Frequency);
        var wall = nowUtc - _lastWallUtc;
        var drift = wall - monotonic;

        if (_wasBlocked.HasValue && Math.Abs(drift.TotalSeconds) > 120)
            _notifier.Alert($"Системное время изменено примерно на {drift.TotalMinutes:+0;-0} мин.");

        _lastWallUtc = nowUtc;
        _lastStamp = nowStamp;
    }

    private void DetectTimeZoneChange(Schedule schedule)
    {
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
