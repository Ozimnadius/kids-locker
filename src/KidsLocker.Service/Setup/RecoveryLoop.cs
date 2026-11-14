namespace KidsLocker.Service.Setup;

internal sealed class RecoveryLoop : IDisposable
{
    private readonly ITimer _timer;
    private readonly Action _recover;
    private readonly Action<Exception> _report;
    private int _busy, _stopped;

    public RecoveryLoop(Action recover, Action<Exception> report, TimeProvider? time = null)
    {
        _recover = recover; _report = report;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Tick(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    private void Tick()
    {
        if (Volatile.Read(ref _stopped) != 0 || Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            if (Volatile.Read(ref _stopped) == 0) _recover();
        }
        catch (Exception ex) { try { _report(ex); } catch { } }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose() { Volatile.Write(ref _stopped, 1); _timer.Dispose(); }
}
