using System.Runtime.Versioning;
using System.Windows;
using KidsLocker.Service.Config;

namespace KidsLocker.Service.Gui;

/// <summary>Запуск пульта: тема Fluent, окно мастер-пароля, затем главное окно.</summary>
[SupportedOSPlatform("windows")]
public static class GuiApp
{
    private const string FluentTheme = "pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml";
    private const string Styles = "pack://application:,,,/KidsLocker;component/Gui/Styles.xaml";

    public static int Run()
    {
        var app = CreateApplication();
        app.DispatcherUnhandledException += (_, e) =>
        {
            // Непредвиденная ошибка не должна молча закрывать пульт посреди настройки.
            ConfirmDialog.Inform(app.MainWindow, "Что-то пошло не так", e.Exception.Message);
            e.Handled = true;
        };

        var login = new LoginWindow(AppConfig.Load());
        if (login.ShowDialog() != true)
            return 0; // отмена или неверный пароль

        var main = new MainWindow();
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        return app.Run(main);
    }

    /// <summary>
    /// Приложение WPF с темой Fluent и общими стилями пульта. Словарь Fluent сам включает тему
    /// «как в Windows»: светлая или тёмная по настройке системы (с переключением на лету),
    /// системный акцентный цвет, тёмный заголовок окна и фон Mica.
    /// </summary>
    public static Application CreateApplication()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(FluentTheme) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(Styles) });
        return app;
    }
}
