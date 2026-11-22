using Microsoft.Maui.Controls.Shapes;
using PKHeX.Core;
using PKHeX.Mobile.Services;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using static PKHeX.Mobile.Services.ThemeService;

namespace PKHeX.Mobile.Pages;

// 银行页：与箱子页 PhoneDetailSheet 同款的详情浮层 + 底部提示徽章可点 + 箱子页互跳
public partial class BankPage
{
    // 首版（对齐“最后一个没闪退的包”）先不开放浮层内直接取出，验证稳定后再打开
    private const bool EnableWithdrawFromDetail = true;

    private Border[]? _detailBadges;
    private GameStrings? _enStrings;
#if ANDROID
    private Android.Media.MediaPlayer? _cryPlayer;
#endif
    private string _lastCrySlug = "";

    private GameStrings EnStrings => _enStrings ??= GameLang.English;

    // ──────────────────────────────────────────────
    //  详情浮层
    // ──────────────────────────────────────────────

    private async Task ShowDetailAsync(PKM pk)
    {
        if (pk is null || pk.Species == 0) return;
        _detailPk     = pk;
        _detailSlot   = _cursorSlot;
        DetailWithdrawBtn.IsVisible = EnableWithdrawFromDetail;
        UpdateDetailContent(pk);

        DetailScrim.Opacity      = 0;
        DetailPanel.TranslationY = 600;
        DetailOverlay.IsVisible  = true;
        DetailOverlay.InputTransparent = false;
        DetailScrim.InputTransparent   = false;
        _detailVisible = true;

        await Task.WhenAll(
            DetailScrim.FadeToAsync(0.55, 220, Easing.CubicOut),
            DetailPanel.TranslateToAsync(0, 0, 260, Easing.CubicOut));
    }

    private async Task HideDetailAsync()
    {
        if (!_detailVisible) return;
        _detailVisible = false;
        DetailScrim.InputTransparent = true;

        await Task.WhenAll(
            DetailScrim.FadeToAsync(0, 180, Easing.CubicIn),
            DetailPanel.TranslateToAsync(0, 600, 220, Easing.CubicIn));

        DetailOverlay.InputTransparent = true;
        DetailOverlay.IsVisible        = false;
        _detailPk   = null;
        _detailSlot = -1;
    }

    private void UpdateDetailContent(PKM pk)
    {
        var speciesName = pk.Species < _strings.specieslist.Length
            ? _strings.specieslist[pk.Species] : pk.Species.ToString();
        var natureName = (int)pk.Nature < _strings.natures.Length
            ? _strings.natures[(int)pk.Nature] : "";

        DetailSpecies.Text         = (pk.IsShiny ? "✦ " : "") + speciesName;
        DetailLevelNature.Text     = $"Lv.{pk.CurrentLevel}  ·  {natureName}";

        EnsureDetailBadges();
        int t1 = pk.PersonalInfo.Type1;
        int t2 = pk.PersonalInfo.Type2;
        ApplyDetailBadge(_detailBadges![0], t1, visible: true);
        ApplyDetailBadge(_detailBadges![1], t2, visible: t2 != t1);

        var names = new[] { DetailMoveName0, DetailMoveName1, DetailMoveName2, DetailMoveName3 };
        var pps   = new[] { DetailMovePP0,   DetailMovePP1,   DetailMovePP2,   DetailMovePP3 };
        var dots  = new[] { DetailMoveDot0,  DetailMoveDot1,  DetailMoveDot2,  DetailMoveDot3 };
        var rows  = new[] { DetailMoveRow0,  DetailMoveRow1,  DetailMoveRow2,  DetailMoveRow3 };
        int[] moves = [pk.Move1, pk.Move2, pk.Move3, pk.Move4];
        int[] movePps = [pk.Move1_PP, pk.Move2_PP, pk.Move3_PP, pk.Move4_PP];
        var ctx = _session.ActiveSave?.Context ?? EntityContext.Gen9;
        for (int i = 0; i < 4; i++)
        {
            if (moves[i] == 0) { rows[i].IsVisible = false; continue; }
            rows[i].IsVisible  = true;
            names[i].Text = moves[i] < _strings.movelist.Length
                ? _strings.movelist[moves[i]] : $"Move {moves[i]}";
            pps[i].Text = $"PP {movePps[i]}";
            var moveTypeName = MoveInfo.GetType((ushort)moves[i], ctx) is var mt && mt < _strings.types.Length
                ? _strings.types[mt] : "Normal";
            if (Theme.TypeColors.Map.TryGetValue(moveTypeName, out var tc))
                dots[i].Fill = new SolidColorBrush(
                    Color.FromUint((uint)((tc.Alpha << 24) | (tc.Red << 16) | (tc.Green << 8) | tc.Blue)));
        }

        DetailSpriteCanvas.InvalidateSurface();
    }

    private void EnsureDetailBadges()
    {
        if (_detailBadges is not null) return;
        _detailBadges = new[] { CreateDetailBadge(), CreateDetailBadge() };
        foreach (var b in _detailBadges) DetailTypeBadges.Add(b);
    }

    private static Border CreateDetailBadge() => new()
    {
        StrokeShape   = new RoundRectangle { CornerRadius = 6 },
        StrokeThickness = 0,
        Padding       = new Thickness(7, 2),
        Content       = new Label
        {
            FontFamily = "NunitoBold", FontSize = 9, TextColor = Colors.White,
        },
    };

    private void ApplyDetailBadge(Border badge, int typeId, bool visible)
    {
        badge.IsVisible = visible;
        if (!visible) return;
        var typeName = typeId < _strings.types.Length ? _strings.types[typeId] : "???";
        badge.BackgroundColor = Theme.TypeColors.Map.TryGetValue(typeName, out var c)
            ? Color.FromUint((uint)((c.Alpha << 24) | (c.Red << 16) | (c.Green << 8) | c.Blue))
            : Color.FromArgb("#A8A878");
        ((Label)badge.Content!).Text = typeName.ToUpperInvariant();
    }

    private void OnDetailSpritePaint(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_detailPk is null) return;
        var sprite  = _sprites.GetSprite(_detailPk);
        float aspect = sprite.Width > 0 ? (float)sprite.Width / sprite.Height : 1f;
        float w = e.Info.Width, h = e.Info.Height;
        float drawW, drawH;
        if (w / h <= aspect) { drawW = w; drawH = w / aspect; }
        else                 { drawH = h; drawW = h * aspect; }
        canvas.DrawBitmap(sprite, SKRect.Create((w - drawW) / 2f, (h - drawH) / 2f, drawW, drawH));
    }

    private void OnDetailScrimTapped(object? sender, TappedEventArgs e) => _ = HideDetailAsync();
    private void OnDetailCloseTapped(object? sender, TappedEventArgs e) => _ = HideDetailAsync();
    private void OnDetailCryTapped(object? sender, TappedEventArgs e)
    {
        PlayDetailCry();
        Haptic();
    }

    private void OnDetailWithdrawTapped(object? sender, TappedEventArgs e) => _ = WithdrawFromDetailAsync();

    private void OnDetailGrabTapped(object? sender, TappedEventArgs e) => _ = GrabFromDetailAsync();

    private async Task GrabFromDetailAsync()
    {
        var pk = _detailPk;
        if (pk is null) return;
        int box = _boxIndex, slot = _detailSlot;
        await HideDetailAsync();

        _movePk         = pk.Clone();
        _moveSourceBox  = box;
        _moveSourceSlot = slot;
        _moveMode       = true;
        BankCanvas.InvalidateSurface();
        UpdateModeBanner();
    }

    private void PlayDetailCry()
    {
        if (_detailPk is { Species: > 0 } pk) PlayCry((ushort)pk.Species);
    }

    private void PlayCurrentSlotCry()
    {
        var pk = _cursorSlot < _currentSlots.Length ? _currentSlots[_cursorSlot] : null;
        if (pk is { Species: > 0 }) PlayCry((ushort)pk.Species);
        Haptic();
    }

    private async void PlayCry(ushort species)
    {
        if (species == 0) return;
        var en   = EnStrings.specieslist;
        var name = species < en.Length ? en[species] : "";
        var slug = SpriteCacheService.ToShowdownSlug(name);
        if (string.IsNullOrWhiteSpace(slug)) return;
        if (slug == _lastCrySlug) return;   // 防抖
        _lastCrySlug = slug;

        var cryDir  = System.IO.Path.Combine(FileSystem.CacheDirectory, "cries");
        var cryPath = System.IO.Path.Combine(cryDir, slug + ".ogg");
        if (!File.Exists(cryPath))
        {
            Directory.CreateDirectory(cryDir);
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                var bytes = await http.GetByteArrayAsync(
                    $"https://play.pokemonshowdown.com/audio/cries/{slug}.ogg");
                await File.WriteAllBytesAsync(cryPath, bytes);
            }
            catch { return; }
        }
        if (_lastCrySlug != slug) return;

#if ANDROID
        try
        {
            _cryPlayer?.Stop();
            _cryPlayer?.Reset();
            _cryPlayer?.Release();
            _cryPlayer = new Android.Media.MediaPlayer();
            _cryPlayer.SetDataSource(cryPath);
            _cryPlayer.Prepare();
            _cryPlayer.Start();
        }
        catch
        {
            _cryPlayer?.Release();
            _cryPlayer = null;
        }
#endif
    }

    // ──────────────────────────────────────────────
    //  取出到箱子（首版默认关闭）
    // ──────────────────────────────────────────────

    private async Task WithdrawFromDetailAsync()
    {
        var pk = _detailPk;
        if (pk is null) return;
        int box = _boxIndex, slot = _detailSlot;
        await HideDetailAsync();

        _session.PendingMove       = pk.Clone();
        _session.PendingFromBank   = true;
        _session.PendingSourceBox  = box;
        _session.PendingSourceSlot = slot;

        _session.BankSlideDir = -1;
        await SwapToGame(-1);
    }

    // ──────────────────────────────────────────────
    //  底部提示徽章（本身就是按键）
    // ──────────────────────────────────────────────

    private void BuildHintBar()
    {
        HintBar.Children.Clear();
        HintBar.Children.Add(HintKey(KeyMap.Confirm, HintPalette.AccentConfirm, UiText.HintSelectPlace, () => HandleActivate()));
        HintBar.Children.Add(HintKey(KeyMap.Grab, HintPalette.AccentGrab, UiText.HintGrab, () =>
        {
            if (_cursorSlot < _currentSlots.Length && _currentSlots[_cursorSlot]?.Species > 0)
                EnterWithdrawMode();
        }));
        HintBar.Children.Add(HintKey(KeyMap.Cancel, HintPalette.AccentCancel, UiText.HintBack, () =>
        {
            _session.PendingMove = null;
            _ = SwapToGame();
        }));
        HintBar.Children.Add(HintKey(KeyMap.Cry, HintPalette.AccentCry, UiText.HintCry, PlayCurrentSlotCry));
    }

    private Border HintKey(PKHeX.Mobile.Services.KeyBinding binding, Color accent, string text, Action action)
    {
        var border = new Border
        {
            StrokeShape   = new RoundRectangle { CornerRadius = 5 },
            StrokeThickness = 1,
            Padding       = new Thickness(8, 4),
        };
        if (Application.Current?.Resources.TryGetValue("ThHintBg", out var bgRes) == true && bgRes is Color bg)
            border.BackgroundColor = bg;
        else
            border.BackgroundColor = Color.FromArgb("#132038");
        if (Application.Current?.Resources.TryGetValue("ThHintStroke", out var stRes) == true && stRes is Color st)
            border.Stroke = st;
        else
            border.Stroke = Color.FromArgb("#2A3A5A");

        var stack = new HorizontalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center };
        stack.Children.Add(new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            BackgroundColor = Color.FromArgb("#0B1524"),
            StrokeThickness = 0,
            Padding = new Thickness(5, 2),
            Content = new Label
            {
                Text = binding.Glyph, FontFamily = "NunitoExtraBold", FontSize = 9,
                TextColor = accent,
                HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
            },
        });
        stack.Children.Add(new Label
        {
            Text = text, FontFamily = "Nunito", FontSize = 11,
            TextColor = Color.FromArgb("#C8D2E8"), VerticalOptions = LayoutOptions.Center,
        });
        border.Content = stack;
        border.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => { Haptic(); action(); }),
        });
        return border;
    }

    private void OnToBoxTapped(object? sender, TappedEventArgs e) => _ = SwapToGame(0);

    // ──────────────────────────────────────────────
    //  小工具
    // ──────────────────────────────────────────────

    private Task ShowTipAsync(string message)
    {
#if ANDROID
        Android.Widget.Toast.MakeText(Android.App.Application.Context, message,
            Android.Widget.ToastLength.Short)?.Show();
        Haptic();
#endif
        return Task.CompletedTask;
    }

    private static void Haptic()
    {
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
    }
}
