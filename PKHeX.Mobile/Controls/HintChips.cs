using Microsoft.Maui.Controls.Shapes;

namespace PKHeX.Mobile.Controls;

/// <summary>按键提示徽章（与竖屏提示条同一套样式），供横屏单栏底栏复用。</summary>
public static class HintChips
{
    private static readonly Color CaptionColor = Color.FromArgb("#C8D2E8");

    public static Border Create(string key, string keyColorHex, string caption)
    {
        var keyChip = new Border
        {
            StrokeShape     = new RoundRectangle { CornerRadius = 4 },
            BackgroundColor = Color.FromArgb("#0B1524"),
            StrokeThickness = 0,
            Padding         = new Thickness(5, 2),
        };
        keyChip.Content = new Label
        {
            Text              = key,
            FontFamily        = "NunitoExtraBold",
            FontSize          = 9,
            TextColor         = Color.FromArgb(keyColorHex),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions   = LayoutOptions.Center,
        };

        var inner = new HorizontalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center };
        inner.Add(keyChip);
        inner.Add(new Label
        {
            Text            = caption,
            FontFamily      = "Nunito",
            FontSize        = 11,
            TextColor       = CaptionColor,
            VerticalOptions = LayoutOptions.Center,
        });

        return new Border
        {
            StrokeShape     = new RoundRectangle { CornerRadius = 5 },
            BackgroundColor = Res("ThHintBg", "#111827"),
            Stroke          = Res("ThHintStroke", "#2A3F70"),
            StrokeThickness = 1,
            Padding         = new Thickness(8, 4),
            Content         = inner,
        };
    }

    private static Color Res(string key, string fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
            return c;
        return Color.FromArgb(fallback);
    }
}
