using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using KidsLocker.Service.Scheduling;

namespace KidsLocker.Service.Gui;

/// <summary>Полоска суток 0–24 ч: разрешённые отрезки закрашены акцентным цветом.</summary>
[SupportedOSPlatform("windows")]
public sealed class DayTimeline : FrameworkElement
{
    private static readonly int[] TickHours = { 6, 12, 18 };

    public static readonly DependencyProperty IntervalsProperty = DependencyProperty.Register(
        nameof(Intervals), typeof(IReadOnlyList<Interval>), typeof(DayTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(DayTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(DayTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TickBrushProperty = DependencyProperty.Register(
        nameof(TickBrush), typeof(Brush), typeof(DayTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<Interval>? Intervals
    {
        get => (IReadOnlyList<Interval>?)GetValue(IntervalsProperty);
        set => SetValue(IntervalsProperty, value);
    }

    public Brush? TrackBrush { get => (Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush? FillBrush { get => (Brush?)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public Brush? TickBrush { get => (Brush?)GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }

    public DayTimeline()
    {
        SetResourceReference(TrackBrushProperty, "ControlAltFillColorQuarternaryBrush");
        SetResourceReference(FillBrushProperty, "AccentFillColorDefaultBrush");
        SetResourceReference(TickBrushProperty, "ControlStrongStrokeColorDisabledBrush");
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = h / 2;

        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, w, h), r, r);
        foreach (var hour in TickHours)
            dc.DrawRectangle(TickBrush, null, new Rect(Math.Round(w * hour / 24) - 0.5, 0, 1, h));

        if (Intervals is null) return;
        foreach (var iv in Intervals)
        {
            if (!TimeParsing.TryParse(iv.From, out var from) || !TimeParsing.TryParse(iv.To, out var to) || to <= from)
                continue;
            var x1 = w * from.TotalHours / 24;
            var x2 = w * to.TotalHours / 24;
            dc.DrawRoundedRectangle(FillBrush, null, new Rect(x1, 0, Math.Max(x2 - x1, h), h), r, r);
        }
    }
}

/// <summary>Подписи часов 0, 6, 12, 18, 24 над столбцом полосок <see cref="DayTimeline"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class HourScale : FrameworkElement
{
    private static readonly int[] Hours = { 0, 6, 12, 18, 24 };

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(HourScale),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public HourScale() => SetResourceReference(ForegroundProperty, "TextFillColorTertiaryBrush");

    protected override Size MeasureOverride(Size availableSize) => new(0, 16);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        if (w <= 0 || Foreground is null) return;

        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var hour in Hours)
        {
            var text = new FormattedText(hour.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, typeface, 11, Foreground, dip);
            var x = Math.Clamp(w * hour / 24 - text.Width / 2, 0, Math.Max(0, w - text.Width));
            dc.DrawText(text, new Point(x, 0));
        }
    }
}
