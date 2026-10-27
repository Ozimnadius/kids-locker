using System.Runtime.Versioning;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Scheduling;
using KidsLocker.Service.Setup;

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
    private bool _savedEnabled;

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
        _savedEnabled = Enabled.IsChecked == true;
        UpdateSaveHint();
    }

    private void UpdateSaveHint()
    {
        var bad = _rows.FirstOrDefault(r => !r.Result.IsValid);
        if (bad is not null)
        {
            SaveHint.Text = $"Сначала исправьте {bad.Accusative}";
            SaveHint.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            return;
        }

        var dirty = _savedEnabled != (Enabled.IsChecked == true)
                    || _rows.Where((r, i) => ScheduleText.Format(r.Intervals) != _savedTexts[i]).Any();
        SaveHint.Text = dirty ? "Есть несохранённые изменения" : "";
        SaveHint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
    }

    private void OnEnabledClick(object sender, RoutedEventArgs e) => UpdateSaveHint();

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

        var s = new ScheduleStore().Load(); // сохраняем дополнительные отрезки, паузу и часовой пояс
        s.Enabled = Enabled.IsChecked == true;
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
        RunAction("Ставлю паузу на 2 часа…", st => st.Update(x => x.PauseUntil = DateTime.Now.AddHours(2)));

    private void OnResume(object sender, RoutedEventArgs e) =>
        RunAction("Снимаю паузу…", st => st.Update(x => x.PauseUntil = null));

    private void OnExtraHour(object sender, RoutedEventArgs e) =>
        RunAction("Добавляю час на сегодня…", AddExtraHour);

    private async void RunAction(string text, Action<ScheduleStore> op)
    {
        ActionInfo.Hide();
        var error = await _shell.RunAsync(text,
            () => Task.Run(() => ServiceControl.ApplyWithReload(() => op(new ScheduleStore()))), critical: true);
        if (error is not null) ActionInfo.Show(InfoSeverity.Error, "Ошибка: " + error.Message);
        RefreshStatus();
        _shell.RefreshServiceStatus();
    }

    private static void AddExtraHour(ScheduleStore st)
    {
        var now = DateTime.Now;
        var s = st.Load();
        var latest = ScheduleEvaluator.AllowedFor(s, now).Select(r => r.To).DefaultIfEmpty(now.TimeOfDay).Max();
        var start = now.TimeOfDay > latest ? now.TimeOfDay : latest;
        var end = start + TimeSpan.FromHours(1);
        if (end > TimeParsing.EndOfDay) end = TimeParsing.EndOfDay;
        st.Update(x => x.Extras.Add(new DatedInterval(now.ToString("yyyy-MM-dd"), TimeParsing.Format(start), TimeParsing.Format(end))));
    }

    private async void OnShutdown(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Ask(Window.GetWindow(this), "Выключить компьютер сейчас?",
                "Все программы закроются без сохранения.", "Выключить", risky: true))
            return;

        var error = await _shell.RunAsync("Выключаю компьютер…",
            () => Task.Run(() => Installer.RunProcess("shutdown.exe", "/s /f /t 0")), critical: false);
        if (error is not null) ActionInfo.Show(InfoSeverity.Error, "Ошибка: " + error.Message);
    }

    /// <summary>
    /// Обновить карточку «сейчас». Файл расписания здесь только читается: опрос раз в полминуты
    /// ничего не должен записывать, даже если файл как раз меняет служба.
    /// </summary>
    public void RefreshStatus()
    {
        Schedule? s;
        try
        {
            s = File.Exists(AppPaths.ScheduleFile)
                ? JsonSerializer.Deserialize<Schedule>(File.ReadAllText(AppPaths.ScheduleFile), JsonOpts)
                : null;
        }
        catch
        {
            return; // файл как раз перезаписывают — покажем при следующем обновлении
        }
        if (s is null) return;

        var now = DateTime.Now;
        var eval = ScheduleEvaluator.Evaluate(s, now);
        var (text, back, fore) = !s.Enabled
            ? ("Контроль выключен", "SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush")
            : eval.Blocked
                ? ("Сейчас запрещено", "SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush")
                : ("Сейчас разрешено", "SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush");
        NowText.Text = text;
        NowBadge.SetResourceReference(Border.BackgroundProperty, back);
        NowText.SetResourceReference(TextBlock.ForegroundProperty, fore);

        var details = new List<string>();
        if (s.PauseUntil is DateTime pause && pause > now) details.Add($"Пауза до {pause:HH:mm}");
        if (ScheduleEvaluator.NextChange(s, now) is DateTime change) details.Add($"Смена в {RoundUpToMinute(change):ddd HH:mm}");
        NowDetails.Text = string.Join(" · ", details);
    }

    // Конец дня «24:00» вычисляется как 23:59:59.9999999 — показываем его как полночь.
    private static DateTime RoundUpToMinute(DateTime t)
    {
        var rest = t.Ticks % TimeSpan.TicksPerMinute;
        return rest == 0 ? t : t.AddTicks(TimeSpan.TicksPerMinute - rest);
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
