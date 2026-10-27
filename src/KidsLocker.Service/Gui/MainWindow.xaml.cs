using System.ComponentModel;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
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
    private bool _critical;

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
        _critical = busy && critical;
        BusyText.Text = text;
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        BusyBar.IsIndeterminate = busy; // невидимая бесконечная анимация зря тратила бы процессор
        foreach (var page in new IPage[] { _telegram, _service, _schedule })
            page.SetBusy(busy);
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
        base.OnClosing(e);
    }
}
