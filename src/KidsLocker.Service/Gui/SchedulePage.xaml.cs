using System.Runtime.Versioning;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Scheduling;
using KidsLocker.Service.Time;

namespace KidsLocker.Service.Gui;

/// <summary>Раздел «Расписание»: разрешённые часы по дням, статус «сейчас» и быстрые действия.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class SchedulePage : UserControl, IPage
{
    // Порядок как в прежней вкладке: неделя начинается с понедельника.
    private static readonly (string Key, string Name, string FullName, string Accusative)[] DayInfo =
    {
        ("mon", "Пн", "Понедельник", "понедельник"),
        ("tue", "Вт", "Вторник", "вторник"),
        ("wed", "Ср", "Среда", "среду"),
        ("thu", "Чт", "Четверг", "четверг"),
        ("fri", "Пт", "Пятница", "пятницу"),
        ("sat", "Сб", "Суббота", "субботу"),
        ("sun", "Вс", "Воскресенье", "воскресенье"),
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IShell _shell;
    private readonly List<DayRow> _rows = new();
    private string[] _savedTexts = Array.Empty<string>();
    private bool _refreshingStatus;

    public SchedulePage(IShell shell)
    {
        InitializeComponent();
        _shell = shell;
        LoadSchedule();
        Days.ItemsSource = _rows;
        RefreshStatus();
    }

    public void SetBusy(bool busy)
    {
        // Поля тоже блокируем: после сохранения они переписываются сохранённым видом.
        Days.IsEnabled = Enabled.IsEnabled = !busy;
        foreach (var button in new[] { SaveButton, PauseButton, ResumeButton, ExtraButton, ShutdownButton })
            button.IsEnabled = !busy;
    }

    public bool HasUnsavedChanges =>
        _savedTexts.Length == _rows.Count
        && _rows.Where((r, i) => !r.Result.IsValid || ScheduleText.Format(r.Intervals) != _savedTexts[i]).Any();

    private void LoadSchedule()
    {
        var s = new ScheduleStore().Load();
        foreach (var (key, name, fullName, accusative) in DayInfo)
        {
            var row = new DayRow(key, name, fullName, accusative, ScheduleText.Format(s.Allowed.GetValueOrDefault(key)));
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DayRow.Text)) UpdateSaveHint(); };
            _rows.Add(row);
        }
        Enabled.IsChecked = s.Enabled;
        MarkSaved();
    }

    // Запоминаем сохранённое состояние, чтобы подсказать про несохранённые изменения.
    private void MarkSaved()
    {
        _savedTexts = _rows.Select(r => ScheduleText.Format(r.Intervals)).ToArray();
        UpdateSaveHint();
    }

    private void UpdateSaveHint()
    {
        var bad = _rows.FirstOrDefault(r => !r.Result.IsValid);
        if (bad is not null)
        {
            SaveHint.Text = $"Сначала исправьте {bad.Accusative}";
            SaveHint.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            SaveInfo.Hide();
            return;
        }

        var dirty = HasUnsavedChanges;
        SaveHint.Text = dirty ? "Есть несохранённые изменения" : "";
        SaveHint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        if (dirty) SaveInfo.Hide(); // «Расписание сохранено» больше не про то, что на экране
    }

    // Тумблер, как в «Параметрах» Windows, действует сразу — а не по кнопке из соседней карточки.
    private async void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        var enable = Enabled.IsChecked == true;
        EnabledInfo.Hide();
        if (!enable && !ConfirmDialog.Ask(Window.GetWindow(this), "Выключить контроль?",
                "Компьютер перестанет выключаться по расписанию, пока контроль снова не включат.",
                "Выключить", risky: true))
        {
            Enabled.IsChecked = true;
            return;
        }

        var error = await _shell.RunAsync(enable ? "Включаю контроль…" : "Выключаю контроль…",
            () => Task.Run(() => ServiceControl.ApplyWithReload(() => new ScheduleStore().Update(x => x.Enabled = enable))),
            critical: true);
        if (error is null)
        {
            EnabledInfo.Show(enable ? InfoSeverity.Success : InfoSeverity.Warning,
                enable ? "Контроль включён." : "Контроль выключен: компьютер не будет выключаться по расписанию.");
        }
        else
        {
            Enabled.IsChecked = !enable;
            EnabledInfo.Show(InfoSeverity.Error, "Не удалось: " + error.Message);
        }
        RefreshStatus();
        _shell.RefreshServiceStatus();
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        SaveInfo.Hide();

        // Раньше опечатка молча сохраняла день как полностью запрещённый — теперь не сохраняем вовсе.
        // Что не так, уже написано у поля и у кнопки — просто переводим курсор в это поле.
        var bad = _rows.FirstOrDefault(r => !r.Result.IsValid);
        if (bad is not null)
        {
            FocusDay(bad);
            return;
        }

        // Сохраняем дни: дополнительные отрезки, пауза и «Контроль включён»
        // остаются как в файле (тумблер применяется сам, а его могли переключить и из Telegram).
        var s = new ScheduleStore().Load();
        foreach (var row in _rows)
            s.Allowed[row.Key] = row.Intervals.ToList();

        var error = await _shell.RunAsync("Сохраняю расписание и перезапускаю службу…",
            () => Task.Run(() => ServiceControl.ApplyWithReload(() => new ScheduleStore().Save(s))), critical: true);
        if (error is not null)
        {
            SaveInfo.Show(InfoSeverity.Error, "Не удалось сохранить: " + error.Message);
            return;
        }

        foreach (var row in _rows)
            row.Text = ScheduleText.Format(row.Intervals); // показываем ровно то, что сохранено
        MarkSaved();
        SaveInfo.Show(InfoSeverity.Success, "Расписание сохранено.");
        RefreshStatus();
        _shell.RefreshServiceStatus();
    }

    private void OnPause(object sender, RoutedEventArgs e) =>
        RunTimedAction("Ставлю паузу на 2 часа…", (st, now) => st.Update(x => x.PauseUntil = now.AddHours(2)));

    private void OnResume(object sender, RoutedEventArgs e) =>
        RunAction("Снимаю паузу…", st => st.Update(x => x.PauseUntil = null));

    private void OnExtraHour(object sender, RoutedEventArgs e) =>
        RunTimedAction("Добавляю час на сегодня…", (st, now) =>
        {
            var extra = ScheduleEvaluator.BuildExtra(st.Load(), now, 60);
            if (extra is null) throw new InvalidOperationException("Сегодня уже разрешено до полуночи — добавлять нечего.");
            st.Update(x => x.Extras.Add(extra));
        });

    private async void RunTimedAction(string text, Action<ScheduleStore, DateTime> op)
    {
        ActionInfo.Hide();
        var error = await _shell.RunAsync(text, async () =>
        {
            // Получаем время ДО остановки службы для записи расписания.
            var utc = await TrustedTimeReader.GetUtcAsync();
            if (utc is null) throw new InvalidOperationException("Время ещё не подтверждено через интернет.");
            var stamp = Stopwatch.GetTimestamp();
            await Task.Run(() => ServiceControl.ApplyWithReload(() =>
            {
                var st = new ScheduleStore();
                op(st, TrustedSchedule.LocalNow(st.Get(), utc + Stopwatch.GetElapsedTime(stamp)));
            }));
        }, critical: true);
        if (error is not null) ActionInfo.Show(InfoSeverity.Error, "Ошибка: " + error.Message);
        RefreshStatus();
        _shell.RefreshServiceStatus();
    }

    private async void RunAction(string text, Action<ScheduleStore> op)
    {
        ActionInfo.Hide();
        var error = await _shell.RunAsync(text,
            () => Task.Run(() => ServiceControl.ApplyWithReload(() => op(new ScheduleStore()))), critical: true);
        if (error is not null) ActionInfo.Show(InfoSeverity.Error, "Ошибка: " + error.Message);
        RefreshStatus();
        _shell.RefreshServiceStatus();
    }

    private async void OnShutdown(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Ask(Window.GetWindow(this), "Выключить компьютер сейчас?",
                "Все программы закроются без сохранения.", "Выключить", risky: true))
            return;

        ActionInfo.Hide();

        // В тестовом режиме служба ничего не выключает — и кнопка пульта тоже не должна, иначе
        // проверка внезапно гасит ПК. Честно сообщаем об этом вместо выключения.
        if (ServiceControl.InstalledDryRun())
        {
            ActionInfo.Show(InfoSeverity.Info,
                "Служба в тестовом режиме — реальное выключение отключено. Переустановите в боевом режиме, чтобы выключать по-настоящему.");
            return;
        }

        // Тот же общий путь выключения, что и у службы (shutdown.exe с запасным P/Invoke).
        var error = await _shell.RunAsync("Выключаю компьютер…",
            () => Task.Run(() => NativeShutdown.Force("KidsLocker: выключение из пульта")), critical: false);
        if (error is not null)
            ActionInfo.Show(InfoSeverity.Error, "Ошибка: " + error.Message);
    }

    /// <summary>
    /// Обновить карточку «сейчас» и тумблер. Файл расписания здесь только читается: опрос раз в
    /// полминуты ничего не должен записывать, даже если файл как раз меняет служба.
    /// </summary>
    public async void RefreshStatus()
    {
        if (_refreshingStatus) return;
        _refreshingStatus = true;
        try
        {
            var utc = await TrustedTimeReader.GetUtcAsync();
            if (ReadScheduleQuietly() is not { } s) return;
            if (Enabled.IsEnabled) Enabled.IsChecked = s.Enabled;
            var eval = TrustedSchedule.Evaluate(s, utc);
            var (text, back, fore) = !s.Enabled
                ? ("Контроль выключен", "SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush")
                : eval.Blocked
                    ? ("Сейчас запрещено", "SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush")
                    : ("Сейчас разрешено", "SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush");
            NowText.Text = text;
            NowBadge.SetResourceReference(Border.BackgroundProperty, back);
            NowText.SetResourceReference(TextBlock.ForegroundProperty, fore);
            if (utc is null)
            {
                NowDetails.Text = "Время не подтверждено через интернет";
                return;
            }
            var now = TrustedSchedule.LocalNow(s, utc);
            var details = new List<string> { $"Проверенное время: {now:HH:mm}", "Москва (UTC+3)" };
            if (s.PauseUntil is DateTime pause && pause > now) details.Add($"Пауза до {pause:HH:mm}");
            if (ScheduleEvaluator.NextChange(s, now) is DateTime change) details.Add($"Смена в {change:ddd HH:mm}");
            NowDetails.Text = string.Join(" · ", details);
        }
        catch (Exception ex)
        {
            NowText.Text = "Статус не подтверждён";
            NowDetails.Text = ex.Message;
        }
        finally { _refreshingStatus = false; }
    }

    // Читаем так, чтобы не мешать службе заменять файл (File.Move поверх открытого файла).
    private static Schedule? ReadScheduleQuietly()
    {
        try
        {
            if (!File.Exists(AppPaths.ScheduleFile)) return null;
            using var stream = new FileStream(AppPaths.ScheduleFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<Schedule>(stream, JsonOpts);
        }
        catch
        {
            return null; // файл как раз перезаписывают — покажем при следующем обновлении
        }
    }

    private void FocusDay(DayRow row)
    {
        if (Days.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject container
            && FindChild<TextBox>(container) is { } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
