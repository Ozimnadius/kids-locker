using KidsLocker.Service.Scheduling;

namespace KidsLocker.Service.Gui;

/// <summary>Итог проверки поля одного дня: отрезки к сохранению или текст ошибки.</summary>
public sealed record DayInput(IReadOnlyList<Interval> Intervals, string? Error)
{
    public bool IsValid => Error is null;

    /// <summary>Поле пустое: в этот день компьютер запрещён целиком.</summary>
    public bool IsBlockedAllDay => IsValid && Intervals.Count == 0;
}

/// <summary>
/// Строгая проверка того, что родитель ввёл в поле дня. Сохранение идёт через
/// <see cref="ScheduleText.ParseIntervals"/>, который молча пропускает непонятные куски,
/// поэтому пульт сначала проверяет текст здесь и не даёт сохранить опечатку.
/// </summary>
public static class ScheduleInput
{
    public static DayInput Check(string? text)
    {
        var pieces = (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var piece in pieces)
        {
            var error = CheckPiece(piece);
            if (error is not null) return new DayInput(Array.Empty<Interval>(), error);
        }

        // Те же разделители и правила, что у ScheduleText: раз каждый кусок прошёл проверку,
        // при сохранении ни один не выпадет, а время будет в виде ЧЧ:ММ.
        return new DayInput(ScheduleText.ParseIntervals(text ?? ""), null);
    }

    private static string? CheckPiece(string piece)
    {
        var ends = piece.Split('-', '–');
        if (ends.Length != 2)
            return $"Не понял «{piece}» — нужно ЧЧ:ММ-ЧЧ:ММ";

        if (!TimeParsing.TryParse(ends[0], out var from)) return DescribeBadTime(ends[0]);
        if (!TimeParsing.TryParse(ends[1], out var to)) return DescribeBadTime(ends[1]);

        return to <= from ? $"«{piece}»: конец должен быть позже начала" : null;
    }

    // Буквы, которые в шрифте интерфейса не отличить от цифр.
    private static readonly Dictionary<char, (char Digit, string Alphabet)> Lookalikes = new()
    {
        ['З'] = ('3', "русская"), ['з'] = ('3', "русская"),
        ['О'] = ('0', "русская"), ['о'] = ('0', "русская"),
        ['O'] = ('0', "латинская"), ['o'] = ('0', "латинская"),
        ['l'] = ('1', "латинская"), ['I'] = ('1', "латинская"),
    };

    private static string DescribeBadTime(string value)
    {
        var time = value.Trim();

        // Называем неверный символ прямо: иначе «2З:00» на экране выглядит как правильное «23:00».
        var odd = time.FirstOrDefault(c => !char.IsAsciiDigit(c) && c != ':' && c != '.' && !char.IsWhiteSpace(c));
        if (odd != default)
        {
            if (Lookalikes.TryGetValue(odd, out var twin))
                return $"Не понял «{time}»: вместо цифры {twin.Digit} стоит {twin.Alphabet} буква «{odd}»";
            return char.IsLetter(odd)
                ? $"Не понял «{time}»: «{odd}» — это буква, а нужна цифра"
                : $"Не понял «{time}»: лишний символ «{odd}»";
        }

        return time.Contains(':') || time.Contains('.')
            ? $"Нет такого времени: «{time}»"
            : $"Не понял «{time}» — нужен формат ЧЧ:ММ";
    }
}
