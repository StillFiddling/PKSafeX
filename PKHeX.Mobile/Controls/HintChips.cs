using Microsoft.Maui.Controls.Shapes;

namespace PKHeX.Mobile.Controls;

/// <summary>按键提示徽章（与竖屏提示条同一套样式），供横屏单栏底栏复用。</summary>
public static class HintChips
{
    private static readonly Color CaptionColor = Color.FromArgb("#C8D2E8");

    /// <summary>
    /// 构建一枚按键徽章。<paramref name="onTap"/> 为空时徽章只作提示、不可点；
    /// 新的徽章一定要传动作，别再出现「看着像按钮点了没反应」。
    /// </summary>
    public static Border Create(string key, string keyColorHex, string caption, Action? onTap = null)
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

        var chip = new Border
        {
            StrokeShape     = new RoundRectangle { CornerRadius = 5 },
            BackgroundColor = Res("ThHintBg", "#111827"),
            Stroke          = Res("ThHintStroke", "#2A3F70"),
            StrokeThickness = 1,
            Padding         = new Thickness(8, 4),
            Content         = inner,
        };

        if (onTap is not null)
        {
            chip.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => onTap()),
            });
        }

        return chip;
    }

    private static Color Res(string key, string fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
            return c;
        return Color.FromArgb(fallback);
    }
}
