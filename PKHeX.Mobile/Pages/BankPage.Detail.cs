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
    private string _lastCrySlug = "";
    /// <summary>详情浮层显隐序号：收尾动画校验，避免清掉新打开的卡片。</summary>
    private int _detailSeq;
    /// <summary>竖屏卡片高度上限（min(0.94 屏, 760)）。</summary>
    private double _detailMaxH;
    /// <summary>当前卡片实际高度（横屏固定值，竖屏为实测后 clamp 的结果）。</summary>
    private double _detailCardH;

    private GameStrings EnStrings => _enStrings ??= GameLang.English;

    // ──────────────────────────────────────────────
    //  详情浮层
    // ──────────────────────────────────────────────

    private async Task ShowDetailAsync(PKM pk)
    {
        if (pk is null || pk.Species == 0) return;
        _detailPk     = pk;
        _detailSlot   = _cursorSlot;
        _detailSeq++;                       // 使在途的隐藏动画收尾失效

        // 顺序：先可见（否则 Measure 返回 0）→ 定宽与复位高度 → 填内容 → 占位与量高
        DetailOverlay.IsVisible        = true;
        ApplyDetailSheetSize();
        UpdateDetailContent(pk);
        ApplyDetailMovesReserve();
        ApplyDetailSheetHeight();
        DetailWithdrawBtn.IsVisible = EnableWithdrawFromDetail;

        // 后台预取叫声，避免第一次按 R 等网络
        {
            var en = EnStrings.specieslist;
            if (pk.Species > 0 && pk.Species < en.Length)
                CryService.Prefetch(CryService.Slug(en[pk.Species]));
        }

        DetailPanel.TranslationY = 0;
        DetailScrim.Opacity      = 0;
        DetailPanel.Opacity      = 0;
        DetailPanel.Scale        = 0.96;
        DetailOverlay.InputTransparent = false;
        DetailScrim.InputTransparent   = false;
        _detailVisible = true;              // 先置位：淡入中按返回也能先关卡片

        await Task.WhenAll(
            DetailScrim.FadeToAsync(0.55, 220, Easing.CubicOut),
            DetailPanel.FadeToAsync(1, 220, Easing.CubicOut),
            DetailPanel.ScaleToAsync(1, 220, Easing.CubicOut));
    }

    private async Task HideDetailAsync()
    {
        if (!_detailVisible) return;
        int seq = ++_detailSeq;
        _detailVisible = false;
        DetailScrim.InputTransparent = true;

        await Task.WhenAll(
            DetailScrim.FadeToAsync(0, 180, Easing.CubicIn),
            DetailPanel.FadeToAsync(0, 180, Easing.CubicIn),
            DetailPanel.ScaleToAsync(0.96, 200, Easing.CubicIn));

        if (seq != _detailSeq) return;      // 期间又打开了新卡片，别清掉它

        DetailOverlay.InputTransparent = true;
        DetailOverlay.IsVisible        = false;
        DetailPanel.TranslationY       = 0;
        DetailPanel.Scale              = 1;
        _detailPk   = null;
        _detailSlot = -1;
    }

    /// <summary>
    /// 卡片尺寸：竖屏按内容撑高（招式块固定 4 招占位），横屏固定 min(屏高×0.94, 760)；
    /// 宽度统一 min(屏宽×0.92, 560)。银行页无底部通栏，行结构为 表头 + 可滚内容。
    /// </summary>
    private void ApplyDetailSheetSize()
    {
        double w = Width  > 0 ? Width  : (Window?.Width  ?? 400);
        double h = Height > 0 ? Height : (Window?.Height ?? 700);
        DetailPanel.WidthRequest = Math.Min(w * 0.92, 560);
        _detailMaxH = Math.Min(h * 0.94, 760);

        if (_isLandscape)
        {
            DetailPanel.HeightRequest         = _detailMaxH;
            DetailPanel.MaximumHeightRequest  = double.PositiveInfinity;
            DetailPanel.MinimumHeightRequest  = -1;
            _detailCardH = _detailMaxH;
        }
        else
        {
            // 竖屏：先解除高度约束，等内容填完再由 ApplyDetailSheetHeight 实测
            DetailPanel.HeightRequest         = -1;
            DetailPanel.MaximumHeightRequest  = double.PositiveInfinity;
            DetailPanel.MinimumHeightRequest  = -1;
            _detailCardH = 0;
        }
    }

    /// <summary>
    /// 竖屏按内容量高（同箱子页）：内部 Grid 是 Auto,*，受约束测量时 * 行会吃满剩余高度，
    /// 所以必须用无约束测量拿内容高度再 clamp 写回；横屏保持固定高，直接返回。
    /// </summary>
    private void ApplyDetailSheetHeight()
    {
        if (_isLandscape) { _detailCardH = DetailPanel.HeightRequest; return; }

        double maxH  = _detailMaxH > 0 ? _detailMaxH : 700;
        double cardW = DetailPanel.WidthRequest > 0 ? DetailPanel.WidthRequest : 360;
        double desired = 0;
        try { desired = DetailPanel.Measure(cardW, double.PositiveInfinity).Height; }
        catch { /* 测量失败走兜底 */ }

        double target = desired > 0 ? Math.Clamp(desired, 240, maxH) : maxH;
        DetailPanel.HeightRequest = target;
        _detailCardH = target;
    }

    /// <summary>招式块按 4 招式占位：竖屏用一行实测行高推 2×2 高度，1~4 招卡片高度一致；横屏归零。</summary>
    private void ApplyDetailMovesReserve()
    {
        if (_isLandscape)
        {
            DetailSheetMovesGrid.MinimumHeightRequest = 0;
            return;
        }

        var row  = DetailMoveRow0;
        var name = DetailMoveName0;
        var pp   = DetailMovePP0;
        bool   savedVis  = row.IsVisible;
        string savedName = name.Text;
        string savedPp   = pp.Text;

        double rowH = -1;
        try
        {
            row.IsVisible = true;
            if (string.IsNullOrEmpty(name.Text)) name.Text = "招式";
            if (string.IsNullOrEmpty(pp.Text))   pp.Text   = "PP 99";

            double colW = ((DetailPanel.WidthRequest > 0 ? DetailPanel.WidthRequest : 360) - 32 - 8) / 2;
            if (colW < 60) colW = 60;
            rowH = row.Measure(colW, double.PositiveInfinity).Height;
        }
        catch { /* Measure 偶发失败时走兜底 */ }
        finally
        {
            row.IsVisible = savedVis;
            name.Text     = savedName;
            pp.Text       = savedPp;
        }

        DetailSheetMovesGrid.MinimumHeightRequest = rowH > 20 ? (rowH * 2 + 6) : 72;
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

        // 能力 —— 与箱子页同口径：队伍值优先，否则按 种族值+IV+EV+等级+性格 现场计算（[HP,ATK,DEF,SPE,SPA,SPD]）
        {
            ushort[] st = pk.PartyStatsPresent
                ? [ (ushort)pk.Stat_HPMax, (ushort)pk.Stat_ATK, (ushort)pk.Stat_DEF,
                    (ushort)pk.Stat_SPE, (ushort)pk.Stat_SPA, (ushort)pk.Stat_SPD ]
                : pk.GetStats(pk.PersonalInfo);
            DetailStatHP.Text  = st[0].ToString();
            DetailStatATK.Text = st[1].ToString();
            DetailStatDEF.Text = st[2].ToString();
            DetailStatSPE.Text = st[3].ToString();
            DetailStatSPA.Text = st[4].ToString();
            DetailStatSPD.Text = st[5].ToString();
        }

        // 详情
        DetailInfoAbility.Text = pk.Ability < _strings.abilitylist.Length
            ? _strings.abilitylist[pk.Ability] : "—";
        DetailInfoItem.Text = pk.HeldItem > 0 && pk.HeldItem < _strings.itemlist.Length
            ? _strings.itemlist[pk.HeldItem] : "无";
        DetailInfoGender.Text = pk.Gender switch { 0 => "♂ 雄性", 1 => "♀ 雌性", _ => "无性别" };
        DetailInfoBall.Text = pk.Ball < _strings.balllist.Length
            ? _strings.balllist[pk.Ball] : "—";

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

    private void OnDetailScrimTapped(object? sender, TappedEventArgs e)
    {
        _ = HideDetailAsync();
        DeselectSlot();
    }

    private void OnDetailCloseTapped(object? sender, TappedEventArgs e)
    {
        _ = HideDetailAsync();
        DeselectSlot();
    }
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
        _lastCrySlug = "";   // 同只连按也要能重播
        if (_detailPk is { Species: > 0 } pk) PlayCry((ushort)pk.Species);
    }

    private void PlayCurrentSlotCry()
    {
        _lastCrySlug = "";   // 同只连按也要能重播
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

        bool ok = await CryService.PlayAsync(slug);
        if (!ok && _lastCrySlug == slug) _lastCrySlug = "";   // 失败放开防抖，允许重按
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
