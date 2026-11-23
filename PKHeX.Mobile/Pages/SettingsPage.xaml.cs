using PKHeX.Core;
using PKHeX.Drawing.Mobile.Sprites;
using PKHeX.Mobile.Services;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using static PKHeX.Mobile.Services.ThemeService;

#if ANDROID
using PKHeX.Mobile.Platforms.Android;
#endif

namespace PKHeX.Mobile.Pages;

public partial class SettingsPage : ContentPage
{
    public const string KeyLanguage        = "language";
    public const string KeyShinySprites    = "shiny_sprites";
    public const string KeyAnimated3D      = "animated_3d";
    public const string KeyRadarAdaptive   = "radar_adaptive";
    public const string KeyLegalityBadge   = "legality_badge";
    public const string KeyMoireBg         = "hero_bg_moire";
    public const string KeyAlwaysShowWelcome = "always_welcome";
    public const string KeyAutoScanAll       = "auto_scan_all";

    private static readonly string[] LanguageCodes = ["ja", "en", "fr", "it", "de", "es", "es-419", "ko", "zh-Hans", "zh-Hant"];
    private static readonly string[] LanguageNames = ["日本語", "English", "Français", "Italiano", "Deutsch", "Español", "Español (LATAM)", "한국어", "中文 (简)", "中文 (繁)"];

    private bool _loading;
    private int _focusRow = 0;
    private Border[] _rows = [];

    private readonly SessionState _session;
    private readonly SaveDirectoryService _dirService = new();
#if ANDROID
    private readonly IDirectoryPicker _dirPicker = new AndroidDirectoryPicker();
    private readonly ISaveFilePicker _filePicker = new AndroidFilePicker();
#else
    private readonly IDirectoryPicker _dirPicker = new NullDirectoryPicker();
    private readonly ISaveFilePicker _filePicker = new NullSaveFilePicker();
#endif

    // Download logs
    private readonly System.Text.StringBuilder _spriteLog = new();
    private readonly System.Text.StringBuilder _animLog   = new();
    private int _spriteLogLines;
    private int _animLogLines;
    private const int MaxLogLines = 200;

    /// <summary>当前唯一开放的语言：简体中文。其它语言后续再放开。</summary>
    public const string SupportedLanguage = "zh-Hans";

    public SettingsPage(SessionState session)
    {
        _session = session;
        InitializeComponent();

        // 只开放简体中文，其余语言列表照原样列出但标注「暂未支持」并不可选
        for (int i = 0; i < LanguageNames.Length; i++)
        {
            var name = LanguageNames[i];
            LanguagePicker.Items.Add(
                LanguageCodes[i] == SupportedLanguage ? name : name + "（暂未支持）");
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        GamepadRouter.KeyReceived -= OnGamepadKey;
        GamepadRouter.KeyReceived += OnGamepadKey;
#endif
        HomeSpriteCacheService.BulkProgressChanged += OnBulkProgress;
        HomeSpriteCacheService.SlugCompleted       += OnSpriteSlugCompleted;
        SpriteCacheService.BulkProgressChanged     += OnAnimProgress;
        SpriteCacheService.SlugCompleted           += OnAnimSlugCompleted;
        _loading = true;

        var lang = Preferences.Default.Get(KeyLanguage, SupportedLanguage);
        var idx = Array.IndexOf(LanguageCodes, lang);
        LanguagePicker.SelectedIndex = idx >= 0 ? idx : Array.IndexOf(LanguageCodes, SupportedLanguage);

        ShinySwitch.IsToggled         = Preferences.Default.Get(KeyShinySprites, true);
        Animated3DSwitch.IsToggled    = Preferences.Default.Get(KeyAnimated3D, true);
        RadarAdaptiveSwitch.IsToggled = Preferences.Default.Get(KeyRadarAdaptive, false);
        LegalitySwitch.IsToggled      = Preferences.Default.Get(KeyLegalityBadge, false);
        ThemeSwitch.IsToggled         = ThemeService.Current == PkTheme.Light;
        MoireSwitch.IsToggled         = Preferences.Default.Get(KeyMoireBg, true);
        AutoScanSwitch.IsToggled      = Preferences.Default.Get(KeyAutoScanAll, false);

        _loading = false;

        BuildRows();
        UpdateHighlight();
        UpdateSpriteStatus();
        UpdateAnimSpriteStatus();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
#if ANDROID
        GamepadRouter.KeyReceived -= OnGamepadKey;
#endif
        HomeSpriteCacheService.BulkProgressChanged -= OnBulkProgress;
        HomeSpriteCacheService.SlugCompleted       -= OnSpriteSlugCompleted;
        SpriteCacheService.BulkProgressChanged     -= OnAnimProgress;
        SpriteCacheService.SlugCompleted           -= OnAnimSlugCompleted;
    }

    private void BuildRows()
    {
        _rows = [Row_Language, Row_Shiny, Row_Radar, Row_Folders, Row_AddSave, Row_AutoScan,
                 Row_Legality, Row_Theme, Row_HeroBg, Row_Animated3D, Row_Sprites, Row_AnimSprites];
    }

    private void UpdateHighlight()
    {
        var focusedBg     = Color.FromArgb(ThemeService.Current == PkTheme.Light ? "#EEF2FF" : "#182845");
        var focusedStroke = Color.FromArgb("#4F80FF");
        var normalBg      = Color.FromArgb(ThemeService.Current == PkTheme.Light ? "#FFFFFF" : "#111827");

        for (int i = 0; i < _rows.Length; i++)
        {
            bool focused = i == _focusRow;
            _rows[i].BackgroundColor = focused ? focusedBg : normalBg;
            _rows[i].Stroke          = focused ? focusedStroke : Colors.Transparent;
        }
    }

    private void MoveFocus(int delta)
    {
        _focusRow = Math.Clamp(_focusRow + delta, 0, _rows.Length - 1);
        UpdateHighlight();
        ScrollRowIntoView(_focusRow);
    }

    private void ScrollRowIntoView(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Length) return;
        var row = _rows[rowIndex];
        _ = RootScroll.ScrollToAsync(row, ScrollToPosition.MakeVisible, false);
    }

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
            case Android.Views.Keycode.ButtonB:
                _ = Shell.Current.GoToAsync(".."); break;

            case Android.Views.Keycode.DpadUp:
                MoveFocus(-1); break;

            case Android.Views.Keycode.DpadDown:
                MoveFocus(+1); break;

            case Android.Views.Keycode.DpadLeft:
                AdjustRow(-1); break;

            case Android.Views.Keycode.DpadRight:
                AdjustRow(+1); break;

            case Android.Views.Keycode.ButtonA:
                ActivateRow(); break;
        }
    }
#endif

    private void AdjustRow(int delta)
    {
        switch (_focusRow)
        {
            case 0: // Language — left/right cycles through options
                int count = LanguagePicker.Items.Count;
                if (count == 0) return;
                LanguagePicker.SelectedIndex = Math.Clamp(LanguagePicker.SelectedIndex + delta, 0, count - 1);
                break;

            case 1:  SetToggle(ShinySwitch,         delta); break;
            case 2:  SetToggle(RadarAdaptiveSwitch, delta); break;
            case 5:  SetToggle(AutoScanSwitch,      delta); break;
            case 6:  SetToggle(LegalitySwitch,      delta); break;
            case 7:  SetToggle(ThemeSwitch,         delta); break;
            case 8:  SetToggle(MoireSwitch,         delta); break;
            case 9:  SetToggle(Animated3DSwitch,    delta); break;
        }

        static void SetToggle(Switch sw, int delta)
        {
            // Right = on, Left = off
            if (delta > 0 && !sw.IsToggled) sw.IsToggled = true;
            else if (delta < 0 && sw.IsToggled) sw.IsToggled = false;
        }
    }

    private void ActivateRow()
    {
        switch (_focusRow)
        {
            case 0: // Language — A cycles forward (wraps)
                int count = LanguagePicker.Items.Count;
                if (count > 0)
                    LanguagePicker.SelectedIndex = (LanguagePicker.SelectedIndex + 1) % count;
                break;
            case 1:
                ShinySwitch.IsToggled = !ShinySwitch.IsToggled;
                break;
            case 2:
                RadarAdaptiveSwitch.IsToggled = !RadarAdaptiveSwitch.IsToggled;
                break;
            case 3:
                _ = Shell.Current.GoToAsync(nameof(FolderManagerPage));
                break;
            case 4:
                OnAddSaveClicked(this, EventArgs.Empty);
                break;
            case 5:
                AutoScanSwitch.IsToggled = !AutoScanSwitch.IsToggled;
                break;
            case 6:
                LegalitySwitch.IsToggled = !LegalitySwitch.IsToggled;
                break;
            case 7:
                ThemeSwitch.IsToggled = !ThemeSwitch.IsToggled;
                break;
            case 8:
                MoireSwitch.IsToggled = !MoireSwitch.IsToggled;
                break;
            case 9:
                Animated3DSwitch.IsToggled = !Animated3DSwitch.IsToggled;
                break;
            case 10:
                StartBulkDownload();
                break;
            case 11:
                StartAnimBulkDownload();
                break;
        }
    }

    private void OnLanguageChanged(object sender, EventArgs e)
    {
        if (_loading || LanguagePicker.SelectedIndex < 0) return;
        var lang = LanguageCodes[LanguagePicker.SelectedIndex];

        // 其余语言暂不开放：选中后自动退回简体中文
        if (lang != SupportedLanguage)
        {
            _loading = true;
            LanguagePicker.SelectedIndex = Array.IndexOf(LanguageCodes, SupportedLanguage);
            _loading = false;
            _ = DisplayAlertAsync("暂未支持",
                "目前只开放简体中文，其它语言会陆续加入。", "好");
            return;
        }

        Preferences.Default.Set(KeyLanguage, lang);
        GameInfo.CurrentLanguage = lang;
    }

    private void OnShinySwitchToggled(object sender, ToggledEventArgs e)
    {
        if (_loading) return;
        Preferences.Default.Set(KeyShinySprites, e.Value);
        SpriteName.AllowShinySprite = e.Value;
    }

    private void OnAnimated3DSwitchToggled(object sender, ToggledEventArgs e)
    {
        if (_loading) return;
        Preferences.Default.Set(KeyAnimated3D, e.Value);
    }

    private void OnRadarAdaptiveToggled(object sender, ToggledEventArgs e)
    {
        if (_loading) return;
        Preferences.Default.Set(KeyRadarAdaptive, e.Value);
    }

    private void OnLegalitySwitchToggled(object sender, ToggledEventArgs e)
    {
        if (_loading) return;
        Preferences.Default.Set(KeyLegalityBadge, e.Value);
    }

    private async void OnAutoScanSwitchToggled(object sender, ToggledEventArgs e)
    {
        if (_loading) return;
        Preferences.Default.Set(KeyAutoScanAll, e.Value);
        _session.RescanNeeded = true;

        if (!e.Value) return;

        // 开启时必须先拿到「所有文件访问权限」，否则扫不到 Android/data
        if (AllFilesAccess.IsGranted())
        {
            await DisplayAlertAsync("已开启", "回首页后会重新扫描一次。", "好");
            return;
        }

        bool go = await DisplayAlertAsync(
            "还需要一个权限",
            "自动扫描需要「所有文件访问权限」，否则读不到 Android/data 里的模拟器存档。\n\n"
            + "不想授权的话，把这个开关关掉，改用「新增存档」逐个添加即可。",
            "去授权", "先算了");
        if (go) AllFilesAccess.OpenSettings();
        else
        {
            _loading = true;
            AutoScanSwitch.IsToggled = false;
            _loading = false;
            Preferences.Default.Set(KeyAutoScanAll, false);
        }
    }

    private void OnMoireSwitchToggled(object sender, ToggledEventArgs e)
    {
        if (_loading) return;
        Preferences.Default.Set(KeyMoireBg, e.Value);
    }



    private async void OnThemeSwitchToggled(object sender, ToggledEventArgs e)
    {
        if (_loading) return;
        ThemeService.Apply(e.Value ? PkTheme.Light : PkTheme.Dark);
        UpdateHighlight();
        await DisplayAlertAsync("主题已更改", "重启应用后完全生效。", "OK");
    }

    // ── Sprite pack import (ZIP) ──────────────────────────────────────────────

    private bool _importing;

    private static readonly FilePickerFileType ZipFileType = new(
        new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.Android]      = ["application/zip", "application/x-zip-compressed", "application/octet-stream"],
            [DevicePlatform.WinUI]        = [".zip"],
            [DevicePlatform.MacCatalyst]  = ["public.zip-archive"],
            [DevicePlatform.iOS]          = ["public.zip-archive"],
        });

    private async void OnImportHomeSpritesTapped(object? sender, EventArgs e)
        => await ImportSpritePackAsync(ImportHomeStatusLabel);

    private async void OnImportAnimSpritesTapped(object? sender, EventArgs e)
        => await ImportSpritePackAsync(ImportAnimStatusLabel);

    private async Task ImportSpritePackAsync(Label status)
    {
        if (_importing)
        {
            await DisplayAlertAsync("正在导入", "上一个导入还没结束，请稍候。", "OK");
            return;
        }

        FileResult? picked;
        try
        {
            picked = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "选择精灵包 ZIP",
                FileTypes   = ZipFileType,
            });
        }
        catch (Exception ex)
        {
            status.Text = "打开文件选择器失败：" + ex.Message;
            return;
        }

        if (picked is null) return;

        _importing = true;
        try
        {
            status.Text = "正在导入…（请保持应用在前台）";

            await using var stream = await picked.OpenReadAsync();
            var progress = new Progress<(int Done, int Total)>(p =>
                status.Text = $"正在导入… {p.Done} / {p.Total}");

            var result = await SpritePackInstaller.ImportAsync(stream, progress);

            double homeMb = HomeSpriteCacheService.CacheSizeMb();
            double animMb = SpriteCacheService.CacheSizeMb();
            status.Text = $"{result.Summary()}｜HOME {homeMb:F1} MB，动态 {animMb:F1} MB";

            if (result.Errors.Count > 0)
            {
                string detail = string.Join("\n", result.Errors.Take(8));
                await DisplayAlertAsync("导入完成（有部分失败）",
                    $"{result.Summary()}\n\n{detail}", "OK");
            }
        }
        catch (Exception ex)
        {
            status.Text = "导入失败：" + ex.Message;
        }
        finally
        {
            _importing = false;
        }
    }

    // ── HOME sprite download ──────────────────────────────────────────────────

    private void OnSpriteDownloadTapped(object? sender, EventArgs e) => StartBulkDownload();

    private void StartBulkDownload()
    {
        if (HomeSpriteCacheService.IsBulkDownloading)
        {
            HomeSpriteCacheService.StopBulk();
            return;
        }
        _spriteLog.Clear();
        _spriteLogLines  = 0;
        SpriteLogLabel.Text = "";
        SpriteLogPanel.IsVisible = false;
        _ = HomeSpriteCacheService.BulkDownloadAsync();
        UpdateSpriteStatus();
    }

    private void OnBulkProgress(int done, int total)
    {
        SpriteRingCanvas.InvalidateSurface();
        UpdateSpriteStatus();
    }

    private void OnSpriteSlugCompleted(string slug, bool success)
    {
        AppendLog(SpriteLogPanel, SpriteLogLabel, SpriteLogScroll,
                  _spriteLog, ref _spriteLogLines, slug, success);
    }

    private void UpdateSpriteStatus()
    {
        var (done, total, failed) = HomeSpriteCacheService.BulkProgress;
        bool running = HomeSpriteCacheService.IsBulkDownloading;

        if (running)
        {
            SpriteStatusLabel.Text = $"正在下载…  {done} / {total}（再次点按可停止）";
            SpriteRingLabel.Text   = $"{done}";
        }
        else if (total > 0)
        {
            string failNote = failed > 0 ? $"  ·  失败 {failed} 个" : "";
            SpriteStatusLabel.Text = $"已缓存 {done} 张{failNote}";
            SpriteRingLabel.Text   = "✓";
        }
        else
        {
            SpriteStatusLabel.Text = "约 120 MB · 缓存全部宝可梦的 HOME 立绘";
            SpriteRingLabel.Text   = "";
        }
    }

    private void OnSpriteRingPaint(object sender, SKPaintSurfaceEventArgs e)
        => PaintProgressRing(e, HomeSpriteCacheService.BulkProgress, HomeSpriteCacheService.IsBulkDownloading);

    // ── Animated sprite download ──────────────────────────────────────────────

    private void OnAnimSpriteDownloadTapped(object? sender, EventArgs e) => StartAnimBulkDownload();

    private void StartAnimBulkDownload()
    {
        if (SpriteCacheService.IsBulkDownloading)
        {
            SpriteCacheService.StopBulk();
            return;
        }
        _animLog.Clear();
        _animLogLines  = 0;
        AnimLogLabel.Text = "";
        AnimLogPanel.IsVisible = false;
        _ = SpriteCacheService.BulkDownloadAllAsync();
        UpdateAnimSpriteStatus();
    }

    private void OnAnimProgress(int done, int total)
    {
        AnimRingCanvas.InvalidateSurface();
        UpdateAnimSpriteStatus();
    }

    private void OnAnimSlugCompleted(string slug, bool success)
    {
        AppendLog(AnimLogPanel, AnimLogLabel, AnimLogScroll,
                  _animLog, ref _animLogLines, slug, success);
    }

    private void UpdateAnimSpriteStatus()
    {
        var (done, total, failed) = SpriteCacheService.BulkProgress;
        bool running = SpriteCacheService.IsBulkDownloading;

        if (running)
        {
            AnimSpriteStatusLabel.Text = $"正在下载…  {done} / {total}（再次点按可停止）";
            AnimRingLabel.Text         = $"{done}";
        }
        else if (total > 0)
        {
            string failNote = failed > 0 ? $"  ·  失败 {failed} 个" : "";
            AnimSpriteStatusLabel.Text = $"已缓存 {done} 张{failNote}";
            AnimRingLabel.Text         = "✓";
        }
        else
        {
            AnimSpriteStatusLabel.Text = "约 200 MB · 缓存全部宝可梦的动态立绘 GIF";
            AnimRingLabel.Text         = "";
        }
    }

    private void OnAnimRingPaint(object sender, SKPaintSurfaceEventArgs e)
        => PaintProgressRing(e, SpriteCacheService.BulkProgress, SpriteCacheService.IsBulkDownloading);

    // ── Shared log helper ─────────────────────────────────────────────────────

    private void AppendLog(Border panel, Label label, ScrollView scroll,
                           System.Text.StringBuilder sb, ref int lineCount,
                           string slug, bool success)
    {
        if (success) return;

        panel.IsVisible = true;

        // Trim oldest lines when cap is reached
        if (lineCount >= MaxLogLines)
        {
            var text  = sb.ToString();
            int cut   = 0;
            int found = 0;
            // Drop first MaxLogLines/4 lines
            int dropCount = MaxLogLines / 4;
            while (found < dropCount && cut < text.Length)
            {
                if (text[cut++] == '\n') found++;
            }
            sb.Clear();
            sb.Append(text.AsSpan(cut));
            lineCount -= found;
        }

        sb.Append("✗ ");
        sb.AppendLine(slug);
        lineCount++;

        label.Text = sb.ToString();
        _ = scroll.ScrollToAsync(0, double.MaxValue, false);
    }

    // ── Shared ring painter ───────────────────────────────────────────────────

    private static void PaintProgressRing(SKPaintSurfaceEventArgs e, (int Done, int Total, int Failed) progress, bool running)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var (done, total, _) = progress;
        float pct = total > 0 ? Math.Clamp((float)done / total, 0f, 1f) : 0f;

        float cx = e.Info.Width  / 2f;
        float cy = e.Info.Height / 2f;
        float r  = Math.Min(cx, cy) - 4f;

        bool light = ThemeService.Current == PkTheme.Light;

        using var trackPaint = new SKPaint
        {
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 5f,
            Color       = light ? new SKColor(200, 210, 230) : new SKColor(40, 55, 80),
            IsAntialias = true,
        };
        canvas.DrawCircle(cx, cy, r, trackPaint);

        if (!running && total == 0)
        {
            using var arrowPaint = new SKPaint { Color = new SKColor(100, 160, 220), IsAntialias = true, StrokeWidth = 2.5f, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
            float ax = cx, ay1 = cy - r * 0.45f, ay2 = cy + r * 0.2f;
            canvas.DrawLine(ax, ay1, ax, ay2, arrowPaint);
            using var path = new SKPath();
            path.MoveTo(ax - r * 0.3f, ay2 - r * 0.25f);
            path.LineTo(ax, ay2);
            path.LineTo(ax + r * 0.3f, ay2 - r * 0.25f);
            canvas.DrawPath(path, arrowPaint);
            return;
        }

        var arcColor = !running && total > 0
            ? new SKColor(52, 217, 144)
            : new SKColor(59, 139, 255);

        using var arcPaint = new SKPaint
        {
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 5f,
            Color       = arcColor,
            IsAntialias = true,
            StrokeCap   = SKStrokeCap.Round,
        };

        float sweep = pct * 360f;
        var rect = new SKRect(cx - r, cy - r, cx + r, cy + r);
        canvas.DrawArc(rect, -90f, sweep, false, arcPaint);
    }

    // ── 新增存档（逐个添加，有多少个模拟器就点多少次）────────────────────────

    /// <summary>点击「管理存档文件夹」进入管理页（手柄和触摸都能进）。</summary>
    private async void OnFoldersTapped(object? sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync(nameof(FolderManagerPage));
        }
        catch (Exception ex)
        {
            CrashLog.Write("SettingsPage.OnFoldersTapped", ex);
        }
    }

    private async void OnAddSaveClicked(object sender, EventArgs e)
    {
        try
        {
            var uri = await _filePicker.PickFileAsync();
            if (uri is null) return;

            // 先验证：能不能识别成存档，避免"加进去了但首页还是读不到"
            var entry = await _dirService.ScanFileAsync(uri);
            if (entry is null)
            {
                await DisplayAlertAsync(
                    "无法识别",
                    "这个文件不是可识别的宝可梦存档。\n\n"
                    + "3DS 一般是没有扩展名的 main 文件，NDS 是 .dsv / .sav，GBA 是 .sav，"
                    + "Switch 是 main。请确认选的是存档本身，不是 ROM 或存档文件夹。",
                    "好");
                return;
            }

            _dirService.AddFile(uri);
            SessionState.RequestAdd(uri, true);   // 首页只读这一个，不触发全量重扫
            _session.RescanNeeded = false;
            ScanReport.Add($"手动新增存档：{entry.FileName}（第 {entry.Generation} 世代）");

            await DisplayAlertAsync(
                "已新增存档",
                $"第 {entry.Generation} 世代 · {entry.Version}\n"
                + $"训练家：{entry.TrainerName}\n"
                + $"游戏时间：{entry.PlayTime}\n\n"
                + "已加入存档列表，回首页就能看到。还有别的模拟器就再点一次「新增存档」。",
                "好");
        }
        catch (Exception ex)
        {
            CrashLog.Write("SettingsPage.OnAddSaveClicked", ex);
            await DisplayAlertAsync("新增失败", ex.Message, "好");
        }
    }

    // ── Stub pickers for non-Android ─────────────────────────────────────────

    private sealed class NullSaveFilePicker : ISaveFilePicker
    {
        public Task<string?> PickFileAsync() => Task.FromResult<string?>(null);
    }

    private sealed class NullDirectoryPicker : IDirectoryPicker
    {
        public Task<string?> PickDirectoryAsync() => Task.FromResult<string?>(null);
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Apply persisted settings on app startup.
    /// </summary>
    public static void ApplyOnStartup()
    {
        // 强制简体中文：老用户偏好里存的 "en"（或其它语言）一次性迁移过来
        var lang = Preferences.Default.Get(KeyLanguage, SupportedLanguage);
        if (lang != SupportedLanguage)
        {
            lang = SupportedLanguage;
            Preferences.Default.Set(KeyLanguage, lang);
        }
        GameInfo.CurrentLanguage = lang;
        SpriteName.AllowShinySprite = Preferences.Default.Get(KeyShinySprites, true);
    }
}
