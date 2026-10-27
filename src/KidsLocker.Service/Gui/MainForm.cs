using System.Net.Http;
using System.Runtime.Versioning;
using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Scheduling;
using KidsLocker.Service.Setup;
using KidsLocker.Service.Telegram;

namespace KidsLocker.Service.Gui;

/// <summary>The parent's control panel: Telegram setup, install/uninstall, and schedule — all buttons.</summary>
[SupportedOSPlatform("windows")]
public sealed class MainForm : Form
{
    private AppConfig _config = AppConfig.Load();

    // Telegram tab
    private readonly TextBox _token = new();
    private readonly TextBox _chatId = new();
    private readonly Label _tgStatus = new();

    // Service tab
    private readonly Label _svcStatus = new();
    private readonly TextBox _log = new();

    // Schedule tab
    private static readonly string[] DayKeys = { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };
    private static readonly string[] DayNames = { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
    private readonly TextBox[] _days = new TextBox[7];
    private readonly CheckBox _enabled = new() { Text = "Контроль включён" };
    private readonly Label _schedStatus = new();

    public MainForm()
    {
        Text = "KidsLocker";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(600, 470);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildTelegramTab());
        tabs.TabPages.Add(BuildServiceTab());
        tabs.TabPages.Add(BuildScheduleTab());
        Controls.Add(tabs);

        LoadTelegram();
        LoadSchedule();
        RefreshServiceStatus();
        RefreshScheduleStatus();
    }

    // ---------------- Telegram tab ----------------

    private TabPage BuildTelegramTab()
    {
        var page = new TabPage("Telegram");

        page.Controls.Add(new Label { Text = "Токен бота (@BotFather):", Left = 16, Top = 20, Width = 200 });
        _token.Left = 16; _token.Top = 42; _token.Width = 550;
        page.Controls.Add(_token);

        page.Controls.Add(new Label { Text = "Ваш chat_id:", Left = 16, Top = 78, Width = 120 });
        _chatId.Left = 140; _chatId.Top = 74; _chatId.Width = 180;
        page.Controls.Add(_chatId);

        var detect = new Button { Text = "Определить автоматически", Left = 330, Top = 73, Width = 200 };
        detect.Click += OnDetectChatId;
        page.Controls.Add(detect);

        page.Controls.Add(new Label
        {
            Text = "Сначала напишите своему боту любое сообщение, потом нажмите «Определить».",
            Left = 16, Top = 104, Width = 560, ForeColor = Color.DimGray,
        });

        var test = new Button { Text = "Проверить связь", Left = 16, Top = 140, Width = 160 };
        test.Click += OnTestConnection;
        page.Controls.Add(test);

        var save = new Button { Text = "Сохранить", Left = 186, Top = 140, Width = 120 };
        save.Click += OnSaveTelegram;
        page.Controls.Add(save);

        _tgStatus.Left = 16; _tgStatus.Top = 184; _tgStatus.Width = 560; _tgStatus.Height = 90;
        page.Controls.Add(_tgStatus);
        return page;
    }

    private void LoadTelegram()
    {
        _token.Text = _config.BotToken;
        _chatId.Text = _config.ParentChatId != 0 ? _config.ParentChatId.ToString() : "";
    }

    private async void OnDetectChatId(object? sender, EventArgs e)
    {
        var token = _token.Text.Trim();
        if (token.Length == 0) { _tgStatus.Text = "Введите токен."; return; }

        _tgStatus.Text = "Определяю...";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var client = new TelegramClient(token, http);

            var me = await client.GetMeAsync(CancellationToken.None);
            if (me is null) { _tgStatus.Text = "Токен неверный или нет сети."; return; }

            var updates = await client.GetUpdatesAsync(0, 0, CancellationToken.None);
            var chats = updates.Select(u => u.Message?.Chat).Where(c => c is not null)
                .GroupBy(c => c!.Id).Select(g => g.First()!).ToList();

            if (chats.Count == 0)
            {
                _tgStatus.Text = $"Бот @{me}. Пока никто не писал — напишите боту сообщение и повторите.";
                return;
            }
            _chatId.Text = chats[0].Id.ToString();
            _tgStatus.Text = $"Бот @{me}. Найдено: " + string.Join("; ", chats.Select(c => $"{c.Id} {c.DisplayName}"));
        }
        catch (Exception ex) { _tgStatus.Text = "Ошибка: " + ex.Message; }
    }

    private async void OnTestConnection(object? sender, EventArgs e)
    {
        var token = _token.Text.Trim();
        if (token.Length == 0) { _tgStatus.Text = "Введите токен."; return; }
        if (!long.TryParse(_chatId.Text.Trim(), out var chatId) || chatId == 0) { _tgStatus.Text = "Укажите числовой chat_id (или нажмите «Определить»)."; return; }

        _tgStatus.Text = "Проверяю...";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var client = new TelegramClient(token, http);

            var me = await client.GetMeAsync(CancellationToken.None);
            if (me is null) { _tgStatus.Text = "Токен неверный или нет сети."; return; }

            var (ok, detail) = await client.SendMessageResultAsync(chatId, "KidsLocker: проверка связи ✅", CancellationToken.None);
            _tgStatus.Text = ok
                ? $"Успех! Бот @{me}, сообщение отправлено. Проверьте Telegram."
                : $"Бот @{me}, но сообщение не ушло: {detail}. Напишите боту сообщение и проверьте chat_id.";
        }
        catch (Exception ex) { _tgStatus.Text = "Ошибка: " + ex.Message; }
    }

    private void OnSaveTelegram(object? sender, EventArgs e)
    {
        var token = _token.Text.Trim();
        if (token.Length == 0) { _tgStatus.Text = "Введите токен."; return; }
        if (!long.TryParse(_chatId.Text.Trim(), out var chatId) || chatId == 0) { _tgStatus.Text = "Укажите числовой chat_id."; return; }

        try
        {
            var cfg = AppConfig.Load(); // keep password + other fields
            cfg.BotToken = token;
            cfg.ParentChatId = chatId;
            ServiceControl.ApplyWithReload(cfg.Save);
            _config = AppConfig.Load();
            _tgStatus.Text = "Сохранено. Служба перезапущена (если установлена).";
        }
        catch (Exception ex) { _tgStatus.Text = "Не удалось сохранить: " + ex.Message; }
    }

    // ---------------- Service tab ----------------

    private TabPage BuildServiceTab()
    {
        var page = new TabPage("Служба");

        _svcStatus.Left = 16; _svcStatus.Top = 18; _svcStatus.Width = 420; _svcStatus.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        page.Controls.Add(_svcStatus);

        var refresh = new Button { Text = "Обновить", Left = 456, Top = 14, Width = 110 };
        refresh.Click += (_, _) => RefreshServiceStatus();
        page.Controls.Add(refresh);

        var installDry = new Button { Text = "Установить (тест)", Left = 16, Top = 54, Width = 170 };
        installDry.Click += (_, _) => DoInstall(dryRun: true);
        page.Controls.Add(installDry);

        var installLive = new Button { Text = "Установить (боевой)", Left = 196, Top = 54, Width = 190 };
        installLive.Click += (_, _) => DoInstall(dryRun: false);
        page.Controls.Add(installLive);

        var uninstall = new Button { Text = "Удалить", Left = 396, Top = 54, Width = 170 };
        uninstall.Click += (_, _) => DoUninstall();
        page.Controls.Add(uninstall);

        page.Controls.Add(new Label
        {
            Text = "«Тест» — ничего не выключает, для проверки. «Боевой» — включает реальное выключение.",
            Left = 16, Top = 90, Width = 560, ForeColor = Color.DimGray,
        });

        _log.Left = 16; _log.Top = 118; _log.Width = 550; _log.Height = 300;
        _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.White;
        page.Controls.Add(_log);
        return page;
    }

    private void RefreshServiceStatus()
    {
        _svcStatus.Text = ServiceControl.GetState() switch
        {
            ServiceControl.State.Running => "Служба работает.",
            ServiceControl.State.Stopped => "Служба установлена, но остановлена.",
            ServiceControl.State.Missing => "Служба не установлена.",
            _ => "Состояние службы неизвестно.",
        };
    }

    private async void DoInstall(bool dryRun)
    {
        if (!dryRun)
        {
            var r = MessageBox.Show(
                "Включить реальное выключение компьютера по расписанию?\nСначала рекомендуется «Установить (тест)».",
                "KidsLocker", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
        }

        _log.Clear();
        AppendLog(dryRun ? "Установка в тестовом режиме..." : "Установка в боевом режиме...");
        try
        {
            await Task.Run(() => Installer.Install(dryRun, AppendLog));
            AppendLog("Готово.");
        }
        catch (Exception ex) { AppendLog("ОШИБКА: " + ex.Message); }
        RefreshServiceStatus();
    }

    private async void DoUninstall()
    {
        if (MessageBox.Show("Удалить KidsLocker полностью с этого ПК?", "KidsLocker",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        _log.Clear();
        AppendLog("Удаление...");
        try
        {
            await Task.Run(() => Installer.Uninstall(keepData: false, AppendLog));
            AppendLog("Готово.");
        }
        catch (Exception ex) { AppendLog("ОШИБКА: " + ex.Message); }
        RefreshServiceStatus();
    }

    private void AppendLog(string line)
    {
        if (_log.InvokeRequired) { _log.BeginInvoke((MethodInvoker)(() => AppendLog(line))); return; }
        _log.AppendText(line + Environment.NewLine);
    }

    // ---------------- Schedule tab ----------------

    private TabPage BuildScheduleTab()
    {
        var page = new TabPage("Расписание");

        page.Controls.Add(new Label { Text = "Разрешённое время по дням (напр. 07:00-23:00, 24:00 = полночь):", Left = 16, Top = 12, Width = 560 });

        for (var i = 0; i < 7; i++)
        {
            var top = 40 + i * 30;
            page.Controls.Add(new Label { Text = DayNames[i], Left = 16, Top = top + 3, Width = 40 });
            _days[i] = new TextBox { Left = 60, Top = top, Width = 300 };
            page.Controls.Add(_days[i]);
        }

        _enabled.Left = 380; _enabled.Top = 44; _enabled.Width = 180;
        page.Controls.Add(_enabled);

        var save = new Button { Text = "Сохранить расписание", Left = 380, Top = 74, Width = 190 };
        save.Click += (_, _) => SaveSchedule();
        page.Controls.Add(save);

        var pause = new Button { Text = "Пауза 2 часа", Left = 380, Top = 108, Width = 190 };
        pause.Click += (_, _) => WithReloadRefresh(st => st.Update(x => x.PauseUntil = DateTime.Now.AddHours(2)));
        page.Controls.Add(pause);

        var resume = new Button { Text = "Снять паузу", Left = 380, Top = 142, Width = 190 };
        resume.Click += (_, _) => WithReloadRefresh(st => st.Update(x => x.PauseUntil = null));
        page.Controls.Add(resume);

        var extra = new Button { Text = "+1 час сегодня", Left = 380, Top = 176, Width = 190 };
        extra.Click += (_, _) => AddExtraHour();
        page.Controls.Add(extra);

        var off = new Button { Text = "Выключить сейчас", Left = 380, Top = 210, Width = 190, ForeColor = Color.Firebrick };
        off.Click += (_, _) => ShutdownNow();
        page.Controls.Add(off);

        _schedStatus.Left = 16; _schedStatus.Top = 268; _schedStatus.Width = 350; _schedStatus.Height = 60;
        _schedStatus.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        page.Controls.Add(_schedStatus);

        var refresh = new Button { Text = "Обновить статус", Left = 16, Top = 320, Width = 160 };
        refresh.Click += (_, _) => RefreshScheduleStatus();
        page.Controls.Add(refresh);

        return page;
    }

    private void LoadSchedule()
    {
        var s = new ScheduleStore().Load();
        _enabled.Checked = s.Enabled;
        for (var i = 0; i < 7; i++)
            _days[i].Text = ScheduleText.Format(s.Allowed.GetValueOrDefault(DayKeys[i]));
    }

    private void SaveSchedule()
    {
        var s = new ScheduleStore().Load(); // preserve extras / pause / tz
        s.Enabled = _enabled.Checked;
        for (var i = 0; i < 7; i++)
            s.Allowed[DayKeys[i]] = ScheduleText.ParseIntervals(_days[i].Text);

        try
        {
            ServiceControl.ApplyWithReload(() => new ScheduleStore().Save(s));
            RefreshScheduleStatus();
            MessageBox.Show("Расписание сохранено.", "KidsLocker", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show("Ошибка: " + ex.Message, "KidsLocker"); }
    }

    private void WithReloadRefresh(Action<ScheduleStore> op)
    {
        try
        {
            ServiceControl.ApplyWithReload(() => op(new ScheduleStore()));
            RefreshScheduleStatus();
        }
        catch (Exception ex) { MessageBox.Show("Ошибка: " + ex.Message, "KidsLocker"); }
    }

    private void AddExtraHour()
    {
        WithReloadRefresh(st =>
        {
            // Same shared helper as the Telegram /extra command, so both stay consistent and both
            // skip the empty 24:00–24:00 interval when today is already allowed to midnight.
            var extra = ScheduleEvaluator.BuildExtra(st.Load(), DateTime.Now, 60);
            if (extra is not null)
                st.Update(x => x.Extras.Add(extra));
        });
    }

    private void ShutdownNow()
    {
        if (MessageBox.Show("Выключить компьютер сейчас?", "KidsLocker",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        Installer.RunProcess("shutdown.exe", "/s /f /t 0");
    }

    private void RefreshScheduleStatus()
    {
        var s = new ScheduleStore().Load();
        var now = DateTime.Now;
        var eval = ScheduleEvaluator.Evaluate(s, now);
        var next = ScheduleEvaluator.NextChange(s, now);
        var line = eval.Blocked ? "Сейчас: 🔴 ЗАПРЕЩЕНО" : "Сейчас: 🟢 разрешено";
        if (s.PauseUntil is DateTime pu && pu > now) line += $"\nПауза до {pu:HH:mm}";
        if (next is DateTime nc) line += $"\nСмена в {nc:ddd HH:mm}";
        _schedStatus.Text = line;
    }
}
