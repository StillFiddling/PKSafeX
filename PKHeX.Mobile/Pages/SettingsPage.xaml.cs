using PKHeX.Core;
using PKHeX.Drawing.Mobile.Sprites;
using PKHeX.Mobile.Services;
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
        UpdateSpriteSummaries();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
#if ANDROID
        GamepadRouter.KeyReceived -= OnGamepadKey;
#endif
    }

    private void BuildRows()
    {
        _rows = [Row_Language, Row_Shiny, Row_Radar, Row_Folders, Row_AutoScan,
                 Row_Legality, Row_Theme, Row_HeroBg, Row_Animated3D, Row_Sprites, Row_AnimSprites,
                 Row_ExportDiag];
    }

    /// <summary>两行入口各显示一行缓存概况，具体操作在二级页完成。</summary>
    private void UpdateSpriteSummaries()
    {
        int homeCount = HomeSpriteCacheService.CountCached();
        int animCount = SpriteCacheService.CountCached();

        HomeSpriteSummaryLabel.Text = homeCount > 0
            ? $"已缓存 {homeCount} 张 · 下载或导入"
            : "下载或导入 HOME 立绘";
        AnimSpriteSummaryLabel.Text = animCount > 0
            ? $"已缓存 {animCount} 张 · 下载或导入"
            : "下载或导入动态立绘";
    }

    private async void OnHomeSpritesTapped(object? sender, EventArgs e)
        => await OpenSpriteSourcePage("home");

    private async void OnAnimSpritesTapped(object? sender, EventArgs e)
        => await OpenSpriteSourcePage("anim");

    private async Task OpenSpriteSourcePage(string kind)
    {
        try { await Shell.Current.GoToAsync($"{nameof(SpriteSourcePage)}?kind={kind}"); }
        catch (Exception ex) { CrashLog.Write("SettingsPage.OnSpritesTapped", ex); }
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
            case 4:  SetToggle(AutoScanSwitch,      delta); break;
            case 5:  SetToggle(LegalitySwitch,      delta); break;
            case 6:  SetToggle(ThemeSwitch,         delta); break;
            case 7:  SetToggle(MoireSwitch,         delta); break;
            case 8:  SetToggle(Animated3DSwitch,    delta); break;
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
                AutoScanSwitch.IsToggled = !AutoScanSwitch.IsToggled;
                break;
            case 5:
                LegalitySwitch.IsToggled = !LegalitySwitch.IsToggled;
                break;
            case 6:
                ThemeSwitch.IsToggled = !ThemeSwitch.IsToggled;
                break;
            case 7:
                MoireSwitch.IsToggled = !MoireSwitch.IsToggled;
                break;
            case 8:
                Animated3DSwitch.IsToggled = !Animated3DSwitch.IsToggled;
                break;
            case 9:
                _ = OpenSpriteSourcePage("home");
                break;
            case 10:
                _ = OpenSpriteSourcePage("anim");
                break;
            case 11:
                _ = ExportDiagAsync();
                break;
        }
    }

    private async void OnExportDiagTapped(object? sender, TappedEventArgs e)
        => await ExportDiagAsync();

    /// <summary>诊断（临时）：把布局日志写成文本，调系统分享发出。</summary>
    private async Task ExportDiagAsync()
    {
        try
        {
            if (DiagLog.Count == 0)
            {
                await DisplayAlertAsync("还没有日志",
                    "请先进箱子页，再进银行页，然后按 B 返回箱子页，来回几次后再导出。", "好");
                return;
            }

            var path = DiagLog.Dump();
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "PKSafe 布局诊断日志",
                File  = new ShareFile(path),
            });
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("导出失败", ex.Message, "好");
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

    // ── 存档文件夹入口，有多少个模拟器就点多少次）────────────────────────

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
