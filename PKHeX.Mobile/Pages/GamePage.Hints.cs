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

    // 放生：清空这一格并安全写回存档。只在这里（箱子/队伍详情）提供，银行页没有。
    private async void OnPhoneSheetReleaseTapped(object? sender, TappedEventArgs e)
    {
        var pk = _phoneSheetPk;
        if (pk is null || pk.Species == 0) return;
        Haptic();

        var name  = MoveSpeciesName(pk);
        var place = _boxIndex == -1
            ? $"队伍第 {_cursorSlot + 1} 位"
            : $"箱子 {_boxIndex + 1} 第 {_cursorSlot + 1} 格";

        var first = await DisplayAlertAsync("放生确认",
            $"{name} Lv.{pk.CurrentLevel}（{place}）即将放生。\n放生会清空这一格，并立刻写入存档文件。",
            "继续", "取消");
        if (!first) return;

        var second = await DisplayAlertAsync("再次确认",
            $"{name} 放生后无法恢复，确定放生吗？\n若只是清掉神秘礼物导入的非法宝可梦，建议先备份存档。",
            "确认放生", "取消");
        if (!second) return;

        await HidePhoneDetailSheetAsync();
        DeselectSlot();

        var box  = _boxIndex == -1 ? -2 : _boxIndex;
        var slot = _cursorSlot;

        try
        {
            var result = await SaveSyncService.ApplyAndWriteAsync(_session, s =>
            {
                if (box == -2)
                    s.SetPartySlotAtIndex(s.BlankPKM, slot);
                else
                    s.SetBoxSlotAtIndex(s.BlankPKM, box, slot);
            });

            if (result is SaveWriteResult.NoUri or SaveWriteResult.StaleUnreadable)
                throw new InvalidOperationException(result == SaveWriteResult.NoUri
                    ? "找不到原存档文件位置，请改用「导出」。"
                    : "存档已被游戏更新且无法解析，请回首页重新加载存档后再试。");

            _sav = _session.ActiveSave ?? _sav;
            LoadBox(_boxIndex);

            if (result == SaveWriteResult.MergedIntoLatest)
            {
                // test11 问题7：合并改轻提示，不再弹窗打断
                Toast("已合并到最新存档");
                RefreshCanvasAfterDialog();
            }
        }
        catch (Exception ex)
        {
            // 同上：回滚换了 SaveFile 实例，_sav 要重新指向会话里那份
            _sav = _session.ActiveSave ?? _sav;
            LoadBox(_boxIndex);
            await DisplayAlertAsync("放生失败，存档未改动", ex.Message, "OK");
            RefreshCanvasAfterDialog();
        }
    }
}
