using System.ComponentModel;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;

namespace KidsLocker.Service.Gui;

/// <summary>Пульт родителя: меню слева (Telegram, Служба, Расписание) и выбранная страница справа.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class MainWindow : Window, IShell
{
    private readonly TelegramPage _telegram;
    private readonly ServicePage _service;
    private readonly SchedulePage _schedule;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _busy, _critical;
    private IInputElement? _focusBeforeBusy;

    public MainWindow()
    {
        InitializeComponent();

        // На ноутбуке с крупным масштабом рабочая область бывает ниже окна — не вылезаем за экран.
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);

        _telegram = new TelegramPage(this);
        _service = new ServicePage(this);
        _schedule = new SchedulePage(this);

        RefreshServiceStatus();
        // Пока Telegram не настроен, начинаем с него; дальше родителю чаще нужно расписание.
        ShowPage(AppConfig.Load().IsTelegramConfigured ? "schedule" : "telegram");

        _timer.Tick += (_, _) =>
        {
            if (_busy) return; // во время работы файлы как раз переписываются — обновим в следующий раз
            RefreshServiceStatus();
            _schedule.RefreshStatus();
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Открыть раздел: "telegram", "service" или "schedule".</summary>
    public void ShowPage(string key) =>
        Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().First(i => (string)i.Tag == key);

    private void OnNavigate(object sender, SelectionChangedEventArgs e)
    {
        // Ctrl+щелчок по выбранному разделу снимает выбор — возвращаем его, страница-то осталась.
        if (Nav.SelectedItem is null && e.RemovedItems.Count > 0)
        {
            Nav.SelectedItem = e.RemovedItems[0];
            return;
        }
        if (Nav.SelectedItem is not ListBoxItem item) return;
        PageHost.Content = (string)item.Tag switch
        {
            "telegram" => _telegram,
            "service" => _service,
            _ => _schedule,
        };
    }

    public async Task<Exception?> RunAsync(string text, Func<Task> work, bool critical)
    {
        SetBusy(true, text, critical);
        try
        {
            await work();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            SetBusy(false, " ", critical: false);
        }
    }

    private void SetBusy(bool busy, string text, bool critical)
    {
        if (busy) _focusBeforeBusy = Keyboard.FocusedElement;
        _busy = busy;
        _critical = busy && critical;
        BusyText.Text = text;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        BusyBar.IsIndeterminate = busy; // невидимая бесконечная анимация зря тратила бы процессор
        foreach (var page in new IPage[] { _telegram, _service, _schedule })
            page.SetBusy(busy);

        // Кнопка с фокусом на время работы выключалась — возвращаем фокус на неё.
        if (!busy)
        {
            if (_focusBeforeBusy is UIElement { IsEnabled: true, IsVisible: true } element) element.Focus();
            _focusBeforeBusy = null;
        }
    }

    public void RefreshServiceStatus()
    {
        var state = ServiceControl.GetState();
        var (text, brush) = state switch
        {
            ServiceControl.State.Running => ("Служба работает", "SystemFillColorSuccessBrush"),
            ServiceControl.State.Stopped => ("Служба остановлена", "SystemFillColorCautionBrush"),
            ServiceControl.State.Missing => ("Служба не установлена", "TextFillColorTertiaryBrush"),
            _ => ("Состояние службы неизвестно", "TextFillColorTertiaryBrush"),
        };
        ServiceLine.Text = text;
        ServiceDot.SetResourceReference(Shape.FillProperty, brush);
        _service.ShowState(state, DateTime.Now);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_critical)
        {
            // Закрытие оборвало бы установку или перезапуск службы на полпути.
            e.Cancel = true;
            ConfirmDialog.Inform(this, "Подождите немного",
                "Пульт сейчас меняет службу. Закрыть окно можно будет сразу после этого.");
            return;
        }

        var unsaved = new[] { (Page: (IPage)_telegram, Key: "telegram", Title: "Telegram"), (_schedule, "schedule", "Расписание") }
            .Where(p => p.Page.HasUnsavedChanges)
            .ToList();
        if (unsaved.Count > 0)
        {
            var where = unsaved.Count == 1
                ? $"в разделе «{unsaved[0].Title}»"
                : "в разделах " + string.Join(" и ", unsaved.Select(p => $"«{p.Title}»"));
            if (!ConfirmDialog.Ask(this, "Закрыть без сохранения?",
                    $"Изменения {where} не сохранены и пропадут.", "Закрыть", risky: true))
            {
                e.Cancel = true;
                ShowPage(unsaved[0].Key);
                return;
            }
        }
        base.OnClosing(e);
    }
}
