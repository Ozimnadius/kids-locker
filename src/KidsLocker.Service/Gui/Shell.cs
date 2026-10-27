namespace KidsLocker.Service.Gui;

/// <summary>Что страницы пульта получают от главного окна.</summary>
public interface IShell
{
    /// <summary>
    /// Выполнить работу так, чтобы окно не зависало: кнопки на время недоступны, сверху виден индикатор.
    /// Пока идёт «критичная» работа (установка, перезапуск службы), окно нельзя закрыть.
    /// Возвращает исключение, если работа упала, иначе null.
    /// </summary>
    Task<Exception?> RunAsync(string text, Func<Task> work, bool critical);

    /// <summary>Перечитать состояние службы и показать его в меню и на странице «Служба».</summary>
    void RefreshServiceStatus();
}

/// <summary>Страница пульта: блокирует свои кнопки на время фоновой работы и знает о несохранённых правках.</summary>
public interface IPage
{
    void SetBusy(bool busy);

    /// <summary>Есть правки, которые пропадут, если закрыть пульт без сохранения.</summary>
    bool HasUnsavedChanges { get; }
}
