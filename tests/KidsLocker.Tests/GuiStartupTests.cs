using System.Windows.Controls;
using System.Windows.Threading;
using KidsLocker.Service.Gui;
using KidsLocker.Service.Scheduling;
using Xunit;

namespace KidsLocker.Tests;

[Collection("EnvHome")]
public class GuiStartupTests
{
    [Fact]
    public async Task InitialStatus_AfterLogin_UpdatesOnWindowThread()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var previousHome = Environment.GetEnvironmentVariable("KIDSLOCKER_HOME");
            var home = Path.Combine(Path.GetTempPath(), "kl-gui-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("KIDSLOCKER_HOME", home);
                new ScheduleStore().Save(Schedule.Default());
                var app = GuiApp.CreateApplication();
                try
                {
                    // После закрытия диалога пароля и до Application.Run контекста WPF нет.
                    Assert.Null(SynchronizationContext.Current);
                    foreach (var fail in new[] { false, true })
                    {
                        SynchronizationContext? captured = null;
                        async Task<DateTimeOffset?> ReadUtc()
                        {
                            captured = SynchronizationContext.Current;
                            // Старую ошибку обнаруживаем без падения всего процесса тестов.
                            if (captured is not DispatcherSynchronizationContext)
                                throw new InvalidOperationException("Нет контекста окна");
                            await Task.Yield();
                            if (fail) throw new IOException("Проверочная ошибка сети");
                            return null;
                        }

                        var page = new SchedulePage(new Shell(), ReadUtc);
                        var details = (TextBlock)page.FindName("NowDetails");
                        var expected = fail ? "Проверочная ошибка сети" : "Время не подтверждено через интернет";
                        var frame = new DispatcherFrame();
                        var deadline = System.Diagnostics.Stopwatch.StartNew();
                        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                        poll.Tick += (_, _) =>
                        {
                            if (details.Text == expected || deadline.Elapsed > TimeSpan.FromSeconds(5))
                                frame.Continue = false;
                        };
                        poll.Start();
                        try { Dispatcher.PushFrame(frame); }
                        finally { poll.Stop(); }
                        Assert.IsType<DispatcherSynchronizationContext>(captured);
                        Assert.Equal(expected, details.Text);
                    }
                }
                finally { app.Shutdown(); }
                result.SetResult();
            }
            catch (Exception ex) { result.SetException(ex); }
            finally
            {
                Environment.SetEnvironmentVariable("KIDSLOCKER_HOME", previousHome);
                if (Directory.Exists(home)) Directory.Delete(home, true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(20));
        thread.Join();
    }

    private sealed class Shell : IShell
    {
        public Task<Exception?> RunAsync(string text, Func<Task> work, bool critical) => throw new NotSupportedException();
        public void RefreshServiceStatus() { }
    }
}
