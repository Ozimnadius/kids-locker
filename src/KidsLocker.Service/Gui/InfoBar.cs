using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KidsLocker.Service.Gui;

public enum InfoSeverity { Info, Success, Warning, Error }

/// <summary>
/// Плашка с сообщением на странице, как InfoBar в Windows 11: цветной фон и значок по важности.
/// Заменяет всплывающие окна «Сохранено» и «Ошибка».
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InfoBar : Border
{
    private readonly Ellipse _dot = new() { Width = 16, Height = 16 };
    private readonly TextBlock _glyph = new()
    {
        FontSize = 10,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    public InfoBar()
    {
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(14, 10, 14, 10);
        Margin = new Thickness(0, 0, 0, 8);
        Visibility = Visibility.Collapsed;
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorInverseBrush");
        _text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        var icon = new Grid { Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.Children.Add(_dot);
        icon.Children.Add(_glyph);

        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(_text);
        Child = row;
    }

    public void Show(InfoSeverity severity, string message)
    {
        var (glyph, back, fill) = severity switch
        {
            InfoSeverity.Success => ("", "SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
            InfoSeverity.Warning => ("", "SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
            InfoSeverity.Error => ("", "SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush"),
            _ => ("i", "SystemFillColorAttentionBackgroundBrush", "SystemFillColorAttentionBrush"),
        };

        SetResourceReference(BackgroundProperty, back);
        _dot.SetResourceReference(Shape.FillProperty, fill);

        // У «информации» значок — обычная буква i, у остальных — глиф из Segoe Fluent Icons.
        if (severity == InfoSeverity.Info)
        {
            _glyph.ClearValue(TextBlock.FontFamilyProperty);
            _glyph.FontWeight = FontWeights.Bold;
            _glyph.FontSize = 11;
        }
        else
        {
            _glyph.SetResourceReference(TextBlock.FontFamilyProperty, "SymbolThemeFontFamily");
            _glyph.FontWeight = FontWeights.Normal;
            _glyph.FontSize = 10;
        }

        _glyph.Text = glyph;
        _text.Text = message;
        Visibility = Visibility.Visible;
    }

    public void Hide() => Visibility = Visibility.Collapsed;
}
