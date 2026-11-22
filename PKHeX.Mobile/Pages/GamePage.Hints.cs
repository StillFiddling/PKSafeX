using Microsoft.Maui.Controls.Shapes;
using PKHeX.Core;
using PKHeX.Mobile.Services;

namespace PKHeX.Mobile.Pages;

// 箱子页：底部提示徽章本身就是按键 + 详情浮层右上角的三个小按键
public partial class GamePage
{
    private GameStrings? _enStrings;
    private GameStrings EnStrings => _enStrings ??= GameLang.English;

    // ──────────────────────────────────────────────
    //  底部提示徽章 → 真按键
    // ──────────────────────────────────────────────

    private void BindBoxHintTaps()
    {
        foreach (var child in HintBadges.Children)
        {
            if (child is not HorizontalStackLayout group) continue;
            if (group.GestureRecognizers.Count > 0) continue;   // 「保存」已经自带手势
            var key = KeyOf(group);
            if (key.Length == 0) continue;
            group.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => OnHintKeyPressed(key)),
            });
        }
    }

    // 右上角「银行 ▸」：箱子 → 银行
    private async void OnToBankTapped(object? sender, TappedEventArgs e)
    {
        Haptic();
        await SwapToBank(-1);
    }

    private static string KeyOf(HorizontalStackLayout group)
    {
        if (group.Children.Count == 0) return "";
        return FindFirstLabelText(group.Children[0] as Element);
    }

    // 新样式是「外框 → 内层 Stack → 键帽 Border → Label」的嵌套结构，需要递归找第一个 Label
    private static string FindFirstLabelText(Element? element)
    {
        switch (element)
        {
            case null:
                return "";
            case Label l:
                return l.Text?.Trim() ?? "";
            case Border b:
                return FindFirstLabelText(b.Content as Element);
            case ContentView cv:
                return FindFirstLabelText(cv.Content as Element);
            case Layout ly:
                foreach (var child in ly.Children)
                {
                    var s = FindFirstLabelText(child as Element);
                    if (s.Length > 0) return s;
                }
                return "";
            default:
                return "";
        }
    }

    private Border BuildHintBadge(string key, string keyColor, string text, Action action)
    {
        var border = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 5 },
            StrokeThickness = 1,
            Padding = new Thickness(4, 3),
        };
        border.BackgroundColor = ThemeColorOr("ThHintBg", "#132038");
        border.Stroke          = ThemeColorOr("ThHintStroke", "#2A3A5A");

        var stack = new HorizontalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
        stack.Children.Add(new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 5 },
            BackgroundColor = border.BackgroundColor,
            Stroke = border.Stroke,
            StrokeThickness = 1,
            WidthRequest = 20, HeightRequest = 20,
            Content = new Label
            {
                Text = key, FontFamily = "NunitoExtraBold", FontSize = 9,
                TextColor = Color.FromArgb(keyColor),
                HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
            },
        });
        stack.Children.Add(new Label
        {
            Text = text, FontFamily = "Nunito", FontSize = 10,
            TextColor = ThemeColorOr("ThTextDim", "#8B97B0"), VerticalOptions = LayoutOptions.Center,
        });
        border.Content = stack;
        border.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => { Haptic(); action(); }),
        });
        return border;
    }

    private static Color ThemeColorOr(string key, string fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var res) == true && res is Color c)
            return c;
        return Color.FromArgb(fallback);
    }

    private void OnHintKeyPressed(string key)
    {
        Haptic();
#if ANDROID
        var code = key switch
        {
            "A"  => KeyMap.ConfirmKey,
            "Y"  => KeyMap.GrabKey,
            "B"  => KeyMap.CancelKey,
            "R"  => KeyMap.CryKey,
            _    => (Android.Views.Keycode?)null,
        };
        if (code is null) return;
        HandleGamepadKey(code.Value);
#endif
    }

    // ──────────────────────────────────────────────
    //  详情浮层右上角：Y 抓取 / R3 叫声 / 存入银行
    // ──────────────────────────────────────────────

    private async void OnPhoneSheetGrabTapped(object? sender, TappedEventArgs e)
    {
        var pk = _phoneSheetPk;
        if (pk is null) return;
        Haptic();
        await HidePhoneDetailSheetAsync();
        DeselectSlot();

        _movePk         = pk.Clone();
        _moveSourceBox  = _boxIndex == -1 ? -2 : _boxIndex;
        _moveSourceSlot = _cursorSlot;
        _moveMode       = true;
        BoxCanvas.InvalidateSurface();
    }

    private void OnPhoneSheetCryTapped(object? sender, TappedEventArgs e) => OnPhoneSheetCry();

    private void OnPhoneSheetCry()
    {
        var pk = _phoneSheetPk;
        if (pk is null || pk.Species == 0) return;
        Haptic();
        var en = EnStrings.specieslist;
        var name = pk.Species < en.Length ? en[pk.Species] : "";
        if (string.IsNullOrWhiteSpace(name)) return;
        _lastCrySlug = "";
        PlayCry(name);
    }

    private async void OnPhoneSheetDepositTapped(object? sender, TappedEventArgs e)
    {
        var pk = _phoneSheetPk;
        if (pk is null) return;
        Haptic();
        await HidePhoneDetailSheetAsync();
        DeselectSlot();

        _session.PendingMove       = pk.Clone();
        _session.PendingFromBank   = false;
        _session.PendingSourceBox  = _boxIndex == -1 ? -2 : _boxIndex;
        _session.PendingSourceSlot = _cursorSlot;
        _session.BankSlideDir      = -1;

        await Shell.Current.GoToAsync(nameof(BankPage), false);
    }
}
