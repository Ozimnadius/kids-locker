using System.Runtime.Versioning;
using KidsLocker.Service.Config;
using KidsLocker.Service.Security;

namespace KidsLocker.Service.Gui;

/// <summary>
/// Master-password gate for the admin GUI. On first run it asks the parent to set a password;
/// afterwards it verifies it. This is what keeps the son (also an admin) out of the console.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LoginForm : Form
{
    private readonly AppConfig _config;
    private readonly bool _create;
    private readonly TextBox _pass1 = new() { UseSystemPasswordChar = true };
    private readonly TextBox _pass2 = new() { UseSystemPasswordChar = true };
    private readonly Label _msg = new();

    public LoginForm(AppConfig config)
    {
        _config = config;
        _create = !config.HasMasterPassword;
        Build();
    }

    private void Build()
    {
        Text = "KidsLocker";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(370, _create ? 210 : 165);

        Controls.Add(new Label
        {
            Text = _create ? "Задайте мастер-пароль" : "Введите мастер-пароль",
            Left = 16, Top = 16, Width = 330, Font = new Font("Segoe UI", 12f, FontStyle.Bold),
        });
        Controls.Add(new Label
        {
            Text = _create ? "Его будете вводить только вы. Сын не должен его знать." : "",
            Left = 16, Top = 42, Width = 340, ForeColor = Color.DimGray,
        });

        Controls.Add(new Label { Text = "Пароль:", Left = 16, Top = 78, Width = 90 });
        _pass1.Left = 110; _pass1.Top = 74; _pass1.Width = 244;
        Controls.Add(_pass1);

        var okTop = 112;
        if (_create)
        {
            Controls.Add(new Label { Text = "Повтор:", Left = 16, Top = 114, Width = 90 });
            _pass2.Left = 110; _pass2.Top = 110; _pass2.Width = 244;
            Controls.Add(_pass2);
            okTop = 150;
        }

        _msg.Left = 16; _msg.Top = okTop + 34; _msg.Width = 340; _msg.ForeColor = Color.Firebrick;
        Controls.Add(_msg);

        var ok = new Button { Text = "OK", Left = 198, Top = okTop, Width = 75 };
        var cancel = new Button { Text = "Отмена", Left = 279, Top = okTop, Width = 75, DialogResult = DialogResult.Cancel };
        ok.Click += OnOk;
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void OnOk(object? sender, EventArgs e)
    {
        var p1 = _pass1.Text;
        if (_create)
        {
            if (p1.Length < 4) { _msg.Text = "Пароль слишком короткий (минимум 4 символа)."; return; }
            if (p1 != _pass2.Text) { _msg.Text = "Пароли не совпадают."; return; }
            var (hash, salt) = PasswordHasher.Create(p1);
            _config.MasterPasswordHash = hash;
            _config.MasterPasswordSalt = salt;
            try { _config.Save(); }
            catch (Exception ex) { _msg.Text = "Не удалось сохранить: " + ex.Message; return; }
            DialogResult = DialogResult.OK;
            Close();
        }
        else if (PasswordHasher.Verify(p1, _config.MasterPasswordHash, _config.MasterPasswordSalt))
        {
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            _msg.Text = "Неверный пароль.";
            _pass1.SelectAll();
            _pass1.Focus();
        }
    }
}
