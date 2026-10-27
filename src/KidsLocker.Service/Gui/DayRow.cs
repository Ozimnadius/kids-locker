using System.Collections;
using System.ComponentModel;
using KidsLocker.Service.Scheduling;

namespace KidsLocker.Service.Gui;

public enum DayState { Allowed, BlockedAllDay, Error }

/// <summary>
/// Строка дня в редакторе расписания. Текст проверяется при каждом вводе; ошибку видно сразу —
/// красная рамка у поля (через <see cref="INotifyDataErrorInfo"/>) и пояснение справа.
/// </summary>
public sealed class DayRow : INotifyPropertyChanged, INotifyDataErrorInfo
{
    private string _text;

    public DayRow(string key, string name, string fullName, string accusative, string text)
    {
        Key = key;
        Name = name;
        FullName = fullName;
        Accusative = accusative;
        _text = text;
        Result = ScheduleInput.Check(text);
    }

    /// <summary>Ключ дня в файле расписания: "mon", "tue", …</summary>
    public string Key { get; }

    /// <summary>Короткое имя для строки: «Пн».</summary>
    public string Name { get; }

    /// <summary>Полное имя для экранного диктора: «Понедельник».</summary>
    public string FullName { get; }

    /// <summary>Винительный падеж для подсказок: «исправьте среду».</summary>
    public string Accusative { get; }

    public string AutomationId => "Day_" + Key;

    public DayInput Result { get; private set; }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            Result = ScheduleInput.Check(value);
            foreach (var name in new[] { nameof(Text), nameof(Result), nameof(State), nameof(Intervals), nameof(Note) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(Text)));
        }
    }

    public DayState State =>
        !Result.IsValid ? DayState.Error : Result.IsBlockedAllDay ? DayState.BlockedAllDay : DayState.Allowed;

    public IReadOnlyList<Interval> Intervals => Result.Intervals;

    public string Note => State switch
    {
        DayState.Error => Result.Error!,
        DayState.BlockedAllDay => "Весь день запрещено",
        _ => "",
    };

    public bool HasErrors => !Result.IsValid;

    public IEnumerable GetErrors(string? propertyName) =>
        HasErrors && (propertyName is null || propertyName == nameof(Text)) ? new[] { Result.Error! } : Array.Empty<string>();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
}
