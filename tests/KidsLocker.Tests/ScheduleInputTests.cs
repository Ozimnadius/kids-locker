using KidsLocker.Service.Gui;
using KidsLocker.Service.Scheduling;
using Xunit;

namespace KidsLocker.Tests;

public class ScheduleInputTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Check_Empty_MeansBlockedAllDay(string? input)
    {
        var r = ScheduleInput.Check(input);
        Assert.Null(r.Error);
        Assert.Empty(r.Intervals);
        Assert.True(r.IsBlockedAllDay);
    }

    [Fact]
    public void Check_TwoPieces_ReturnsBoth()
    {
        var r = ScheduleInput.Check("07:00-12:00, 14:00-23:00");
        Assert.Null(r.Error);
        Assert.Equal(2, r.Intervals.Count);
        Assert.Equal(("07:00", "12:00"), (r.Intervals[0].From, r.Intervals[0].To));
        Assert.Equal(("14:00", "23:00"), (r.Intervals[1].From, r.Intervals[1].To));
        Assert.False(r.IsBlockedAllDay);
    }

    [Theory]
    [InlineData("7:00-23:00", "07:00", "23:00")]
    [InlineData("07.00 - 23.00", "07:00", "23:00")]
    [InlineData("09:00–24:00", "09:00", "24:00")] // длинное тире и полночь
    public void Check_AcceptedForms_AreNormalized(string input, string from, string to)
    {
        var r = ScheduleInput.Check(input);
        Assert.Null(r.Error);
        var iv = Assert.Single(r.Intervals);
        Assert.Equal((from, to), (iv.From, iv.To));
    }

    [Fact]
    public void Check_Typo_IsAnError_NamingTheBadTime()
    {
        // «З» — кириллическая буква вместо цифры 3: раньше такой день молча сохранялся запрещённым.
        var r = ScheduleInput.Check("10:00-2З:00");
        Assert.NotNull(r.Error);
        Assert.Contains("2З:00", r.Error);
        Assert.False(r.IsBlockedAllDay);
    }

    [Theory]
    [InlineData("10:00-2З:00", "З", "3")] // кириллическая З на месте цифры 3
    [InlineData("1О:00-23:00", "О", "0")] // кириллическая О на месте нуля
    [InlineData("1O:00-23:00", "O", "0")] // латинская O на месте нуля
    public void Check_LookalikeLetter_NamesLetterAndDigit(string input, string letter, string digit)
    {
        // В шрифте интерфейса такие буквы неотличимы от цифр: без явной подсказки родитель не найдёт ошибку.
        var r = ScheduleInput.Check(input);
        Assert.NotNull(r.Error);
        Assert.Contains($"«{letter}»", r.Error);
        Assert.Contains($"цифры {digit}", r.Error);
    }

    [Fact]
    public void Check_OtherLetter_IsNamed()
    {
        var r = ScheduleInput.Check("10:00-2x:00");
        Assert.NotNull(r.Error);
        Assert.Contains("«x»", r.Error);
    }

    [Theory]
    [InlineData("23:00-07:00")] // конец раньше начала
    [InlineData("12:00-12:00")] // пустой отрезок
    [InlineData("07:00")]       // нет конца
    [InlineData("07:00-12:00-14:00")]
    [InlineData("25:00-26:00")] // таких часов нет
    [InlineData("garbage")]
    public void Check_Malformed_IsAnError(string input)
    {
        var r = ScheduleInput.Check(input);
        Assert.NotNull(r.Error);
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Check_OneBadPieceAmongGood_RejectsWholeDay()
    {
        var r = ScheduleInput.Check("07:00-12:00, 14:00-2З:00");
        Assert.NotNull(r.Error);
        Assert.Contains("2З:00", r.Error);
    }

    [Theory]
    [InlineData("07:00-12:00,,14:00-23:00", 2)]
    [InlineData("07:00-23:00,", 1)]
    [InlineData(" , 07:00-23:00", 1)]
    public void Check_StrayCommas_AreIgnored(string input, int count)
    {
        var r = ScheduleInput.Check(input);
        Assert.Null(r.Error);
        Assert.Equal(count, r.Intervals.Count);
    }

    [Theory]
    [InlineData("07:00-23:00")]
    [InlineData("\t07:00-23:00")]
    [InlineData("07:00-23:00\u00A0")] // неразрывный пробел
    [InlineData("\u00A007:00 - 12:00 ,14:00-23:00")]
    [InlineData("7:00-9:00,10:00-11:00,12.00-13.00")]
    [InlineData("07:00–24:00")]
    [InlineData(" , 07:00-23:00")]
    [InlineData("07:00-12:00,,14:00-23:00")]
    public void Check_Accepted_KeepsEveryPiece(string input)
    {
        // Гарантия для сохранения: если проверка прошла, ScheduleText не выбросит ни одного куска.
        var pieces = input.Split(',').Count(p => p.Trim().Length > 0);
        var r = ScheduleInput.Check(input);
        Assert.Null(r.Error);
        Assert.Equal(pieces, r.Intervals.Count);
        Assert.Equal(pieces, ScheduleText.ParseIntervals(input).Count);
    }
}

public class DayRowTests
{
    [Fact]
    public void Typo_IsError_ForBindingAndNote()
    {
        var row = new DayRow("sat", "Сб", "Суббота", "субботу", "10:00-2З:00");
        Assert.Equal(DayState.Error, row.State);
        Assert.True(row.HasErrors);
        Assert.Equal(row.Result.Error, Assert.Single(row.GetErrors(nameof(DayRow.Text)).Cast<string>()));
        Assert.Equal(row.Result.Error, row.Note);
    }

    [Fact]
    public void Empty_IsBlockedAllDay_WithoutErrors()
    {
        var row = new DayRow("sun", "Вс", "Воскресенье", "воскресенье", "");
        Assert.Equal(DayState.BlockedAllDay, row.State);
        Assert.False(row.HasErrors);
        Assert.Empty(row.GetErrors(nameof(DayRow.Text)).Cast<string>());
        Assert.Equal("Весь день запрещено", row.Note);
    }

    [Fact]
    public void FixingTypo_ClearsError_AndNotifiesBinding()
    {
        var row = new DayRow("sat", "Сб", "Суббота", "субботу", "10:00-2З:00");
        var errorsChanged = 0;
        var changed = new List<string?>();
        row.ErrorsChanged += (_, _) => errorsChanged++;
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.Text = "10:00-23:00";

        Assert.Equal(DayState.Allowed, row.State);
        Assert.False(row.HasErrors);
        Assert.Equal(1, errorsChanged); // рамка ошибки у поля снимется
        Assert.Contains(nameof(DayRow.State), changed);
        Assert.Contains(nameof(DayRow.Intervals), changed); // полоска суток перерисуется
    }
}
