using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using KidsLocker.Service.Enforcement;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace KidsLocker.Service.Time;

/// <summary>Только чтение времени из памяти службы; файловый кеш не переживает перезагрузку.</summary>
internal static class TimePipe
{
    public const string Name = "KidsLocker.TrustedTime.v1";

    internal static async Task ServeAsync(string name, Func<DateTimeOffset?> now, CancellationToken ct)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 64, security);
        while (!ct.IsCancellationRequested)
        {
            await pipe.WaitForConnectionAsync(ct);
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(TimeSpan.FromSeconds(1));
                var bytes = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(bytes, now()?.UtcTicks ?? 0);
                await pipe.WriteAsync(bytes, budget.Token);
                // Disconnect отбрасывает непрочитанные данные. Ждём короткое подтверждение чтения,
                // не позволяя клиенту удерживать соединение и мешать остальным бесконечно.
                await pipe.ReadExactlyAsync(new byte[1], budget.Token);
            }
            catch (IOException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            finally { pipe.Disconnect(); }
        }
    }

    internal static async Task<(bool Connected, DateTimeOffset? Utc)> ReadAsync(
        string name, Func<int?> expectedPid, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(budget.Token);
            // Одного имени канала недостаточно: чужой процесс может занять его до службы.
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != expectedPid()) return (false, null);
            var bytes = new byte[8];
            await pipe.ReadExactlyAsync(bytes, budget.Token);
            await pipe.WriteAsync(new byte[] { 1 }, budget.Token);
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes);
            return (true, ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
        { return (false, null); }
    }

    public static Task<(bool Connected, DateTimeOffset? Utc)> ReadServiceAsync(CancellationToken ct) =>
        ReadAsync(Name, ServicePid, ct);

    private static int? ServicePid()
    {
        try
        {
            using var service = new ServiceController(ServiceControl.ServiceName);
            return QueryServiceStatusEx(service.ServiceHandle, 0, out var status,
                Marshal.SizeOf<ServiceStatusProcess>(), out _) && status.CurrentState == 4
                ? (int)status.ProcessId : null;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
            CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(SafeHandle service, int level,
        out ServiceStatusProcess status, int size, out int needed);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}

internal sealed class TimePipeServer(TrustedClock clock, ILogger<TimePipeServer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TimePipe.ServeAsync(TimePipe.Name, () => clock.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                try { log.LogWarning(ex, "Не удалось выдать время через локальный канал"); } catch { }
                try { await Task.Delay(1000, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
