namespace KidsLocker.Service.Time;

public interface IInternetTimeSource
{
    Task<DateTimeOffset?> GetUtcAsync(CancellationToken ct);
}

/// <summary>UTC из сети плюс монотонный таймер. Доверие не сохраняется между запусками процесса.</summary>
public sealed class TrustedClock(IInternetTimeSource source, TimeProvider? timer = null)
{
    private readonly TimeProvider _timer = timer ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private DateTimeOffset? _anchor;
    private long _anchorStamp;
    private long? _lastAttempt;

    public DateTimeOffset? UtcNow
    {
        get { lock (_gate) return Current(); }
    }

    public bool RefreshDue
    {
        get
        {
            lock (_gate) return _lastAttempt is null
                || _timer.GetElapsedTime(_lastAttempt.Value) >= TimeSpan.FromMinutes(15);
        }
    }

    private DateTimeOffset? Current()
    {
        if (_anchor is null) return null;
        var elapsed = _timer.GetElapsedTime(_anchorStamp);
        return elapsed < TimeSpan.Zero ? null : _anchor.Value + elapsed;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!await _refresh.WaitAsync(0, ct)) return;
        try
        {
            lock (_gate) _lastAttempt = _timer.GetTimestamp();
            var utc = await source.GetUtcAsync(ct);
            if (utc is null) return;
            lock (_gate)
            {
                var previous = Current();
                // Сбой сервиса времени не должен сдвигать уже подтверждённое расписание на часы.
                if (previous is not null && Math.Abs((utc.Value - previous.Value).TotalSeconds) > 120) return;
                _anchor = previous > utc ? previous : utc.Value.ToUniversalTime();
                _anchorStamp = _timer.GetTimestamp();
            }
        }
        finally { _refresh.Release(); }
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        // Ожидание нужно только до первого подтверждения времени. Успешный ответ
        // сразу возвращает управление проверке расписания, без остатка льготных минут.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5), _timer);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            while (UtcNow is null)
            {
                await RefreshAsync(budget.Token);
                if (UtcNow is not null) break;
                await Task.Delay(TimeSpan.FromSeconds(2), _timer, budget.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
}
