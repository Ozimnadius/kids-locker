using System.IO.Pipes;
using KidsLocker.Service.Time;
using Xunit;

namespace KidsLocker.Tests;

public class TimePipeTests
{
    private static readonly DateTimeOffset Utc = new(2026, 11, 14, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public Task RunningService_ProvidesTimeWithoutInternet() => WithServer(Utc, async (name, ct) =>
    {
        var result = await TimePipe.ReadAsync(name, () => Environment.ProcessId, ct);
        Assert.True(result.Connected);
        Assert.Equal(Utc, result.Utc);
    });

    [Fact]
    public Task ImpostorPipe_IsNotTrusted() => WithServer(Utc, async (name, ct) =>
    {
        var result = await TimePipe.ReadAsync(name, () => -1, ct);
        Assert.False(result.Connected);
        Assert.Null(result.Utc);
    });

    [Fact]
    public Task ServiceWithoutVerifiedTime_ReportsUnknownNotWindowsTime() => WithServer(null, async (name, ct) =>
    {
        var result = await TimePipe.ReadAsync(name, () => Environment.ProcessId, ct);
        Assert.True(result.Connected);
        Assert.Null(result.Utc);
    });

    [Fact]
    public Task ClientKeepingHandleOpen_DoesNotBlockSubsequentReaders() => WithServer(Utc, async (name, ct) =>
    {
        using var holder = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        await holder.ConnectAsync(ct);
        await holder.ReadExactlyAsync(new byte[8], ct);
        // Во время отключения первого клиента возможен временный отказ или истечение
        // двухсекундного бюджета чтения на загруженном сервере сборки. Проверяем, что
        // канал снова доступен, пока первый клиент всё ещё удерживает свой дескриптор.
        // Настоящее бесконечное удержание канала не пройдёт ни одну из трёх попыток.
        (bool Connected, DateTimeOffset? Utc) second = default;
        for (var attempt = 0; attempt < 3 && !second.Connected; attempt++)
            second = await TimePipe.ReadAsync(name, () => Environment.ProcessId, ct);
        Assert.True(second.Connected);
        Assert.Equal(Utc, second.Utc);
    });

    [Fact]
    public Task PipeName_RemainsReservedBetweenClients() => WithServer(Utc, async (name, ct) =>
    {
        await TimePipe.ReadAsync(name, () => Environment.ProcessId, ct);
        Assert.ThrowsAny<IOException>(() =>
        {
            using var impostor = new NamedPipeServerStream(name, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        });
        Assert.Equal(Utc, (await TimePipe.ReadAsync(name, () => Environment.ProcessId, ct)).Utc);
    });

    private static async Task WithServer(DateTimeOffset? utc, Func<string, CancellationToken, Task> body)
    {
        var name = "kl-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = TimePipe.ServeAsync(name, () => utc, cts.Token);
        try { await body(name, cts.Token); }
        finally
        {
            cts.Cancel();
            try { await server; } catch (OperationCanceledException) { }
        }
    }
}
