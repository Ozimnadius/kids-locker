using System.Runtime.Versioning;
using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Gui;
using KidsLocker.Service.Notifications;
using KidsLocker.Service.Scheduling;
using KidsLocker.Service.Telegram;
using KidsLocker.Service.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace KidsLocker.Service;

[SupportedOSPlatform("windows")]
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { return MainCore(args); }
        catch (Exception ex)
        {
            OperationalLog.Write("Ошибка запуска: " + ex);
            WindowsEventLog.TryWrite("Ошибка запуска KidsLocker: " + ex.Message);
            return 1;
        }
    }

    private static int MainCore(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var a = args ?? Array.Empty<string>();
        bool Has(string flag) => a.Any(x => string.Equals(x, flag, StringComparison.OrdinalIgnoreCase));

        if (Has("--diagnose"))
        {
            var report = Diagnostics.Collect();
            var path = ValueAfter(a, "--diagnose");
            if (path is null) Console.WriteLine(report);
            else File.WriteAllText(path, report);
            return 0;
        }

        // Разовые служебные режимы для установщика и задач планировщика.
        if (Has("--install-eventsource")) { WindowsEventLog.EnsureSource(); return 0; }
        if (Has("--enforce-once")) { EnforceOnce(a).GetAwaiter().GetResult(); return 0; }
        if (Has("--guard")) { Guard(a).GetAwaiter().GetResult(); return 0; }
        if (Has("--test-telegram")) { return TestTelegram(a).GetAwaiter().GetResult(); }
        if (Has("--telegram-updates")) { return TelegramUpdates(a).GetAwaiter().GetResult(); }

        // Хост службы (как служба Windows или --console для отладки).
        if (WindowsServiceHelpers.IsWindowsService() || Has("--console"))
        {
            RunHost(a);
            return 0;
        }

        // Двойной клик / интерактивный запуск без флагов → GUI-пульт родителя.
        return RunGui();
    }

    // WPF загружается только здесь: службе и служебным флагам пульт не нужен.
    private static int RunGui() => GuiApp.Run();

    private static void RunHost(string[] a)
    {
        OperationalLog.Write($"Запуск службы/консоли. Режим Windows: {SystemBootMode.Describe}; версия: {typeof(Program).Assembly.GetName().Version}");
        var config = AppConfig.Load();
        var live = ComputeLive(a);

        var builder = Host.CreateApplicationBuilder(a);
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceControl.ServiceName);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss ");
        // В безопасном режиме необязательный провайдер журнала событий не должен сорвать старт хоста.
        if (WindowsServiceHelpers.IsWindowsService() && !SystemBootMode.IsSafeMode)
            builder.Logging.AddEventLog(s => { s.SourceName = WindowsEventLog.Source; s.LogName = WindowsEventLog.LogName; });

        TelegramClient? telegram = null;
        if (config.IsTelegramConfigured)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.TelegramLongPollSeconds + 20) };
            telegram = new TelegramClient(config.BotToken, http);
        }

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<ScheduleStore>();
        builder.Services.AddSingleton(_ => InternetTimeSource.CreateHttpClient());
        builder.Services.AddSingleton<IInternetTimeSource>(sp => new InternetTimeSource(sp.GetRequiredService<HttpClient>()));
        builder.Services.AddSingleton<TrustedClock>();
        builder.Services.AddSingleton(sp => new Notifier(sp.GetRequiredService<ILogger<Notifier>>(), config, telegram));
        builder.Services.AddSingleton(sp => new Enforcer(sp.GetRequiredService<ILogger<Enforcer>>(), live));
        builder.Services.AddSingleton(sp => new CommandHandler(sp.GetRequiredService<ScheduleStore>(), sp.GetRequiredService<Enforcer>(),
            () => TrustedSchedule.LocalNow(sp.GetRequiredService<ScheduleStore>().Get(), sp.GetRequiredService<TrustedClock>().UtcNow)));
        builder.Services.AddHostedService<TimePipeServer>();
        builder.Services.AddHostedService<EnforcementWorker>();
        builder.Services.AddHostedService<StopAlertService>();

        if (telegram is not null)
        {
            builder.Services.AddSingleton(telegram);
            builder.Services.AddHostedService<TelegramBot>();
        }

        builder.Build().Run();
    }

    // --- общие помощники для CLI-режимов ---

    private static bool ComputeLive(string[] a)
    {
        var forceDry = a.Any(x => x.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        var forceLive = a.Any(x => x.Equals("--live", StringComparison.OrdinalIgnoreCase));
        return !forceDry && (WindowsServiceHelpers.IsWindowsService() || forceLive);
    }

    private static (Notifier notifier, HttpClient? http) BuildNotifier(AppConfig cfg, ILoggerFactory lf)
    {
        HttpClient? http = null;
        TelegramClient? tg = null;
        if (cfg.IsTelegramConfigured)
        {
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            tg = new TelegramClient(cfg.BotToken, http);
        }
        return (new Notifier(lf.CreateLogger<Notifier>(), cfg, tg), http);
    }

    private static string? ValueAfter(string[] a, string flag)
    {
        for (var i = 0; i < a.Length - 1; i++)
            if (a[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return a[i + 1].StartsWith("--") ? null : a[i + 1];
        return null;
    }

    private static async Task EnforceOnce(string[] a)
    {
        AppPaths.EnsureRoot();
        var cfg = AppConfig.Load();
        var live = ComputeLive(a);

        using var lf = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss "));
        var (notifier, http) = BuildNotifier(cfg, lf);

        var store = new ScheduleStore();
        var enforcer = new Enforcer(lf.CreateLogger<Enforcer>(), live);

        var schedule = store.Load();
        var utc = schedule.Enabled ? await TrustedTimeReader.GetUtcAsync(startup: true) : null;
        var eval = TrustedSchedule.Evaluate(schedule, utc);
        lf.CreateLogger("enforce-once").LogInformation("Решение: {Decision}. {Reason}. Режим: {Mode}",
            eval.Blocked ? "запрет" : "разрешение", eval.Reason, live ? "боевой" : "тестовый");

        if (eval.Blocked)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await notifier.AlertAsync($"Работа запрещена: {eval.Reason}. Выключаю компьютер.", budget.Token); }
            finally { enforcer.ShutdownNow(eval.Reason); }
        }

        http?.Dispose();
    }

    private static async Task Guard(string[] a)
    {
        var cfg = AppConfig.Load();
        using var lf = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss "));
        var (notifier, http) = BuildNotifier(cfg, lf);

        switch (ServiceControl.GetState())
        {
            case ServiceControl.State.Running:
                break;
            case ServiceControl.State.Stopped:
                if (ServiceControl.TryStart())
                    await notifier.AlertAsync("Служба KidsLocker была остановлена — запустил заново.");
                else
                    await notifier.AlertAsync("Служба KidsLocker остановлена и не запускается — проверьте ПК.");
                break;
            case ServiceControl.State.Missing:
                await notifier.AlertAsync("Служба KidsLocker была удалена — восстанавливаю.");
                if (!ServiceControl.TryRecreate())
                    await notifier.AlertAsync("Не удалось восстановить службу KidsLocker — проверьте ПК.");
                break;
            case ServiceControl.State.Other:
                break;
        }

        http?.Dispose();
    }

    private static async Task<int> TestTelegram(string[] a)
    {
        var cfgPath = ValueAfter(a, "--test-telegram");
        AppConfig cfg;
        if (!string.IsNullOrEmpty(cfgPath))
        {
            if (!File.Exists(cfgPath)) { Console.WriteLine($"Файл не найден: {cfgPath}"); return 2; }
            try
            {
                cfg = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(
                    File.ReadAllText(cfgPath),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppConfig();
            }
            catch (Exception ex) { Console.WriteLine("Не удалось прочитать конфиг: " + ex.Message); return 2; }
        }
        else
        {
            cfg = AppConfig.Load();
        }

        if (!cfg.IsTelegramConfigured) { Console.WriteLine("❌ Нет токена или chat_id."); return 2; }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var client = new TelegramClient(cfg.BotToken, http);

        var me = await client.GetMeAsync(CancellationToken.None);
        if (me is null) { Console.WriteLine("❌ Токен неверный или нет сети (getMe не прошёл)."); return 1; }
        Console.WriteLine($"✅ Токен рабочий, бот @{me}");

        var (ok, detail) = await client.SendMessageResultAsync(cfg.ParentChatId, "KidsLocker: проверка связи ✅", CancellationToken.None);
        if (ok) { Console.WriteLine($"✅ Сообщение отправлено в chat_id={cfg.ParentChatId}."); return 0; }
        Console.WriteLine($"❌ Не удалось отправить сообщение: {detail}");
        return 1;
    }

    private static async Task<int> TelegramUpdates(string[] a)
    {
        var cfgPath = ValueAfter(a, "--telegram-updates");
        if (string.IsNullOrEmpty(cfgPath) || !File.Exists(cfgPath)) { Console.WriteLine("Не передан файл с токеном."); return 2; }

        AppConfig cfg;
        try
        {
            cfg = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(
                File.ReadAllText(cfgPath),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppConfig();
        }
        catch (Exception ex) { Console.WriteLine("Конфиг: " + ex.Message); return 2; }

        if (string.IsNullOrWhiteSpace(cfg.BotToken)) { Console.WriteLine("❌ Нет токена."); return 2; }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var client = new TelegramClient(cfg.BotToken, http);

        var me = await client.GetMeAsync(CancellationToken.None);
        if (me is null) { Console.WriteLine("❌ Токен неверный или нет сети."); return 1; }
        Console.WriteLine($"Бот @{me}. Кто ему писал:");

        var updates = await client.GetUpdatesAsync(0, 0, CancellationToken.None);
        var seen = new HashSet<long>();
        foreach (var u in updates)
        {
            var chat = u.Message?.Chat;
            if (chat is null || !seen.Add(chat.Id)) continue;
            Console.WriteLine($"   chat_id={chat.Id}   {chat.DisplayName}");
        }
        if (seen.Count == 0) { Console.WriteLine("Пусто. Напишите боту любое сообщение и повторите."); return 1; }
        return 0;
    }
}
