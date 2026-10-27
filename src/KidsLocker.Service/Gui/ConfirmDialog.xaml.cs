using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;

namespace KidsLocker.Service.Gui;

/// <summary>Вопрос или сообщение в окне той же темы, что и пульт (системные окна-сообщения в тёмной теме остаются светлыми).</summary>
[SupportedOSPlatform("windows")]
public sealed partial class ConfirmDialog : Window
{
    private ConfirmDialog(Window? owner, string heading, string body)
    {
        InitializeComponent();
        if (owner is { IsVisible: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Heading.Text = heading;
        Body.Text = body;
        Body.Visibility = body.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Спросить подтверждение. Для опасных действий кнопка по умолчанию — «Отмена»,
    /// чтобы случайный Enter ничего не удалил и не выключил.
    /// </summary>
    public static bool Ask(Window? owner, string heading, string body, string confirmText, bool risky = false)
    {
        var dialog = new ConfirmDialog(owner, heading, body);
        dialog.ConfirmButton.Content = confirmText;
        dialog.ConfirmButton.IsDefault = !risky;
        dialog.CancelButton.IsDefault = risky;
        dialog.Loaded += (_, _) => (risky ? dialog.CancelButton : dialog.ConfirmButton).Focus();
        return dialog.ShowDialog() == true;
    }

    /// <summary>Сообщить что-то важное с одной кнопкой «Понятно».</summary>
    public static void Inform(Window? owner, string heading, string body)
    {
        var dialog = new ConfirmDialog(owner, heading, body);
        dialog.ConfirmButton.Content = "Понятно";
        dialog.ConfirmButton.IsDefault = true;
        dialog.ConfirmButton.IsCancel = true;
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        Grid.SetColumnSpan(dialog.ConfirmButton, 3);
        dialog.Loaded += (_, _) => dialog.ConfirmButton.Focus();
        dialog.ShowDialog();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
