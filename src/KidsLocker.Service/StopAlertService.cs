using System.Runtime.Versioning;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Notifications;
using Microsoft.Extensions.Hosting;

namespace KidsLocker.Service;

/// <summary>
/// Шлёт алерт в Telegram, когда службу остановили не обычным выключением Windows и не сама
/// программа — то есть когда контроль намеренно остановил кто-то. Работает в stop-пути хоста
/// (StopAsync дожидаются), поэтому алерт успевает уйти до выхода процесса. Принудительное
/// убийство (taskkill /f) этот путь пропускает, но задача-хранитель всё равно сообщит об этом
/// в течение минуты.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StopAlertService : IHostedService
{
    private readonly Notifier _notifier;

    public StopAlertService(Notifier notifier) => _notifier = notifier;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (SystemBootMode.IsSystemShuttingDown) return; // ОС выключается (в т.ч. плановое выключение на ночь)
            if (StopMarker.ConsumeIsExpected()) return;       // службу остановила сама программа
            await _notifier.AlertAsync("Контроль KidsLocker выключен: службу остановили.");
        }
        catch { /* stop-путь не должен бросать исключения */ }
    }
}
