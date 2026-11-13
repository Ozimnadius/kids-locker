using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using KidsLocker.Service.Notifications;

namespace KidsLocker.Service.Enforcement;

/// <summary>
/// Выполняет действие контроля — немедленное принудительное выключение.
/// Флаг <c>live</c> разделяет боевой режим и «сухой» прогон: тот же код можно гонять на
/// машине разработчика в режиме <c>--dry-run</c>, не выключая её.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Enforcer
{
    private readonly ILogger<Enforcer> _log;
    private readonly bool _live;
    private readonly Action<string> _shutdown;

    public Enforcer(ILogger<Enforcer> log, bool live, Action<string>? shutdown = null)
    {
        _log = log;
        _live = live;
        _shutdown = shutdown ?? NativeShutdown.Force;
    }

    public bool IsLive => _live;

    /// <summary>Немедленно принудительно выключить ПК (или записать намерение в режиме <c>--dry-run</c>).</summary>
    public void ShutdownNow(string reason)
    {
        if (!_live)
        {
            _log.LogWarning("[ТЕСТ] Компьютер был бы выключен. Причина: {Reason}", reason);
            return;
        }

        try { _log.LogWarning("Принудительное выключение. Причина: {Reason}", reason); } catch { }
        try { _shutdown("KidsLocker: " + reason); }
        catch (Exception ex)
        {
            OperationalLog.Write("Ошибка выключения: " + ex.Message);
            throw;
        }
    }
}
