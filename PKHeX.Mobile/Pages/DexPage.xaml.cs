using System.Diagnostics;
using Microsoft.Maui.Controls.Shapes;
using PKHeX.Core;
using PKHeX.Mobile.Services;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace PKHeX.Mobile.Pages;

public partial class DexPage : ContentPage
{
    private const int Columns      = 6;
    private const int TotalSpecies = DexService.MaxSpecies;
    private static int TotalRows   => (TotalSpecies + Columns - 1) / Columns; // 129

    private readonly ISecondaryDisplay        _secondary;
    private readonly SessionState             _session;
    private readonly DexService               _dex;
    private readonly FileSystemSpriteRenderer _renderer = new();
    private readonly GameStrings              _strings  = GameLang.Strings;

    // 图鉴页自己持有的存档视图：不从首页 ActiveSave 实时跟随，只在首次进入时取一次。
    private SaveFile?    _viewSav;
    private DexProgress? _progress;
    private bool         _initialized;

    // 存档选择浮层
    private readonly List<SaveChoice> _choices = [];
    private bool                      _pickerOpen;
    private readonly Func<bool>       _backHandler;

    // Pre-created PKM instances per species for sprite lookups (avoids per-frame alloc).
    // PID=16 ensures non-shiny (XOR of TID/SID/PID words = 16, which is >= shiny threshold).
    private static readonly PK9[] _speciesPks =
        Enumerable.Range(1, TotalSpecies)
            .Select(i => new PK9 { Species = (ushort)i, PID = 16 })
            .ToArray();

    // Type data indexed [1..1025]
    private static readonly (int t1, int t2)[] _typeData = BuildTypeData();

    private static readonly string[] _typeNames =
    [
        "一般","格斗","飞行","毒","地面","岩石",
        "虫","幽灵","钢","火","水","草",
        "电","超能力","冰","龙","恶","妖精","星晶","???",
    ];

    private static readonly Color[] _typePillColors =
    [
        Color.FromArgb("#A8A878"), // Normal
        Color.FromArgb("#C03028"), // Fighting
        Color.FromArgb("#A890F0"), // Flying
        Color.FromArgb("#A040A0"), // Poison
        Color.FromArgb("#E0C068"), // Ground
        Color.FromArgb("#B8A038"), // Rock
        Color.FromArgb("#A8B820"), // Bug
        Color.FromArgb("#705898"), // Ghost
        Color.FromArgb("#B8B8D0"), // Steel
        Color.FromArgb("#F08030"), // Fire
        Color.FromArgb("#6890F0"), // Water
        Color.FromArgb("#78C850"), // Grass
        Color.FromArgb("#F8D030"), // Electric
        Color.FromArgb("#F85888"), // Psychic
        Color.FromArgb("#98D8D8"), // Ice
        Color.FromArgb("#7038F8"), // Dragon
        Color.FromArgb("#705848"), // Dark
        Color.FromArgb("#EE99AC"), // Fairy
        Color.FromArgb("#40B5A5"), // Stellar
        Color.FromArgb("#68A090"), // ???
    ];

    // Virtual scroll state
    private float _scrollOffsetPx;
    private int   _cursorSlot; // 0-based; species = _cursorSlot + 1

    // Cached layout (set on first paint)
    private float _slotSize;
    private float _rowHeight;
    private float _viewWidthPx;
    private float _viewHeightPx;
    private float _canvasTotalHeight;

    // SkiaSharp type colors — parallel to _typePillColors, used for card tinting
    private static readonly SKColor[] _typeSkColors =
        _typePillColors.Select(c => new SKColor(
            (byte)(c.Red   * 255),
            (byte)(c.Green * 255),
            (byte)(c.Blue  * 255))).ToArray();

    // Silhouette color filter matrix (keeps alpha, zeroes RGB)
    private static readonly float[] _silhouetteMatrix =
        [0,0,0,0,0, 0,0,0,0,0, 0,0,0,0,0, 0,0,0,1,0];

    // "见到未捕获"用的去饱和矩阵（保留 25% 色彩，轮廓可辨）
    private static readonly float[] _seenMatrix = BuildSaturationMatrix(0.25f);

    private static float[] BuildSaturationMatrix(float s)
    {
        const float lumR = 0.2126f, lumG = 0.7152f, lumB = 0.0722f;
        float r = lumR * (1 - s), g = lumG * (1 - s), b = lumB * (1 - s);
        return
        [
            r + s, g,     b,     0, 0,
            r,     g + s, b,     0, 0,
            r,     g,     b + s, 0, 0,
            0,     0,     0,     1, 0,
        ];
    }

    // Pan state
    private float _panStartOffset;

    public DexPage(ISecondaryDisplay secondary, DexService dex, SessionState session)
    {
        _secondary = secondary;
        _dex       = dex;
        _session   = session;
        InitializeComponent();

        _backHandler = () =>
        {
            if (!_pickerOpen) return false;
            ClosePicker();
            return true;
        };
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        GamepadRouter.KeyReceived -= OnGamepadKey;
        GamepadRouter.KeyReceived += OnGamepadKey;
#endif
        BackCoordinator.Unbind(_backHandler);
        BackCoordinator.Bind(_backHandler);

        ThemeService.ThemeChanged -= OnThemeChanged;
        ThemeService.ThemeChanged += OnThemeChanged;

        var t0 = Stopwatch.GetTimestamp();
        DiagLog.Add("[DEX] appear enter");

        if (!_initialized)
        {
            // 首次进入：取一次首页当前存档，之后不再跟随首页切换。
            _initialized = true;
            await UseActiveSaveAsync();
        }
        else
        {
            SyncChip();
            UpdateCaughtLabel();
            UpdateFooter();
            PushDexStats();
            DexCanvas.InvalidateSurface();
        }

        DiagLog.Add($"[DEX] appear done {Ms(t0):F0}ms");

        _ = PreloadSpritesAsync();
    }

    private static double Ms(long startTicks)
        => Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        DiagLog.Add("[DEX] disappear");
        try { _dexCts?.Cancel(); } catch { }
        BackCoordinator.Unbind(_backHandler);
#if ANDROID
        GamepadRouter.KeyReceived -= OnGamepadKey;
#endif
        ThemeService.ThemeChanged -= OnThemeChanged;
        _secondary.HideDexStats();
    }

    private void OnThemeChanged()
        => MainThread.BeginInvokeOnMainThread(() => DexCanvas.InvalidateSurface());

    // ── Sprite preload (background, progressive) ───────────────────────────────

    private CancellationTokenSource? _dexCts;

    /// <summary>
    /// 两段式预加载：先只读本地（磁盘 HOME + 内置图）立刻能画出来，
    /// 再在后台慢慢补网络图；离开页面立即取消，不再出现"进图鉴卡住/打不开"。
    /// </summary>
    private async Task PreloadSpritesAsync()
    {
        _dexCts?.Cancel();
        var cts = new CancellationTokenSource();
        _dexCts = cts;
        var ct = cts.Token;

        try
        {
            // 第一段：离线，纯本地
            for (int start = 0; start < TotalSpecies; start += 30)
            {
                if (ct.IsCancellationRequested) return;
                int count = Math.Min(30, TotalSpecies - start);
                var batch = _speciesPks[start..(start + count)].Cast<PKM>().ToArray();
                await _renderer.PreloadLocalAsync(batch, ct);
                DexCanvas.InvalidateSurface();
            }

            // 第二段：后台补网（失败无所谓，本地图已经显示了）
            for (int start = 0; start < TotalSpecies; start += 30)
            {
                if (ct.IsCancellationRequested) return;
                int count = Math.Min(30, TotalSpecies - start);
                var batch = _speciesPks[start..(start + count)].Cast<PKM>().ToArray();
                try { await _renderer.PreloadBoxAsync(batch); } catch { }
                if (ct.IsCancellationRequested) return;
                DexCanvas.InvalidateSurface();
                await Task.Delay(120, ct);
            }
        }
        catch (OperationCanceledException) { /* 离开页面，正常结束 */ }
        catch { /* 预加载失败不影响浏览 */ }
    }

    // ── Canvas painting ────────────────────────────────────────────────────────

    private void OnDexPaint(object sender, SKPaintSurfaceEventArgs e)
    {
        _viewWidthPx  = e.Info.Width;
        _viewHeightPx = e.Info.Height;

        // Proportional layout — scales cleanly to any density/screen size
        float pad  = _viewWidthPx * 0.025f;
        float gap  = _viewWidthPx * 0.012f;
        _slotSize  = (_viewWidthPx - 2 * pad - (Columns - 1) * gap) / Columns;
        _rowHeight = _slotSize + gap;
        _canvasTotalHeight = TotalRows * _rowHeight + 2 * pad;

        var canvas = e.Surface.Canvas;
        canvas.Clear(ThemeService.PageBg);

        // 构建还没回来时只铺底色，避免空引用
        if (_progress is null) return;

        // Only render visible rows (virtual scroll)
        int firstRow = Math.Max(0, (int)((_scrollOffsetPx - pad) / _rowHeight));
        int lastRow  = Math.Min(TotalRows - 1, (int)((_scrollOffsetPx + _viewHeightPx) / _rowHeight) + 1);

        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int col = 0; col < Columns; col++)
            {
                int slot = row * Columns + col;
                if (slot >= TotalSpecies) break;

                float x = pad + col * (_slotSize + gap);
                float y = pad + row * _rowHeight - _scrollOffsetPx;
                DrawSlot(canvas, SKRect.Create(x, y, _slotSize, _slotSize), (ushort)(slot + 1), slot == _cursorSlot);
            }
        }
    }

    private void DrawSlot(SKCanvas canvas, SKRect rect, ushort species, bool isCursor)
    {
        var state = DexService.StateOf(_progress, species);

        // 超出本作图鉴范围：只留一颗灰点，避免玩家以为是"没看到"
        if (state == DexState.OutOfScope)
        {
            using var outBorder = new SKPaint
            {
                Color       = new SKColor(120, 120, 120, 18),
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                IsAntialias = true,
            };
            canvas.DrawRoundRect(rect, 8, 8, outBorder);
            using var dot = new SKPaint { Color = new SKColor(140, 140, 140, 38), IsAntialias = true };
            canvas.DrawCircle(rect.MidX, rect.MidY, Math.Max(2f, _slotSize * 0.02f), dot);
            return;
        }

        var (t1, _)   = _typeData[species];

        SKColor typeColor = (state == DexState.Caught && t1 < _typeSkColors.Length)
            ? _typeSkColors[t1]
            : new SKColor(120, 120, 120);

        // Background — type-tinted for caught, faint for seen/unseen
        byte bgAlpha = state switch
        {
            DexState.Caught => (byte)38,
            DexState.Seen   => (byte)22,
            _               => (byte)10,
        };
        using var bgPaint = new SKPaint
        {
            Color       = typeColor.WithAlpha(bgAlpha),
            IsAntialias = true,
        };
        canvas.DrawRoundRect(rect, 8, 8, bgPaint);

        // Border — caught = type color, seen = neutral gray, unseen = dim gray; cursor overrides
        byte borderAlpha = state switch
        {
            DexState.Caught => (byte)140,
            DexState.Seen   => (byte)95,
            _               => (byte)42,
        };
        SKColor borderColor = isCursor
            ? new SKColor(80, 160, 255, 230)
            : typeColor.WithAlpha(borderAlpha);
        using var borderPaint = new SKPaint
        {
            Color       = borderColor,
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = isCursor ? 2.5f : 1.2f,
            IsAntialias = true,
        };
        canvas.DrawRoundRect(rect, 8, 8, borderPaint);

        // Layout: sprite in upper ~80%, number strip at bottom ~20%
        float numStripH  = _slotSize * 0.20f;
        float spriteAreaH = rect.Height - numStripH;

        // Sprite / silhouette
        var sprite = _renderer.GetSprite(_speciesPks[species - 1]);
        if (sprite.Width > 0 && sprite.Height > 0 && !_renderer.IsBlank(sprite))
        {
            float inner  = spriteAreaH * 0.82f;
            float aspect = (float)sprite.Width / sprite.Height;
            float drawW  = aspect >= 1f ? inner : inner * aspect;
            float drawH  = aspect >= 1f ? inner / aspect : inner;
            float sx     = rect.MidX - drawW / 2f;
            float sy     = rect.Top + spriteAreaH / 2f - drawH / 2f;
            var   destRect = SKRect.Create(sx, sy, drawW, drawH);

            if (state == DexState.Caught)
            {
                canvas.DrawBitmap(sprite, destRect);
            }
            else if (state == DexState.Seen)
            {
                // 见到未捕获：去饱和到 25%，能看清轮廓但与彩色明显区分
                using var seenPaint = new SKPaint
                {
                    ColorFilter = SKColorFilter.CreateColorMatrix(_seenMatrix),
                };
                canvas.DrawBitmap(sprite, destRect, seenPaint);
            }
            else
            {
                using var silPaint = new SKPaint
                {
                    ColorFilter = SKColorFilter.CreateColorMatrix(_silhouetteMatrix),
                };
                canvas.DrawBitmap(sprite, destRect, silPaint);
            }
        }
        else
        {
            // 缺图：画一个淡色问号，避免"整格空白"看起来像 app 坏了
            float qSize = spriteAreaH * 0.42f;
            using var qFont  = new SKFont(SKTypeface.Default, qSize);
            using var qPaint = new SKPaint
            {
                Color = state == DexState.Caught
                    ? new SKColor(200, 200, 200, 150)
                    : new SKColor(140, 140, 140, 90),
                IsAntialias = true,
            };
            canvas.DrawText("?", rect.MidX, rect.Top + spriteAreaH * 0.62f,
                            SKTextAlign.Center, qFont, qPaint);
        }

        // Dex number — 亮度也按状态分三档
        float fontSize = _slotSize * 0.14f;
        using var numFont  = new SKFont(SKTypeface.Default, fontSize);
        byte numAlpha = state == DexState.Caught ? (byte)210 : state == DexState.Seen ? (byte)165 : (byte)120;
        using var numPaint = new SKPaint { Color = new SKColor(200, 200, 200, numAlpha), IsAntialias = true };
        float numY = rect.Bottom - numStripH / 2f + fontSize * 0.38f;
        canvas.DrawText($"#{species:000}", rect.MidX, numY, SKTextAlign.Center, numFont, numPaint);

        // 已捕获：右上角一枚精灵球角标
        if (state == DexState.Caught)
            DrawBallBadge(canvas, rect);
    }

    /// <summary>右上角精灵球：上红下白、黑带、中心圈。</summary>
    private void DrawBallBadge(SKCanvas canvas, SKRect rect)
    {
        float r  = _slotSize * 0.082f;
        float cx = rect.Right - r - _slotSize * 0.05f;
        float cy = rect.Top   + r + _slotSize * 0.05f;

        // 白色球底
        using var bg = new SKPaint { Color = new SKColor(255, 255, 255, 232), IsAntialias = true };
        canvas.DrawCircle(cx, cy, r, bg);

        // 上半红色（裁剪后画圆）
        using var red = new SKPaint { Color = new SKColor(214, 48, 48, 238), IsAntialias = true };
        canvas.Save();
        canvas.ClipRect(SKRect.Create(cx - r, cy - r, r * 2f, r));
        canvas.DrawCircle(cx, cy, r, red);
        canvas.Restore();

        // 中间黑带
        using var band = new SKPaint
        {
            Color = new SKColor(28, 28, 28, 240),
            StrokeWidth = r * 0.20f,
            IsAntialias = true,
        };
        canvas.DrawLine(cx - r, cy, cx + r, cy, band);

        // 外圈描边
        using var ring = new SKPaint
        {
            Color = new SKColor(28, 28, 28, 240),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = r * 0.11f,
            IsAntialias = true,
        };
        canvas.DrawCircle(cx, cy, r, ring);

        // 中心白圈 + 描边
        using var hub = new SKPaint { Color = new SKColor(255, 255, 255, 245), IsAntialias = true };
        canvas.DrawCircle(cx, cy, r * 0.31f, hub);
        using var hubRing = new SKPaint
        {
            Color = new SKColor(28, 28, 28, 235),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = r * 0.09f,
            IsAntialias = true,
        };
        canvas.DrawCircle(cx, cy, r * 0.31f, hubRing);
    }

    // ── Pan gesture (touch scroll) ─────────────────────────────────────────────

    private void OnPan(object sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartOffset = _scrollOffsetPx;
                break;
            case GestureStatus.Running:
                float density = (float)DeviceDisplay.MainDisplayInfo.Density;
                SetScrollOffset(_panStartOffset - (float)e.TotalY * density);
                DexCanvas.InvalidateSurface();
                break;
        }
    }

    private void SetScrollOffset(float value)
    {
        float maxOffset = Math.Max(0, _canvasTotalHeight - _viewHeightPx);
        _scrollOffsetPx = Math.Clamp(value, 0, maxOffset);
    }

    // ── 点击选中 ───────────────────────────────────────────────────────────────

    private void OnDexTapped(object? sender, TappedEventArgs e)
    {
        if (_slotSize <= 0) return;

        var pos = e.GetPosition(DexCanvas);
        if (pos is null) return;

        // GetPosition 给的是 dp，绘制用的是 px
        float density = (float)DeviceDisplay.MainDisplayInfo.Density;
        int slot = HitTestSlot((float)pos.Value.X * density, (float)pos.Value.Y * density);
        if (slot < 0 || slot == _cursorSlot) return;

        _cursorSlot = slot;
        UpdateFooter();
        DexCanvas.InvalidateSurface();
    }

    /// <summary>屏幕像素坐标 → 图鉴格号；横向落在内边距外返回 -1，纵向越界夹到首尾行。</summary>
    private int HitTestSlot(float xPx, float yPx)
    {
        if (_slotSize <= 0 || _viewWidthPx <= 0 || _rowHeight <= 0) return -1;

        float pad  = _viewWidthPx * 0.025f;
        float gap  = _viewWidthPx * 0.012f;
        float step = _slotSize + gap;

        if (xPx < pad || xPx > _viewWidthPx - pad) return -1;

        int col = (int)((xPx - pad) / step);
        int row = (int)((yPx + _scrollOffsetPx - pad) / _rowHeight);

        col = Math.Clamp(col, 0, Columns - 1);
        row = Math.Max(0, row);

        int slot = row * Columns + col;
        return slot >= 0 && slot < TotalSpecies ? slot : -1;
    }

    // ── Gamepad ────────────────────────────────────────────────────────────────

#if ANDROID
    private void OnGamepadKey(Android.Views.Keycode keyCode, Android.Views.KeyEventActions action)
    {
        if (action != Android.Views.KeyEventActions.Down) return;
        MainThread.BeginInvokeOnMainThread(() => HandleGamepadKey(keyCode));
    }

    private void HandleGamepadKey(Android.Views.Keycode keyCode)
    {
        switch (keyCode)
        {
            case Android.Views.Keycode.DpadUp:    MoveCursor(-Columns);     break;
            case Android.Views.Keycode.DpadDown:  MoveCursor(+Columns);     break;
            case Android.Views.Keycode.DpadLeft:  MoveCursor(-1);           break;
            case Android.Views.Keycode.DpadRight: MoveCursor(+1);           break;
            case Android.Views.Keycode.ButtonL1:  JumpToGen(-1); break;
            case Android.Views.Keycode.ButtonR1:  JumpToGen(+1); break;
            case Android.Views.Keycode.ButtonY:
                OpenPicker(); break;
            case Android.Views.Keycode.ButtonB:
                if (_pickerOpen) { ClosePicker(); break; }
                _ = Shell.Current.GoToAsync(".."); break;
            case Android.Views.Keycode.ButtonSelect:
                ScanActiveSave(); break;
        }
    }
#endif

    private void JumpToGen(int delta)
    {
        ushort curSpecies = (ushort)(_cursorSlot + 1);
        int curGen = 0;
        for (int g = 0; g < DexService.Generations.Length; g++)
        {
            if (curSpecies >= DexService.Generations[g].Start &&
                curSpecies <= DexService.Generations[g].End)
            { curGen = g; break; }
        }
        int nextGen = Math.Clamp(curGen + delta, 0, DexService.Generations.Length - 1);
        if (nextGen == curGen) return;
        _cursorSlot = DexService.Generations[nextGen].Start - 1;
        ScrollToCursor();
        UpdateFooter();
        DexCanvas.InvalidateSurface();
    }

    private void MoveCursor(int delta)
    {
        if (delta == -1 && _cursorSlot % Columns == 0)           return;
        if (delta == +1 && _cursorSlot % Columns == Columns - 1) return;
        int next = Math.Clamp(_cursorSlot + delta, 0, TotalSpecies - 1);
        if (next == _cursorSlot) return;
        _cursorSlot = next;
        ScrollToCursor();
        UpdateFooter();
        DexCanvas.InvalidateSurface();
    }

    private void ScrollToCursor()
    {
        if (_slotSize == 0 || _viewHeightPx == 0) return;
        float pad     = _viewWidthPx * 0.025f;
        int   row     = _cursorSlot / Columns;
        float slotTop = pad + row * _rowHeight;
        float slotBot = slotTop + _slotSize;

        if (slotTop < _scrollOffsetPx)
            SetScrollOffset(slotTop - pad);
        else if (slotBot > _scrollOffsetPx + _viewHeightPx)
            SetScrollOffset(slotBot - _viewHeightPx + pad);
    }

    // ── Scan ──────────────────────────────────────────────────────────────────

    private void ScanActiveSave()
    {
        if (_viewSav is null || _progress is null)
        {
            _ = DisplayAlertAsync("无存档", "请先在图鉴页选择一个存档。", "OK");
            return;
        }
        int newly = _dex.Rescan(_progress, _viewSav);
        UpdateCaughtLabel();
        UpdateFooter();
        PushDexStats();
        DexCanvas.InvalidateSurface();

        string msg = newly > 0
            ? $"新解锁 {newly} 只宝可梦。"
            : "这个存档里没有新的宝可梦。";
        _ = DisplayAlertAsync("扫描完成", msg, "OK");
    }

    // ── UI helpers ─────────────────────────────────────────────────────────────

    private void UpdateCaughtLabel()
    {
        if (_progress is null)
        {
            CaughtLabel.Text = "未加载存档";
            DexNote.IsVisible = false;
            return;
        }

        var tail = _progress.HasLegacyOverlay ? " · 含历史" : "";
        CaughtLabel.Text = $"见 {_progress.SeenCount} · 捕 {_progress.CaughtCount} / {_progress.MaxSpecies}{tail}";

        DexNote.Text     = _progress.Note ?? "";
        DexNote.IsVisible = _progress.Note is not null;
    }

    private void UpdateFooter()
    {
        ushort species = (ushort)(_cursorSlot + 1);
        var    state   = DexService.StateOf(_progress, species);

        SpeciesNameLabel.Text = state >= DexState.Seen && species < _strings.specieslist.Length
            ? _strings.specieslist[species]
            : "???";

        string stateText = state switch
        {
            DexState.Caught     => "已捕获",
            DexState.Seen       => "见到未捕获",
            DexState.OutOfScope => "本作未收录",
            _                   => "未见到",
        };
        SpeciesNumLabel.Text = $"#{species:000} · {stateText}";

        TypePills.Children.Clear();
        if (state >= DexState.Seen)
        {
            var (t1, t2) = _typeData[species];
            TypePills.Children.Add(MakeTypePill(t1));
            if (t2 != t1) TypePills.Children.Add(MakeTypePill(t2));
        }
    }

    private View MakeTypePill(int typeIndex)
    {
        string name  = typeIndex < _typeNames.Length      ? _typeNames[typeIndex]      : "???";
        var    color = typeIndex < _typePillColors.Length ? _typePillColors[typeIndex] : Colors.Gray;
        return new Border
        {
            StrokeShape     = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = color.WithAlpha(0.85f),
            Stroke          = Colors.Transparent,
            Padding         = new Thickness(8, 3),
            Content = new Label
            {
                Text            = name,
                FontFamily      = "NunitoBold",
                FontSize        = 10,
                TextColor       = Colors.White,
                VerticalOptions = LayoutOptions.Center,
            },
        };
    }

    private void PushDexStats()
    {
        var byGen = _dex.GetStatsByGen(_progress);
        _secondary.ShowDexStats(
            _progress?.CaughtCount ?? 0,
            _progress?.MaxSpecies ?? TotalSpecies,
            byGen.Select(g => g.caught).ToArray(),
            byGen.Select(g => g.total).ToArray());
    }

    // ── 存档胶囊按钮 + 选择浮层 ────────────────────────────────────────────────

    private async Task UseActiveSaveAsync()
    {
        var sav = _session.ActiveSave;
        if (sav is null)
        {
            _viewSav  = null;
            _progress = null;
            SaveChipName.Text = "未加载存档";
            SaveChipMeta.Text = "点此选择";
            SaveChipIcon.Source = null;
            UpdateCaughtLabel();
            UpdateFooter();
            DexCanvas.InvalidateSurface();
            return;
        }

        await SetViewSaveAsync(sav, _session.ActiveSaveFileUri);
    }

    private async Task SetViewSaveAsync(SaveFile sav, string uri)
    {
        _viewSav  = sav;
        CaughtLabel.Text = "统计中…";

        // 整个图鉴构建（读 1025 只的捕获/见到状态 + 必要时扫箱子）放到后台线程，
        // 否则首次进入会卡住主线程 3-4 秒，期间所有点击（含返回）都会被吞掉。
        var t0 = Stopwatch.GetTimestamp();
        DexProgress progress;
        try
        {
            progress = await Task.Run(() => _dex.Build(sav, uri));
        }
        catch (Exception ex)
        {
            DiagLog.Add($"[DEX] build failed {Ms(t0):F0}ms {ex.Message}");
            CaughtLabel.Text = "统计失败";
            return;
        }

        DiagLog.Add($"[DEX] build {Ms(t0):F0}ms");

        _progress = progress;
        SyncChip();
        UpdateCaughtLabel();
        UpdateFooter();
        PushDexStats();
        DexCanvas.InvalidateSurface();

        DiagLog.Add($"[DEX] first render {Ms(t0):F0}ms");
    }

    private void SyncChip()
    {
        var sav = _viewSav;
        if (sav is null)
        {
            SaveChipName.Text = "未加载存档";
            SaveChipMeta.Text = "点此选择";
            SaveChipIcon.Source = null;
            return;
        }

        SaveChipName.Text = string.IsNullOrWhiteSpace(sav.OT) ? "未命名训练家" : sav.OT;
        SaveChipMeta.Text = $"{sav.Version} · {sav.TID16}/{sav.SID16} · {sav.BoxCount}箱";

        var iconFile = GamePage.GetTrainerIconFile(sav);
        SaveChipIcon.Source = iconFile is null
            ? null
            : ImageSource.FromStream(ct => FileSystem.OpenAppPackageFileAsync($"gameicons/{iconFile}").WaitAsync(ct));
    }

    private void OnSaveChipTapped(object? sender, TappedEventArgs e) => OpenPicker();

    private void OpenPicker()
    {
        if (_pickerOpen) return;

        BuildSaveChoices();
        if (_choices.Count == 0)
        {
            _ = DisplayAlertAsync("没有存档", "请先回首页扫描存档，或加载一个存档。", "OK");
            return;
        }

        SavePickerList.Children.Clear();
        foreach (var c in _choices)
            SavePickerList.Children.Add(BuildChoiceRow(c));

        PickerHint.Text = $"共 {_choices.Count} 个存档";
        SavePickerOverlay.IsVisible = true;
        _pickerOpen = true;
    }

    private void ClosePicker()
    {
        if (!_pickerOpen) return;
        SavePickerOverlay.IsVisible = false;
        _pickerOpen = false;
    }

    private void OnPickerScrimTapped(object? sender, TappedEventArgs e) => ClosePicker();
    private void OnPickerCardTapped(object? sender, TappedEventArgs e) { /* 吃掉点击，别透到蒙层 */ }
    private void OnPickerCloseTapped(object? sender, TappedEventArgs e) => ClosePicker();

    private void BuildSaveChoices()
    {
        _choices.Clear();

        var activeUri = _session.ActiveSaveFileUri;
        var activeSav = _session.ActiveSave;

        // 首页当前存档排最前
        if (activeSav is not null)
            _choices.Add(SaveChoice.FromSave(activeSav, activeUri, _session.ActiveSaveFileName, isActive: true));

        foreach (var entry in _session.LoadedSaves)
        {
            if (entry.FileUri == activeUri) continue;
            if (_choices.Any(c => c.Uri == entry.FileUri)) continue;
            _choices.Add(SaveChoice.FromEntry(entry, isActive: false));
        }
    }

    private View BuildChoiceRow(SaveChoice c)
    {
        var icon = new Image
        {
            WidthRequest = 30,
            HeightRequest = 30,
            Aspect = Aspect.AspectFill,
            VerticalOptions = LayoutOptions.Center,
        };
        var iconFile = GamePage.GetTrainerIconFile(c.Version);
        if (iconFile is not null)
            icon.Source = ImageSource.FromStream(ct => FileSystem.OpenAppPackageFileAsync($"gameicons/{iconFile}").WaitAsync(ct));

        var name = new Label
        {
            Text = string.IsNullOrWhiteSpace(c.Trainer) ? c.FileName : c.Trainer,
            FontFamily = "NunitoExtraBold",
            FontSize = 13,
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        name.SetDynamicResource(Label.TextColorProperty, "ThTextPrimary");

        var meta = new Label
        {
            Text = $"{c.Version} · {c.IdText} · {c.PlayTime} · {c.Boxes}箱",
            FontFamily = "Nunito",
            FontSize = 9,
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        meta.SetDynamicResource(Label.TextColorProperty, "ThTextDim");

        var text = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
        text.Children.Add(name);
        text.Children.Add(meta);

        var mark = new Label
        {
            Text = c.IsActive ? "✓" : "›",
            FontFamily = "NunitoBold",
            FontSize = 13,
            VerticalOptions = LayoutOptions.Center,
        };
        mark.SetDynamicResource(Label.TextColorProperty, c.IsActive ? "ThTextPrimary" : "ThTextDim");

        var grid = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 10 };
        grid.Add(icon, 0);
        grid.Add(text, 1);
        grid.Add(mark, 2);

        var border = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            StrokeThickness = 1,
            Padding = new Thickness(10, 8),
            Content = grid,
        };
        border.SetDynamicResource(VisualElement.BackgroundColorProperty, c.IsActive ? "ThHintBg" : "ThCardBg");
        border.SetDynamicResource(Border.StrokeProperty, "ThHintStroke");

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => _ = SelectChoiceAsync(c);
        border.GestureRecognizers.Add(tap);
        return border;
    }

    private async Task SelectChoiceAsync(SaveChoice c)
    {
        var sav = c.Resolve();
        if (sav is null)
        {
            await DisplayAlertAsync("读取失败", "这个存档解析不出来，请回首页重新扫描。", "OK");
            return;
        }

        ClosePicker();
        await SetViewSaveAsync(sav, c.Uri);
    }

    /// <summary>浮层里的一项：要么是首页已加载的 SaveFile，要么是扫描出来的 SaveEntry（按需解析）。</summary>
    private sealed class SaveChoice
    {
        private readonly SaveEntry? _entry;
        private readonly SaveFile?  _sav;

        public string      Uri      { get; }
        public string      FileName { get; }
        public GameVersion Version  { get; }
        public string      Trainer  { get; }
        public string      IdText   { get; }
        public string      PlayTime { get; }
        public int         Boxes    { get; }
        public bool        IsActive { get; }

        private SaveChoice(SaveEntry? entry, SaveFile? sav, string uri, string fileName,
            GameVersion version, string trainer, string idText, string playTime, int boxes, bool isActive)
        {
            _entry = entry; _sav = sav; Uri = uri; FileName = fileName;
            Version = version; Trainer = trainer; IdText = idText; PlayTime = playTime;
            Boxes = boxes; IsActive = isActive;
        }

        public static SaveChoice FromSave(SaveFile sav, string uri, string fileName, bool isActive)
            => new(null, sav, uri, fileName, sav.Version, sav.OT,
                   $"{sav.TID16}/{sav.SID16}", sav.PlayTimeString, sav.BoxCount, isActive);

        public static SaveChoice FromEntry(SaveEntry e, bool isActive)
            => new(e, null, e.FileUri, e.FileName, e.Version, e.TrainerName,
                   e.TrainerID, e.PlayTime, e.BoxCount, isActive);

        public SaveFile? Resolve()
        {
            if (_sav is not null) return _sav;
            if (_entry is null || _entry.RawData.Length == 0) return null;
            return SaveUtil.TryGetSaveFile(_entry.RawData, out var sav) ? sav : null;
        }
    }

    // ── Type data ──────────────────────────────────────────────────────────────

    private static (int t1, int t2)[] BuildTypeData()
    {
        var result = new (int, int)[TotalSpecies + 1]; // [0] unused
        for (ushort i = 1; i <= TotalSpecies; i++)
        {
            try
            {
                var info = PersonalTable.SV.GetFormEntry(i, 0);
                result[i] = (info.Type1, info.Type2);
            }
            catch
            {
                result[i] = (0, 0);
            }
        }
        return result;
    }
}
