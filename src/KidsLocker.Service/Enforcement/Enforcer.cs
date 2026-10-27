using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

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

    public Enforcer(ILogger<Enforcer> log, bool live)
    {
        _log = log;
        _live = live;
    }

    public bool IsLive => _live;

    /// <summary>Немедленно принудительно выключить ПК (или записать намерение в режиме <c>--dry-run</c>).</summary>
    public void ShutdownNow(string reason)
    {
        if (!_live)
        {
            _log.LogWarning("[DRY-RUN] Would shut down now. Reason: {Reason}", reason);
            return;
        }

        _log.LogWarning("Forcing shutdown. Reason: {Reason}", reason);
        NativeShutdown.Force("KidsLocker: плановое выключение");
    }
}
