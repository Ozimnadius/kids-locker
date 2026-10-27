using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using KidsLocker.Service.Config;
using KidsLocker.Service.Enforcement;
using KidsLocker.Service.Telegram;

namespace KidsLocker.Service.Gui;

/// <summary>Раздел «Telegram»: токен бота, chat_id родителя, проверка связи.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class TelegramPage : UserControl, IPage
{
    private readonly IShell _shell;
    private string _savedToken, _savedChatId;

    public TelegramPage(IShell shell)
    {
        InitializeComponent();
        _shell = shell;

        var config = AppConfig.Load();
        Token.Text = _savedToken = config.BotToken;
        ChatId.Text = _savedChatId = config.ParentChatId != 0 ? config.ParentChatId.ToString() : "";
    }

    public void SetBusy(bool busy) =>
        DetectButton.IsEnabled = TestButton.IsEnabled = SaveButton.IsEnabled = !busy;

    public bool HasUnsavedChanges =>
        Token.Text.Trim() != _savedToken.Trim() || ChatId.Text.Trim() != _savedChatId;

    private async void OnDetect(object sender, RoutedEventArgs e)
    {
        var token = Token.Text.Trim();
        if (token.Length == 0) { Status.Show(InfoSeverity.Error, "Введите токен."); return; }

        Status.Hide();
        var error = await _shell.RunAsync("Определяю chat_id…", async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var client = new TelegramClient(token, http);

            var me = await client.GetMeAsync(CancellationToken.None);
            if (me is null) { Status.Show(InfoSeverity.Error, "Токен неверный или нет сети."); return; }

            var updates = await client.GetUpdatesAsync(0, 0, CancellationToken.None);
            var chats = updates.Select(u => u.Message?.Chat).Where(c => c is not null)
                .GroupBy(c => c!.Id).Select(g => g.First()!).ToList();

            if (chats.Count == 0)
            {
                Status.Show(InfoSeverity.Warning, $"Бот @{me}. Пока никто не писал — напишите боту сообщение и повторите.");
                return;
            }
            ChatId.Text = chats[0].Id.ToString();
            Status.Show(InfoSeverity.Success, $"Бот @{me}. Найдено: " + string.Join("; ", chats.Select(c => $"{c.Id} {c.DisplayName}")));
        }, critical: false);

        if (error is not null) Status.Show(InfoSeverity.Error, "Ошибка: " + error.Message);
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var token = Token.Text.Trim();
        if (token.Length == 0) { Status.Show(InfoSeverity.Error, "Введите токен."); return; }
        if (!long.TryParse(ChatId.Text.Trim(), out var chatId) || chatId == 0)
        {
            Status.Show(InfoSeverity.Error, "Укажите числовой chat_id (или нажмите «Определить»).");
            return;
        }

        Status.Hide();
        var error = await _shell.RunAsync("Проверяю связь…", async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var client = new TelegramClient(token, http);

            var me = await client.GetMeAsync(CancellationToken.None);
            if (me is null) { Status.Show(InfoSeverity.Error, "Токен неверный или нет сети."); return; }

            var (ok, detail) = await client.SendMessageResultAsync(chatId, "KidsLocker: проверка связи ✅", CancellationToken.None);
            if (ok)
                Status.Show(InfoSeverity.Success, $"Успех! Бот @{me}, сообщение отправлено. Проверьте Telegram.");
            else
                Status.Show(InfoSeverity.Error, $"Бот @{me}, но сообщение не ушло: {detail}. Напишите боту сообщение и проверьте chat_id.");
        }, critical: false);

        if (error is not null) Status.Show(InfoSeverity.Error, "Ошибка: " + error.Message);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var token = Token.Text.Trim();
        if (token.Length == 0) { Status.Show(InfoSeverity.Error, "Введите токен."); return; }
        if (!long.TryParse(ChatId.Text.Trim(), out var chatId) || chatId == 0)
        {
            Status.Show(InfoSeverity.Error, "Укажите числовой chat_id.");
            return;
        }

        Status.Hide();
        var cfg = AppConfig.Load(); // свежая копия: мастер-пароль и прочие поля не теряются
        cfg.BotToken = token;
        cfg.ParentChatId = chatId;

        // Остановка и запуск службы занимают до ~40 с — делаем это в фоне, окно не зависает.
        var error = await _shell.RunAsync("Сохраняю и перезапускаю службу…",
            () => Task.Run(() => ServiceControl.ApplyWithReload(cfg.Save)), critical: true);

        if (error is null)
        {
            _savedToken = token;
            _savedChatId = chatId.ToString();
            Status.Show(InfoSeverity.Success, "Сохранено. Служба перезапущена (если установлена).");
        }
        else
        {
            Status.Show(InfoSeverity.Error, "Не удалось сохранить: " + error.Message);
        }
        _shell.RefreshServiceStatus();
    }
}
