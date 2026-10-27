using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Setup;

namespace KidsLocker.Service.Gui;

/// <summary>Раздел «Служба»: состояние службы, установка в тестовом/боевом режиме, удаление, журнал.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class ServicePage : UserControl, IPage
{
    private readonly IShell _shell;

    public ServicePage(IShell shell)
    {
        InitializeComponent();
        _shell = shell;
    }

    public void SetBusy(bool busy) =>
        RefreshButton.IsEnabled = InstallDryButton.IsEnabled = InstallLiveButton.IsEnabled = UninstallButton.IsEnabled = !busy;

    public bool HasUnsavedChanges => false; // здесь нечего сохранять

    public void ShowState(ServiceControl.State state, DateTime checkedAt)
    {
        var (glyph, brush, text) = state switch
        {
            ServiceControl.State.Running => ("\uEA18", "SystemFillColorSuccessBrush", "Служба работает"),
            ServiceControl.State.Stopped => ("\uE7BA", "SystemFillColorCautionBrush", "Служба установлена, но остановлена"),
            ServiceControl.State.Missing => ("\uEA18", "TextFillColorTertiaryBrush", "Служба не установлена"),
            _ => ("\uE9CE", "TextFillColorTertiaryBrush", "Состояние службы неизвестно"),
        };
        StateIcon.Text = glyph;
        StateIcon.SetResourceReference(TextBlock.ForegroundProperty, brush);
        StateText.Text = text;
        CheckedAt.Text = $"Проверено в {checkedAt:HH:mm:ss}";
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _shell.RefreshServiceStatus();

    private void OnInstallDry(object sender, RoutedEventArgs e) => Install(dryRun: true);

    private void OnInstallLive(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Ask(Window.GetWindow(this), "Включить реальное выключение?",
                "Компьютер будет выключаться по расписанию. Сначала рекомендуется «Установить (тест)».",
                "Установить", risky: true))
            return;
        Install(dryRun: false);
    }

    private async void Install(bool dryRun)
    {
        Log.Clear();
        AppendLog(dryRun ? "Установка в тестовом режиме..." : "Установка в боевом режиме...");
        var error = await _shell.RunAsync(dryRun ? "Идёт установка в тестовом режиме…" : "Идёт установка в боевом режиме…",
            () => Task.Run(() => Installer.Install(dryRun, AppendLog)), critical: true);
        AppendLog(error is null ? "Готово." : "ОШИБКА: " + error.Message);
        _shell.RefreshServiceStatus();
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Ask(Window.GetWindow(this), "Удалить KidsLocker полностью с этого ПК?",
                "Контроль времени перестанет работать.", "Удалить", risky: true))
            return;

        Log.Clear();
        AppendLog("Удаление...");
        var error = await _shell.RunAsync("Удаляю KidsLocker…",
            () => Task.Run(() => Installer.Uninstall(AppendLog)), critical: true);
        AppendLog(error is null ? "Готово." : "ОШИБКА: " + error.Message);
        _shell.RefreshServiceStatus();
    }

    // Установщик пишет строки из фонового потока.
    private void AppendLog(string line)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendLog(line)); return; }
        Log.AppendText(line + Environment.NewLine);
        Log.ScrollToEnd();
    }
}
