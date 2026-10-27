using System.Runtime.Versioning;
using System.Windows;
using KidsLocker.Service.Config;
using KidsLocker.Service.Security;

namespace KidsLocker.Service.Gui;

/// <summary>
/// Вход в пульт по мастер-паролю. При первом запуске родитель задаёт пароль, дальше — вводит его.
/// Так сын (он тоже администратор) не попадёт в пульт.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class LoginWindow : Window
{
    private readonly AppConfig _config;
    private readonly bool _create;

    public LoginWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        _create = !config.HasMasterPassword;

        Heading.Text = _create ? "Задайте мастер-пароль" : "Введите мастер-пароль";
        Subheading.Text = _create
            ? "Его будете вводить только вы. Сын не должен его знать."
            : "Пульт KidsLocker открывается только родителю.";
        OkButton.Content = _create ? "Сохранить" : "Войти";
        RepeatPanel.Visibility = _create ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) => Pass1.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var p1 = Pass1.Password;
        if (_create)
        {
            if (p1.Length < 4) { ShowError("Пароль слишком короткий (минимум 4 символа)."); return; }
            if (p1 != Pass2.Password) { ShowError("Пароли не совпадают."); return; }
            var (hash, salt) = PasswordHasher.Create(p1);
            _config.MasterPasswordHash = hash;
            _config.MasterPasswordSalt = salt;
            try { _config.Save(); }
            catch (Exception ex) { ShowError("Не удалось сохранить: " + ex.Message); return; }
            DialogResult = true;
        }
        else if (PasswordHasher.Verify(p1, _config.MasterPasswordHash, _config.MasterPasswordSalt))
        {
            DialogResult = true;
        }
        else
        {
            ShowError("Неверный пароль.");
            Pass1.SelectAll();
            Pass1.Focus();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }
}
