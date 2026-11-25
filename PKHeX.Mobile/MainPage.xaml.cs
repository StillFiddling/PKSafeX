using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using PKHeX.Core;
using PKHeX.Mobile.Pages;
using PKHeX.Mobile.Services;
using PKHeX.Mobile.Theme;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace PKHeX.Mobile;

public partial class MainPage : ContentPage
{
    private readonly ISecondaryDisplay _secondary;
    private readonly SessionState _session;
    private readonly SaveDirectoryService _dirService = new();
    private readonly IFileService _fileService = new FileService();
    private List<SaveCardViewModel> _saveCards = [];

    // Focus zones: 0 = save list, 1 = action bar
    private int _focusSection;
    private int _cardCursor = -1;

    // Action bar sub-focus: 0 = primary button, 1–4 = tiles (Search, Gifts, Export, Bank)
    private int _actionCursor;

    private Border[] _actionTiles = [];
    private Image[]  _partyImages = [];
    private SaveEntry? _selectedSave;
    private bool _gpNavigating;
    private int  _lastHeroDisplayIndex = -2; // -2 = never shown

    // Floating card animation
    private IDispatcherTimer? _floatTimer;
    private DateTime          _floatStart = DateTime.UtcNow;

    // Hero game colors (used by Moiré canvas + glow/stroke accents)
    private Color    _heroColorLight  = Colors.Transparent;
    private Color    _heroColorDark   = Colors.Transparent;
    // Duotone Moiré — set for B2/W2, Empty otherwise
    private SKColor  _moireBase       = SKColor.Empty;
    private SKColor  _moireAccent     = SKColor.Empty;
    // Classic gradient (used when Moiré is disabled)
    private GradientStop? _heroTopStop;
    private GradientStop? _heroMidStop;

    // Moiré renderer — cached to avoid per-frame allocations
    private static readonly string   MoireChars       = " .,-~:;=!*#";
    private static readonly string[] MoireCharStrings = MoireChars.Select(c => c.ToString()).ToArray();
    private static readonly SKTypeface MoireTypeface  =
        SKTypeface.FromFamilyName("monospace") ?? SKTypeface.Default;

    // Hero cross-fade cancellation
    private CancellationTokenSource? _heroAnimCts;

    public MainPage(ISecondaryDisplay secondary, SessionState session)
    {
        _secondary = secondary;
        _session   = session;
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            // 记录到 crash.log，下次启动可看到真实原因；这里不再往上抛以免整个进程直接死掉
            CrashLog.Write("MainPage.InitializeComponent", ex);
        }

        // 双击「N 个存档」小字 → 扫描诊断（看清每个文件夹为什么读不到存档）
        try
        {
            var tap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
            tap.Tapped += async (_, _) => await ShowScanReportAsync();
            SaveCountLabel?.GestureRecognizers.Add(tap);
        }
        catch (Exception ex)
        {
            CrashLog.Write("MainPage.ScanReportGesture", ex);
        }
    }

    private async Task ShowScanReportAsync()
    {
        try
        {
            var closeBtn = new Button { Text = "关闭", HorizontalOptions = LayoutOptions.End };
            var permBtn = new Button
            {
                Text = AllFilesAccess.IsGranted() ? "所有文件访问：已授权" : "授予「所有文件访问权限」",
                IsEnabled = !AllFilesAccess.IsGranted(),
            };
            var page = new ContentPage
            {
                Title = "扫描诊断",
                Content = new ScrollView
                {
                    Content = new VerticalStackLayout
                    {
                        Padding = 16,
                        Children =
                        {
                            closeBtn,
                            permBtn,
                            new Label
                            {
                                Text = ScanReport.Text,
                                FontSize = 11,
                                LineBreakMode = LineBreakMode.WordWrap,
                            },
                        },
                    },
                },
            };
            permBtn.Clicked += (_, _) => AllFilesAccess.OpenSettings();
            closeBtn.Clicked += async (_, _) =>
            {
                try { await Shell.Current.Navigation.PopModalAsync(); }
                catch (Exception ex) { CrashLog.Write("MainPage.PopScanReport", ex); }
            };
            await Shell.Current.Navigation.PushModalAsync(page);
        }
        catch (Exception ex)
        {
            CrashLog.Write("MainPage.ShowScanReport", ex);
            await DisplayAlertAsync("扫描诊断", ScanReport.Text.Length > 900
                ? ScanReport.Text[..900] + "\n…"
                : ScanReport.Text, "关闭");
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        _homeEnterTicks = Stopwatch.GetTimestamp();
        DiagLog.Add("[HOME] appear enter");

        try
        {
            await OnAppearingCoreAsync();
        }
        catch (Exception ex)
        {
            // 首页初始化出错不允许直接闪退：记录下来并提示，界面仍可用
            CrashLog.Write("MainPage.OnAppearing", ex);
            await ShowCrashAlertAsync("首页初始化异常", ex.Message);
        }

        DiagLog.Add($"[HOME] appear done {HomeMs():F0}ms");
    }

    private long _homeEnterTicks;

    private double HomeMs()
        => _homeEnterTicks == 0 ? 0 : Stopwatch.GetElapsedTime(_homeEnterTicks).TotalMilliseconds;

    private async Task OnAppearingCoreAsync()
    {
        bool showWelcome = false;
        try
        {
            showWelcome = WelcomePage.ShouldShowWelcome();
        }
        catch (Exception ex)
        {
            CrashLog.Write("MainPage.WelcomeCheck", ex);
        }

        if (showWelcome)
        {
            await Shell.Current.GoToAsync(nameof(WelcomePage), false);
            return;
        }

#if ANDROID
        TryStep("GamepadRouter", () =>
        {
            GamepadRouter.KeyReceived -= OnGamepadKey;
            GamepadRouter.KeyReceived += OnGamepadKey;
        });
#endif
        TryStep("ThemeChanged", () =>
        {
            ThemeService.ThemeChanged -= OnThemeChanged;
            ThemeService.ThemeChanged += OnThemeChanged;
        });
        TryStep("Secondary.Show", () => _secondary.Show());

        _actionTiles = [Tile_Gifts, Tile_Export, Tile_Bank, Tile_Dex, Tile_Settings];
        _partyImages  = [Party0, Party1, Party2, Party3, Party4, Party5];

        TryStep("Secondary.IsAvailable", () =>
        {
            bool dual = _secondary.IsAvailable;
            BottomPanel.IsVisible = !dual;
            ApplyMainPageLayout(dual, Width > Height);
        });

        // 主线程卡顿探针：判断「点不动」到底是主线程被占，还是输入/导航本身丢失
        DiagLog.StartWatchdog();

        // 先把上次扫描结果从磁盘缓存读出来：「重进软件」不再面对空白等好几分钟
        bool usedCache = false;
        if (_session.LoadedSaves.Count == 0)
        {
            var cached = SaveIndexCache.Load();
            if (cached is { Count: > 0 })
            {
                _session.LoadedSaves = cached;
                usedCache = true;
            }
        }

        // 管理页等页面没有 SessionState 实例，通过静态标记要求重扫
        if (SessionState.PendingFullRescan)
        {
            SessionState.PendingFullRescan = false;
            _session.RescanNeeded = true;
        }

        if (SessionState.PendingAddCount > 0)
        {
            // 只处理刚新增的路径：已有卡片先原样铺上，新存档读到一个加一个
            if (_session.LoadedSaves.Count > 0)
                TryStep("ApplySaveEntries", () => ApplySaveEntries(_session.LoadedSaves));
            _ = AddPendingSafeAsync();
        }
        else if (!_homeBootstrapped && _session.LoadedSaves.Count > 0)
        {
            // 冷启动 + 有缓存：卡片立刻上屏，后台只校验「磁盘上变过的文件」，
            // 全量扫描推迟到空闲时再做（负责发现新加进来的存档），
            // 首页第一屏不再等那 6~7 秒的整盘遍历。
            // 注意 RescanNeeded 默认是 true，这里必须一并清掉，否则下次回首页又会立刻全扫。
            _session.RescanNeeded = false;
            TryStep("ApplySaveEntries", () => ApplySaveEntries(_session.LoadedSaves));
            _ = StartupVerifyThenIdleScanAsync();
        }
        else if (_session.RescanNeeded || _session.LoadedSaves.Count == 0)
        {
            // 明确要求重扫（管理页改过路径等）或一个缓存都没有：已有卡片先铺上，后台全量扫
            if (_session.LoadedSaves.Count > 0)
                TryStep("ApplySaveEntries", () => ApplySaveEntries(_session.LoadedSaves));
            _ = RefreshSavesSafeAsync();
        }
        else
        {
            // Returning from GamePage — list is identical, just restore highlights.
            TryStep("RestoreAfterGamePage", RestoreAfterGamePage);
            // 缓存铺出来的列表：后台只校验「有变化的文件」，不改动其它卡片
            if (usedCache) _ = VerifyCachedSafeAsync();
        }
        _homeBootstrapped = true;
        TryStep("UpdateActionHighlight", UpdateActionHighlight);
        TryStep("BindHomeHintTaps", BindHomeHintTaps);

        // 走到这里说明首页已可用，清除「启动未完成」标记
        CrashLog.MarkBootHealthy();

        // 上次若发生过未捕获异常，这里把摘要亮出来（可截图反馈，也可清理）
        await ShowCrashAlertAsync(null, null);
    }

    /// <summary>执行一个启动步骤，失败只记录不抛出。</summary>
    private static void TryStep(string tag, Action step)
    {
        try { step(); }
        catch (Exception ex) { CrashLog.Write($"MainPage.{tag}", ex); }
    }

    private bool _refreshRunning;
    private bool _homeBootstrapped;

    /// <summary>
    /// 冷启动的省时路径：先用缓存卡片上屏，立刻做一次「只查变过的文件」的校验，
    /// 等界面稳定 8 秒后再在后台补一次全量扫描（负责发现新加进来的存档）。
    /// 全程都在后台线程，主线程只接收结果。
    /// </summary>
    private async Task StartupVerifyThenIdleScanAsync()
    {
        await VerifyCachedSafeAsync();
        await Task.Delay(8000);
        if (_refreshRunning)
        {
            DiagLog.Add("[HOME] idle scan skipped (refresh running)");
            return;
        }
        DiagLog.Add("[HOME] idle scan start");
        await RefreshSavesSafeAsync();
    }

    private async Task RefreshSavesSafeAsync()
    {
        // 返回首页会再次触发刷新；上一次还没跑完就直接复用它的结果，不叠第二遍。
        if (_refreshRunning)
        {
            DiagLog.Add("[HOME] refresh skipped (already running)");
            return;
        }

        _refreshRunning = true;
        MarkRefreshing(true);
        var t0 = Stopwatch.GetTimestamp();
        DiagLog.Add("[HOME] refresh start");
        try
        {
            await RefreshSavesAsync();
            DiagLog.Add($"[HOME] refresh done {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0}ms");
        }
        catch (Exception ex)
        {
            CrashLog.Write("MainPage.RefreshSavesAsync", ex);
        }
        finally
        {
            _refreshRunning = false;
            MarkRefreshing(false);
        }
    }

    /// <summary>校验缓存列表：只重读被外部改过的文件，其余原样保留。</summary>
    private async Task VerifyCachedSafeAsync()
    {
        MarkRefreshing(true);
        var t0 = Stopwatch.GetTimestamp();
        DiagLog.Add("[HOME] verify start");
        try
        {
            var verified = await Task.Run(async () => await _dirService.VerifyCachedAsync(_session.LoadedSaves));
            DiagLog.Add($"[HOME] verify done {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0}ms");
            if (verified.Count == 0) return;
            _session.LoadedSaves = verified;
            ApplySaveEntries(verified);
            SaveIndexCache.Save(verified);
        }
        catch (Exception ex)
        {
            CrashLog.Write("MainPage.VerifyCached", ex);
        }
        finally
        {
            MarkRefreshing(false);
        }
    }

    /// <summary>刷新期间只在计数旁标注「刷新中…」，不动已有卡片。</summary>
    private void MarkRefreshing(bool refreshing)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var n = Math.Max(_session.LoadedSaves.Count, _saveCards.Count);
            SaveCountLabel.Text = refreshing ? $"{n} 个存档 · 刷新中…" : $"{n} 个存档";
        });
    }

    /// <summary>增量处理「刚新增的存档」，不重扫、不清空、不重读其它文件。</summary>
    private async Task AddPendingSafeAsync()
    {
        var pending = SessionState.TakePendingAdds();
        if (pending.Count == 0) return;

        MarkRefreshing(true);
        var added = 0;
        foreach (var (uri, isFile) in pending)
        {
            try
            {
                var found = await _dirService.ScanSingleAsync(uri, isFile);
                foreach (var e in found)
                {
                    if (_session.LoadedSaves.Any(s => s.FileUri == e.FileUri)) continue;
                    _session.LoadedSaves.Add(e);
                    added++;
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("MainPage.AddPending", ex);
            }
        }
        MarkRefreshing(false);

        if (added > 0)
        {
            ApplySaveEntries(_session.LoadedSaves);
            SaveIndexCache.Save(_session.LoadedSaves);
        }
        else if (pending.Count > 0)
            await DisplayAlertAsync(
                "没读到存档",
                "新增的路径里没有识别出存档。点击首页「N 个存档」小字双击可以打开扫描诊断，看具体原因。",
                "好");
    }

    /// <summary>显示崩溃摘要。title/message 非空时表示本次刚发生错误。</summary>
    private async Task ShowCrashAlertAsync(string? title, string? message)
    {
        try
        {
            if (title is not null)
            {
                await DisplayAlertAsync(title, message ?? "未知错误", "确定");
                return;
            }

            if (!CrashLog.HasLog) return;

            // 对外测试版：不把原始堆栈糊到玩家脸上，只说明已记录、怎么反馈。
            bool clear = await DisplayAlertAsync(
                "上次退出不太正常",
                "上次运行可能异常结束，相关信息已记录下来（不影响存档）。\n\n"
                + "如果之后遇到卡顿或闪退，可以在设置里打开「诊断记录」，再点「导出布局日志」发给作者。",
                "知道了", "清除这条记录");
            if (clear) CrashLog.Clear();
        }
        catch (Exception ex)
        {
            CrashLog.Write("MainPage.ShowCrashAlert", ex);
        }
    }

    // 底部提示徽章 → 真按键（导航 / 选择 / 返回 / 搜索）
    private void BindHomeHintTaps()
    {
        if (HomeHintBar is null) return;
        if (HomeHintBar.Children.Count < 4) return;
        var actions = new Action[]
        {
            () => MoveDown(),                       // ↕ 导航
            () => _ = OnAPressed(),                 // A 选择
            GoBackOrQuit,                           // B 返回
            () => _ = RefreshSavesAsync(),          // X 搜索
        };
        for (int i = 0; i < 4; i++)
        {
            if (HomeHintBar.Children[i] is not View v) continue;
            if (v.GestureRecognizers.Count > 0) continue;
            int idx = i;
            v.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => { Haptic(); actions[idx](); }),
            });
        }
    }

    private DateTime _quitPressAt = DateTime.MinValue;

    internal void GoBackOrQuit()
    {
        // 首页没有上层页面：B 键第一次只提示，同一提示期内再按一次才退出
        var now = DateTime.Now;
        if ((now - _quitPressAt).TotalMilliseconds <= 2500)
        {
            Application.Current?.Quit();
            return;
        }

        _quitPressAt = now;
        QuitToast.IsVisible = true;
        QuitToast.Opacity = 1;
        _ = HideQuitToastAsync();
    }

    private async Task HideQuitToastAsync()
    {
        await Task.Delay(2500);
        if ((DateTime.Now - _quitPressAt).TotalMilliseconds > 2500)
            QuitToast.IsVisible = false;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
#if ANDROID
        GamepadRouter.KeyReceived -= OnGamepadKey;
#endif
        ThemeService.ThemeChanged -= OnThemeChanged;
        _floatTimer?.Stop();
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0 || height <= 0) return;
        bool dual = _secondary.IsAvailable;
        if (!dual) ApplyMainPageLayout(dual, width > height);
    }

    /// <summary>
    /// Portrait single-screen: hero Auto top, save list Star below.
    /// Landscape single-screen: two columns — hero left (*), save list right (*).
    /// Thor (dual): hero fills full screen (Row 0 = *), BottomPanel hidden.
    /// </summary>
    private void ApplyMainPageLayout(bool dual, bool landscape)
    {
        if (dual)
        {
            RootGrid.RowDefinitions[0].Height   = GridLength.Star;
            RootGrid.RowDefinitions[1].Height   = new GridLength(0);
            RootGrid.ColumnDefinitions[0].Width = GridLength.Star;
            RootGrid.ColumnDefinitions[1].Width = new GridLength(0);
            return;
        }

        if (landscape)
        {
            // Two-column: hero left, save list + action bar right — both fill height
            RootGrid.RowDefinitions[0].Height   = GridLength.Star;
            RootGrid.RowDefinitions[1].Height   = new GridLength(0);
            RootGrid.ColumnDefinitions[0].Width = GridLength.Star;
            RootGrid.ColumnDefinitions[1].Width = GridLength.Star;
            Grid.SetRow(BottomPanel, 0);
            Grid.SetColumn(BottomPanel, 1);
            HeroPanel.MinimumHeightRequest = 0; // height is unconstrained — fills column
        }
        else
        {
            // Portrait: hero Auto on top, save list fills below
            RootGrid.RowDefinitions[0].Height   = GridLength.Auto;
            RootGrid.RowDefinitions[1].Height   = GridLength.Star;
            RootGrid.ColumnDefinitions[0].Width = GridLength.Star;
            RootGrid.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetRow(BottomPanel, 1);
            Grid.SetColumn(BottomPanel, 0);
            HeroPanel.MinimumHeightRequest = 190;
        }
    }

    private void OnThemeChanged()
    {
        foreach (var card in _saveCards)
            card.RefreshTheme();
        UpdateActionHighlight();
    }

    // ── Data ─────────────────────────────────────────────────────────────────

    private async Task RefreshSavesAsync()
    {
        // 整条扫描链（SAF 遍历、逐个存档解析、SHA-256 去重）都在主线程上跑过，
        // 实测把 UI 线程占住 6 秒，期间点图鉴、点返回全被吞掉。
        // 现在整段丢到后台线程，主线程只负责最后一次性刷新列表。
        var entries = await Task.Run(async () => await _dirService.ScanAllAsync());

        // 本轮扫描不完整（撞目录/文件上限、有目录读不到）时，不能用它去覆写
        // 首页和缓存：否则又一次出现「刚进来少一条，过一会又回来」。
        if (ScanReport.IsIncomplete && _session.LoadedSaves.Count > 0
            && entries.Count < _session.LoadedSaves.Count)
        {
            DiagLog.Add($"[HOME] refresh incomplete ({ScanReport.IncompleteReason}), keep {_session.LoadedSaves.Count} vs {entries.Count}");
            _session.RescanNeeded = false;
            return;
        }

        _session.LoadedSaves = entries;
        _session.RescanNeeded = false;
        ApplySaveEntries(entries);
        SaveIndexCache.Save(entries);   // 下次进软件先读缓存，秒开
    }

    private void ApplySaveEntries(List<SaveEntry> entries)
    {
        var t0 = Stopwatch.GetTimestamp();
        DiagLog.Add($"[HOME] apply start {entries.Count} 个");
        _saveCards = entries.Select(e => new SaveCardViewModel(e)).ToList();
        SaveCardsList.ItemsSource = _saveCards;
        SaveCountLabel.Text = $"{_saveCards.Count} 个存档";

        // Re-clamp cursor and re-apply IsCursor on fresh card objects
        if (_saveCards.Count > 0)
        {
            int clamped = Math.Max(0, Math.Min(_cardCursor < 0 ? 0 : _cardCursor, _saveCards.Count - 1));
            _cardCursor = -1; // force SetCardCursor to treat it as new
            SetCardCursor(clamped);
        }

        // 首次进入：默认选中第一个存档（真正加载，不只是框选）
        if (_session.ActiveSave is null && _saveCards.Count > 0)
        {
            SetCardCursor(0);
            _gpNavigating = true;
            SaveCardsList.SelectedItem = _saveCards[0];
            _gpNavigating = false;
            _ = LoadSaveAsync(_saveCards[0].Entry);
        }

        // Restore active save highlight when returning from GamePage
        if (_session.ActiveSaveFileUri is { Length: > 0 } uri)
        {
            var active = _saveCards.FirstOrDefault(c => c.Entry.FileUri == uri);
            if (active != null)
            {
                active.IsLoaded = true;
                _selectedSave = active.Entry;
                SetCardCursor(_saveCards.IndexOf(active));
                _gpNavigating = true;
                SaveCardsList.SelectedItem = active;
                _gpNavigating = false;
            }
            else
            {
                // 正在玩的那条存档这轮不在列表里（被删 / 扫描没读到）：
                // 必须把选中态和 Hero 一起回落，否则会出现「高亮在旧卡、Hero 是空的」。
                DiagLog.Add("[HOME] active save missing, fallback to first");
                _selectedSave = null;
                _lastHeroDisplayIndex = -2;      // 强制 Hero 重画
                if (_saveCards.Count > 0)
                {
                    SetCardCursor(0);
                    _gpNavigating = true;
                    SaveCardsList.SelectedItem = _saveCards[0];
                    _gpNavigating = false;
                    _ = LoadSaveAsync(_saveCards[0].Entry);
                }
                else
                {
                    _session.ActiveSave          = null;
                    _session.ActiveSaveFileUri   = "";
                    _session.ActiveSaveFileName  = "";
                    _session.ActiveSaveFingerprint = "";
                }
            }
        }

        // Show hero preview for the focused card
        UpdateHeroPreview();
        UpdateActionHighlight();
        _secondary.ShowMainMenu(_saveCards.Cast<object>().ToList(), _cardCursor);
        DiagLog.Add($"[HOME] apply done {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0}ms");
    }

    /// <summary>
    /// Lightweight return from GamePage: existing save cards stay bound,
    /// only the active-save highlight and cursor are restored.
    /// </summary>
    private void RestoreAfterGamePage()
    {
        if (_saveCards.Count == 0) return;

        StartFloatAnimation();

        // Clear any stale IsLoaded flags from the previous session
        foreach (var c in _saveCards) c.IsLoaded = false;

        if (_session.ActiveSaveFileUri is { Length: > 0 } uri)
        {
            var active = _saveCards.FirstOrDefault(c => c.Entry.FileUri == uri);
            if (active != null)
            {
                active.IsLoaded = true;
                _selectedSave   = active.Entry;
                int idx = _saveCards.IndexOf(active);
                SetCardCursor(idx);
                _gpNavigating = true;
                SaveCardsList.SelectedItem = active;
                _gpNavigating = false;
                SaveCardsList.ScrollTo(idx, -1, ScrollToPosition.MakeVisible, false);
            }
        }

        UpdateHeroPreview();
        UpdateActionHighlight();
        _secondary.ShowMainMenu(_saveCards.Cast<object>().ToList(), _cardCursor);
    }

    // ── Hero preview ──────────────────────────────────────────────────────────

    private async void UpdateHeroPreview()
    {
        // Cancel any in-progress cross-fade
        _heroAnimCts?.Cancel();
        _heroAnimCts = new CancellationTokenSource();
        var cts = _heroAnimCts;

        // If a save is actively selected (pressed A), hero stays locked on it
        int displayIndex = _cardCursor;
        if (_selectedSave is not null)
        {
            int sel = _saveCards.FindIndex(c => c.Entry.FileUri == _selectedSave.FileUri);
            if (sel >= 0) displayIndex = sel;
        }

        // Nothing changed — skip the whole update (key for cursor navigation while locked)
        if (displayIndex == _lastHeroDisplayIndex) return;
        _lastHeroDisplayIndex = displayIndex;

        if (displayIndex < 0 || displayIndex >= _saveCards.Count)
        {
            if (HeroPreview.IsVisible)
            {
                await HeroCard.FadeToAsync(0, 100);
                if (cts.IsCancellationRequested) return;
                HeroPreview.IsVisible    = false;
                HeroGridCanvas.IsVisible = false;
                HeroCard.Opacity         = 1;
            }
            HeroEmptyState.IsVisible  = true;
            PartyStrip.IsVisible      = false;
            HeroPanel.Background      = null; // revert to XAML BackgroundColor
            _lastHeroDisplayIndex     = -1;
            StopFloatAnimation();
            return;
        }

        bool wasVisible = HeroPreview.IsVisible;

        // Only cross-fade when the displayed save actually changes
        if (wasVisible && _selectedSave is null)
        {
            await HeroCard.FadeToAsync(0, 100);
            if (cts.IsCancellationRequested) return;
        }
        else if (!wasVisible)
        {
            // no-op: will fade in below
        }

        var card = _saveCards[displayIndex];
        HeroEmptyState.IsVisible = false;
        HeroPreview.IsVisible    = true;
        HeroGridCanvas.IsVisible = true;

        // Game icon: real image when available, gradient badge fallback
        bool hasIcon = card.HasIcon;
        HeroIconGrad.IsVisible        = !hasIcon;
        HeroIconImgBorder.IsVisible   = hasIcon;
        HeroBgIconGrad.IsVisible      = !hasIcon;
        HeroBgIconImgBorder.IsVisible = hasIcon;

        if (hasIcon)
        {
            HeroIconImg.Source   = card.IconSource;
            HeroBgIconImg.Source = card.IconSource;
        }
        else
        {
            HeroBadgeText.Text   = card.GameShortName;
            HeroBgBadgeText.Text = card.GameShortName;
            HeroBadgeGrad0.Color = card.GameColorDark;
            HeroBadgeGrad1.Color = card.GameColorLight;
            HeroBgGrad0.Color    = card.GameColorDark;
            HeroBgGrad1.Color    = card.GameColorLight;
        }

        // Store game colors for the Moiré canvas and accent elements
        _heroColorLight = card.GameColorLight;
        _heroColorDark  = card.GameColorDark;

        bool moireEnabled = Preferences.Default.Get(Pages.SettingsPage.KeyMoireBg, true);
        HeroGridCanvas.IsVisible = moireEnabled;

        if (!moireEnabled)
        {
            // Classic breathing gradient
            if (_heroTopStop is null)
            {
                _heroTopStop = new GradientStop(card.GameColorLight.WithAlpha(55), 0f);
                _heroMidStop = new GradientStop(card.GameColorDark.WithAlpha(20), 0.6f);
                HeroPanel.Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1),
                    GradientStops = [_heroTopStop, _heroMidStop, new GradientStop(Colors.Transparent, 1f)],
                };
            }
            else
            {
                _heroTopStop.Color  = card.GameColorLight.WithAlpha(55);
                _heroMidStop!.Color = card.GameColorDark.WithAlpha(20);
            }
        }
        else
        {
            // Clear gradient if switching back to Moiré
            if (_heroTopStop is not null)
            {
                HeroPanel.Background = null;
                _heroTopStop = null;
                _heroMidStop = null;
            }
        }

        // Duotone Moiré (B2/W2 only)
        if (card.MoireAccent is { } accent)
        {
            // Base: near-black for B2, near-white for W2 (derived from dark/light game color)
            var dark  = card.GameColorDark;
            bool isBlackGame = dark.Red < 0.3f && dark.Green < 0.3f && dark.Blue < 0.3f;
            _moireBase   = isBlackGame
                ? new SKColor(28, 28, 36)       // near-black base for B2
                : new SKColor(240, 240, 248);    // near-white base for W2
            _moireAccent = accent;
        }
        else
        {
            _moireBase   = SKColor.Empty;
            _moireAccent = SKColor.Empty;
        }

        // Accent glow + card stroke from game color
        HeroGlow0.Color = card.GameColorLight.WithAlpha(64);
        HeroCard.Stroke = new SolidColorBrush(card.GameColorLight.WithAlpha(80));

        // Trainer info
        HeroTrainerName.Text = card.TrainerName;
        HeroGameLabel.Text   = card.VersionLabel;
        HeroTID.Text         = card.Entry.TrainerID.ToString();
        HeroBoxes.Text       = card.Entry.BoxCount.ToString();
        HeroPlaytime.Text    = card.Entry.PlayTime;

        // Active pill
        HeroActivePill.IsVisible = card.IsLoaded;

        // Party strip
        _ = LoadPartyAsync(card.Entry);

        StartFloatAnimation();

        // Fade card in
        HeroCard.Opacity = 0;
        await HeroCard.FadeToAsync(1, 160, Easing.CubicOut);
    }

    private async Task LoadPartyAsync(SaveEntry entry)
    {
        PartyStrip.IsVisible = false;
        foreach (var img in _partyImages)
            img.IsVisible = false;

        var sav = await Task.Run(() =>
        {
            var data = entry.RawData.ToArray(); // copy — SwishCrypto decrypts in-place
            PKHeX.Core.SaveUtil.TryGetSaveFile(data, out var s);
            return s;
        });
        if (sav is null || !sav.HasParty) return;

        var party = sav.PartyData.Where(p => p.Species > 0).ToArray();
        if (party.Length == 0) return;

        for (int i = 0; i < _partyImages.Length; i++)
        {
            if (i < party.Length)
            {
                var pk  = party[i];
                var url = HomeSpriteCacheService.GetHomeUrl((ushort)pk.Species, pk.Form, pk.IsShiny);
                _partyImages[i].Source    = new UriImageSource { Uri = new Uri(url), CacheValidity = TimeSpan.FromDays(30) };
                _partyImages[i].IsVisible = true;
            }
        }

        PartyStrip.IsVisible = true;
    }

    // ── Floating card animation ──────────────────────────────────────────────

    private void StartFloatAnimation()
    {
        if (_floatTimer is not null && _floatTimer.IsRunning) return;
        _floatStart = DateTime.UtcNow;
        _floatTimer ??= Dispatcher.CreateTimer();
        _floatTimer.Interval = TimeSpan.FromMilliseconds(33); // ~30 fps
        _floatTimer.Tick -= OnFloatTick;
        _floatTimer.Tick += OnFloatTick;
        _floatTimer.Start();
    }

    private void StopFloatAnimation()
    {
        _floatTimer?.Stop();
        if (HeroCard is not null)
        {
            HeroCard.TranslationY = 0;
            HeroCard.RotationX    = 0;
            HeroCard.RotationY    = 0;
        }
    }

    private void OnFloatTick(object? sender, EventArgs e)
    {
        double t = (DateTime.UtcNow - _floatStart).TotalSeconds;

        // Two independent sine waves for organic feel
        double phaseA = t * 2 * Math.PI / 3.5;   // 3.5s bob cycle
        double phaseB = t * 2 * Math.PI / 4.8 + Math.PI / 3; // offset tilt

        HeroCard.TranslationY = 5.0  * Math.Sin(phaseA);
        HeroCard.RotationX    = 3.5  * Math.Sin(phaseA);
        HeroCard.RotationY    = 2.5  * Math.Sin(phaseB);

        if (_heroTopStop is not null && _heroColorLight != Colors.Transparent)
        {
            double breath  = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2 / 4.0);
            byte topAlpha  = (byte)(22 + breath * 90);
            byte midAlpha  = (byte)(8  + breath * 28);
            _heroTopStop.Color  = _heroColorLight.WithAlpha(topAlpha);
            _heroMidStop!.Color = _heroColorDark.WithAlpha(midAlpha);
        }

        HeroGridCanvas.InvalidateSurface();
    }

    // ── Moiré ASCII background ───────────────────────────────────────────────

    private void OnHeroGridPaint(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        if (_heroColorLight == Colors.Transparent) return;

        float pw = e.Info.Width;
        float ph = e.Info.Height;

        // Character cell size in canvas pixels
        const float CW   = 14f;
        const float CH   = 23f;
        const float freq = 0.3f;
        const float thr  = 0.15f;
        const float orb  = 0.3f;

        float t = (float)(DateTime.UtcNow - _floatStart).TotalSeconds;

        int cols = (int)(pw / CW) + 2;
        int rows = (int)(ph / CH) + 2;

        // Three orbiting wave centers (ported from MoireCode.html)
        float nx = cols * 0.5f + MathF.Cos(t * 0.30f)       * cols * orb;
        float ny = rows * 0.5f + MathF.Sin(t * 0.40f)       * rows * orb;
        float ix = cols * 0.5f + MathF.Cos(t * 0.37f + 2f)  * cols * (orb * 0.83f);
        float iy = rows * 0.5f + MathF.Sin(t * 0.29f + 2f)  * rows * (orb * 1.17f);
        float sx = cols * 0.5f + MathF.Sin(t * 0.23f + 4f)  * cols * (orb * 1.17f);
        float sy = rows * 0.5f + MathF.Cos(t * 0.31f + 4f)  * rows * (orb * 0.83f);

        float f1 = freq;
        float f2 = freq * 1.033f;
        float f3 = freq * 0.967f;

        // Resolve game color for this theme
        bool isDark = ThemeService.Current == PkTheme.Dark;
        var mc = isDark ? _heroColorLight : _heroColorDark;
        byte cr = (byte)(mc.Red   * 255);
        byte cg = (byte)(mc.Green * 255);
        byte cb = (byte)(mc.Blue  * 255);

        // Dark theme: boost colors that are too dark to see against a dark background
        float r = cr / 255f, g = cg / 255f, b = cb / 255f;
        if (isDark)
        {
            float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            if (lum < 0.12f)
            {
                float boost = (0.12f - lum) / 0.12f;
                r += (1f - r) * boost;
                g += (1f - g) * boost;
                b += (1f - b) * boost;
            }
        }
        var charBase = new SKColor((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));

        using var font  = new SKFont(MoireTypeface, CW);
        using var paint = new SKPaint { IsAntialias = true };

        for (int row = 0; row < rows; row++)
        {
            float fy = row * CH + CW; // baseline offset
            for (int col = 0; col < cols; col++)
            {
                float fx = col * 0.55f;

                float pb = fx - nx * 0.55f, hb = row - ny;
                float gb = fx - ix * 0.55f, qb = row - iy;
                float vb = fx - sx * 0.55f, yb = row - sy;

                float d1 = MathF.Sqrt(pb * pb + hb * hb);
                float d2 = MathF.Sqrt(gb * gb + qb * qb);
                float d3 = MathF.Sqrt(vb * vb + yb * yb);

                float C = MathF.Sin(d1 * f1 + t)
                        + MathF.Sin(d2 * f2 - t * 0.7f)
                        + MathF.Sin(d3 * f3 + t * 0.5f);
                C = (C + 3f) / 6f;

                if (C < thr || C > 1f - thr) continue;

                float w = 1f - MathF.Abs(C - 0.5f) * 2f;
                int ci = Math.Min(MoireCharStrings.Length - 1, (int)(w * MoireCharStrings.Length));
                if (MoireChars[ci] == ' ') continue;

                float alpha = isDark ? 0.30f + w * 0.70f : 0.75f + w * 0.25f;
                SKColor color;
                if (_moireAccent != SKColor.Empty)
                {
                    // Duotone: lerp from base (black/white) to accent color
                    color = new SKColor(
                        (byte)(_moireBase.Red   + (_moireAccent.Red   - _moireBase.Red)   * w),
                        (byte)(_moireBase.Green + (_moireAccent.Green - _moireBase.Green) * w),
                        (byte)(_moireBase.Blue  + (_moireAccent.Blue  - _moireBase.Blue)  * w));
                }
                else
                {
                    color = charBase;
                }
                paint.Color = color.WithAlpha((byte)(alpha * 255));
                canvas.DrawText(MoireCharStrings[ci], col * CW, fy, font, paint);
            }
        }
    }

    // ── Save loading ─────────────────────────────────────────────────────────

    private async Task LoadSaveAsync(SaveEntry entry)
    {
        try
        {
            var sav = await Task.Run(() =>
            {
                var data = entry.RawData.ToArray(); // copy — SwishCrypto decrypts in-place
                SaveUtil.TryGetSaveFile(data, out var s);
                return s;
            });

            if (sav is null)
            {
                await DisplayAlertAsync("加载失败",
                    $"Could not parse \"{entry.FileName}\" ({entry.RawData.Length:N0} bytes). Unsupported format.",
                    "OK");
                return;
            }

            foreach (var card in _saveCards)
                card.IsLoaded = false;

            _session.ActiveSave = sav;
            _session.ActiveSaveFileName = entry.FileName;
            _session.ActiveSaveFileUri = entry.FileUri;
            SaveSyncService.CaptureBaseline(_session, entry.RawData.ToArray());
            _selectedSave = entry;

            var active = _saveCards.FirstOrDefault(c => c.Entry == entry);
            if (active != null) active.IsLoaded = true;

            // Force UpdateHeroPreview to do a full redraw so the Active pill appears.
            // Without this reset it skips the update because the display index didn't change.
            _lastHeroDisplayIndex = -2;
            UpdateHeroPreview();
            UpdateActionHighlight();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("加载错误", $"{ex.GetType().Name}: {ex.Message}", "OK");
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    private void ActivatePrimaryButton()
    {
        if (_selectedSave is not null)
            _ = NavigateTo(nameof(GamePage));
    }

    /// <summary>
    /// 统一的安全跳转：冷启动时 Shell 可能还没完成初始化，GoToAsync 会抛异常且被
    /// 丢弃（表现为"点了没反应"）。这里捕获后短暂等待再重试一次，失败才提示。
    /// </summary>
    private async Task NavigateTo(string route)
    {
        var navT0 = Stopwatch.GetTimestamp();
        DiagLog.Add($"[NAV] start -> {route} @{HomeMs():F0}ms");
        try
        {
            await Shell.Current.GoToAsync(route);
            DiagLog.Add($"[NAV] -> {route} ok {Stopwatch.GetElapsedTime(navT0).TotalMilliseconds:F0}ms");
            return;
        }
        catch (Exception ex)
        {
            CrashLog.Write($"Navigate:{route}", ex);
            DiagLog.Add($"[NAV] -> {route} fail {Stopwatch.GetElapsedTime(navT0).TotalMilliseconds:F0}ms {ex.GetType().Name}");
        }

        await Task.Delay(250);

        try
        {
            await Shell.Current.GoToAsync(route);
            DiagLog.Add($"[NAV] -> {route} retry ok {Stopwatch.GetElapsedTime(navT0).TotalMilliseconds:F0}ms");
        }
        catch (Exception retry)
        {
            CrashLog.Write($"NavigateRetry:{route}", retry);
            await DisplayAlertAsync("跳转失败", $"{route}：{retry.Message}", "OK");
        }
    }

    private void ActivateTile(int tile)
    {
        switch (tile)
        {
            case 0: // Gifts
                _ = NavigateTo(nameof(MysteryGiftDBPage));
                break;
            case 1: // Export
                if (_selectedSave is not null)
                    _ = ExportSaveAsync();
                break;
            case 2: // Bank
                // On Thor (dual screen): BankViewPage shows detail on top screen, grid on second screen.
                // On phone: BankPage shows the full grid on the primary screen (BankViewPage has no grid there).
                _ = NavigateTo(_secondary.IsAvailable ? nameof(BankViewPage) : nameof(BankPage));
                break;
            case 3: // Dex
                _ = NavigateTo(nameof(DexPage));
                break;
            case 4: // Settings
                _ = NavigateTo(nameof(SettingsPage));
                break;
        }
    }

    private async Task ExportSaveAsync()
    {
        if (_session.ActiveSave is null || _selectedSave is null) return;
        try
        {
            var data = _session.ActiveSave.Write().ToArray();
            await _fileService.ExportFileAsync(data, _selectedSave.FileName);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("导出错误", ex.Message, "OK");
        }
    }

    // ── Touch handlers ───────────────────────────────────────────────────────

    private void OnSaveCardSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_gpNavigating) return;
        if (e.CurrentSelection.Count == 0) return;
        if (e.CurrentSelection[0] is SaveCardViewModel vm)
        {
            SetCardCursor(_saveCards.IndexOf(vm));
            _ = LoadSaveAsync(vm.Entry);
        }
    }

    private void OnHeroCardTapped(object? sender, TappedEventArgs e) => ActivatePrimaryButton();
    private void OnOpenBoxesTapped(object? sender, EventArgs e) => ActivatePrimaryButton();
    private void OnSettingsTapped(object? sender, EventArgs e) => ActivateTile(4);
    private void OnGiftsTapped(object? sender, EventArgs e) => ActivateTile(0);
    private void OnExportTapped(object? sender, EventArgs e) => ActivateTile(1);
    private void OnBankTapped(object? sender, EventArgs e) => ActivateTile(2);
    private void OnDexTapped(object? sender, EventArgs e)
    {
        DiagLog.Add($"[HOME] dex tap {HomeMs():F0}ms");
        ActivateTile(3);
    }

    // ── Action bar highlight ─────────────────────────────────────────────────

    private void SetCardCursor(int newIndex)
    {
        if (_cardCursor >= 0 && _cardCursor < _saveCards.Count)
            _saveCards[_cardCursor].IsCursor = false;
        _cardCursor = newIndex;
        if (_cardCursor >= 0 && _cardCursor < _saveCards.Count)
            _saveCards[_cardCursor].IsCursor = true;
    }

    private void UpdateActionHighlight()
    {
        bool light = ThemeService.Current == PkTheme.Light;
        var focusBg      = Color.FromArgb(light ? "#EEF2FF" : "#182242");
        var focusStroke  = Color.FromArgb("#3B8BFF");
        var normalBg     = Color.FromArgb(light ? "#FFFFFF" : "#131B35");
        var normalStroke = Color.FromArgb(light ? "#E0E4EC" : "#0DFFFFFF");

        // Primary button — enforce pastel blue background (overrides any theme reset)
        Btn_OpenBoxes.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
            GradientStops = [
                new GradientStop(Color.FromArgb("#EAF5FF"), 0f),
                new GradientStop(Color.FromArgb("#CCE8FF"), 1f),
            ],
        };
        bool primaryFocused = _focusSection == 1 && _actionCursor == 0;
        Btn_OpenBoxes.Stroke          = primaryFocused ? Color.FromArgb("#5AAAD0") : Colors.Transparent;
        Btn_OpenBoxes.StrokeThickness = primaryFocused ? 2.5 : 1.5;

        // Tiles
        for (int i = 0; i < _actionTiles.Length; i++)
        {
            bool focused = _focusSection == 1 && _actionCursor == i + 1;
            _actionTiles[i].BackgroundColor = focused ? focusBg : normalBg;
            _actionTiles[i].Stroke          = focused ? focusStroke : normalStroke;
        }

        _secondary.UpdateMainMenuState(_cardCursor, _focusSection, _actionCursor);
    }

    // ── Gamepad ──────────────────────────────────────────────────────────────

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
            case Android.Views.Keycode.DpadUp:
                MoveUp(); break;
            case Android.Views.Keycode.DpadDown:
                MoveDown(); break;
            case Android.Views.Keycode.DpadLeft:
                MoveLeft(); break;
            case Android.Views.Keycode.DpadRight:
                MoveRight(); break;
            case Android.Views.Keycode.ButtonA:
                _ = OnAPressed(); break;
            case Android.Views.Keycode.ButtonB:
                GoBackOrQuit(); break;
            case Android.Views.Keycode.ButtonX:
                ActivateTile(4); break; // Quick jump to Settings
            case Android.Views.Keycode.ButtonL1:
                CycleZone(-1); break;
            case Android.Views.Keycode.ButtonR1:
                CycleZone(1); break;
            case Android.Views.Keycode.ButtonSelect:
            case Android.Views.Keycode.ButtonStart:
                _ = NavigateTo(nameof(SettingsPage)); break;
            case (Android.Views.Keycode)107: // KEYCODE_BUTTON_THUMBR — right stick click
                _ = RefreshSavesAsync(); break;
        }
    }
#endif

    private void CycleZone(int dir)
    {
        if (dir < 0 && _focusSection == 1)
        {
            _focusSection = 0;
            if (_saveCards.Count > 0 && _cardCursor < 0)
                SetCardCursor(0);
            if (_saveCards.Count > 0)
            {
                _gpNavigating = true;
                SaveCardsList.SelectedItem = _saveCards[_cardCursor];
                _gpNavigating = false;
                SaveCardsList.ScrollTo(_cardCursor, -1, ScrollToPosition.MakeVisible, false);
            }
        }
        else if (dir > 0 && _focusSection == 0)
        {
            _focusSection = 1;
            _actionCursor = 0;
            _gpNavigating = true;
            SaveCardsList.SelectedItem = null;
            _gpNavigating = false;
        }
        UpdateActionHighlight();
    }

    private static void Haptic() { try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { } }

    private void MoveUp()
    {
        Haptic();
        if (_focusSection == 1)
        {
            if (_actionCursor > 0)
            {
                _actionCursor = 0;
            }
            else
            {
                CycleZone(-1);
                return;
            }
        }
        else
        {
            if (_saveCards.Count == 0) return;
            SetCardCursor(Math.Max(0, (_cardCursor < 0 ? 0 : _cardCursor) - 1));
            _gpNavigating = true;
            SaveCardsList.SelectedItem = _saveCards[_cardCursor];
            _gpNavigating = false;
            SaveCardsList.ScrollTo(_cardCursor, -1, ScrollToPosition.MakeVisible, false);
            UpdateHeroPreview();
        }
        UpdateActionHighlight();
    }

    private void MoveDown()
    {
        Haptic();
        if (_focusSection == 0)
        {
            if (_saveCards.Count > 0 && _cardCursor < _saveCards.Count - 1)
            {
                SetCardCursor(_cardCursor + 1);
                _gpNavigating = true;
                SaveCardsList.SelectedItem = _saveCards[_cardCursor];
                _gpNavigating = false;
                SaveCardsList.ScrollTo(_cardCursor, -1, ScrollToPosition.MakeVisible, false);
                UpdateHeroPreview();
            }
            else
            {
                CycleZone(1);
                return;
            }
        }
        else
        {
            if (_actionCursor == 0)
                _actionCursor = 1;
        }
        UpdateActionHighlight();
    }

    private void MoveLeft()
    {
        Haptic();
        if (_focusSection == 1 && _actionCursor > 1)
        {
            _actionCursor--;
            UpdateActionHighlight();
        }
    }

    private void MoveRight()
    {
        Haptic();
        if (_focusSection == 1 && _actionCursor >= 1 && _actionCursor < 5)
        {
            _actionCursor++;
            UpdateActionHighlight();
        }
    }

    private async Task OnAPressed()
    {
        Haptic();
        if (_focusSection == 0)
        {
            if (_cardCursor >= 0 && _cardCursor < _saveCards.Count)
            {
                var card = _saveCards[_cardCursor];
                if (card.IsLoaded)
                    ActivatePrimaryButton();
                else
                    await LoadSaveAsync(card.Entry);
            }
        }
        else
        {
            if (_actionCursor == 0)
                ActivatePrimaryButton();
            else
                ActivateTile(_actionCursor - 1);
        }
    }

    // ── SaveCardViewModel ────────────────────────────────────────────────────

    internal sealed class SaveCardViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static Color ColNormal    => Color.FromArgb(ThemeService.Current == PkTheme.Light ? "#FFFFFF" : "#131B35");
        private static Color ColCursor    => Color.FromArgb(ThemeService.Current == PkTheme.Light ? "#EEF2FF" : "#152040");
        private static Color ColLoaded    => Color.FromArgb(ThemeService.Current == PkTheme.Light ? "#E8F0FF" : "#1C2850");
        private static Color StrokeNone   => Colors.Transparent;
        private static Color StrokeCursor => Color.FromArgb("#5CA0FF");
        private static Color StrokeLoaded => Color.FromArgb("#3B8BFF");

        private bool _isLoaded;
        public bool IsLoaded
        {
            get => _isLoaded;
            set { if (_isLoaded == value) return; _isLoaded = value; OnPropertyChanged(); RefreshCardVisuals(); }
        }

        private bool _isCursor;
        public bool IsCursor
        {
            get => _isCursor;
            set { if (_isCursor == value) return; _isCursor = value; OnPropertyChanged(); RefreshCardVisuals(); }
        }

        private Color _cardBackground = ColNormal;
        public Color CardBackground { get => _cardBackground; private set { _cardBackground = value; OnPropertyChanged(); } }

        private Color _cardStroke = StrokeNone;
        public Color CardStroke { get => _cardStroke; private set { _cardStroke = value; OnPropertyChanged(); } }

        private double _cardStrokeThickness = 1.5;
        public double CardStrokeThickness { get => _cardStrokeThickness; private set { _cardStrokeThickness = value; OnPropertyChanged(); } }

        public void RefreshTheme() => RefreshCardVisuals();

        private void RefreshCardVisuals()
        {
            if (_isLoaded)
            {
                CardBackground      = ColLoaded;
                CardStroke          = StrokeLoaded;
                CardStrokeThickness = 2;
            }
            else if (_isCursor)
            {
                CardBackground      = ColCursor;
                CardStroke          = StrokeCursor;
                CardStrokeThickness = 1.5;
            }
            else
            {
                CardBackground      = ColNormal;
                CardStroke          = StrokeNone;
                CardStrokeThickness = 1.5;
            }
            OnPropertyChanged(nameof(TextPrimary));
            OnPropertyChanged(nameof(TextSecondary));
            OnPropertyChanged(nameof(TextDim));
        }

        public Color TextPrimary   => Color.FromArgb(ThemeService.Current == PkTheme.Light ? "#0D1117" : "#EDF0FF");
        public Color TextSecondary => Color.FromArgb(ThemeService.Current == PkTheme.Light ? "#4A5568" : "#8892B5");
        public Color TextDim       => Color.FromArgb(ThemeService.Current == PkTheme.Light ? "#9AA5B4" : "#3D4A6E");

        public SaveEntry Entry { get; }
        public string TrainerName { get; }
        public string VersionLabel { get; }
        public string DetailLine { get; }
        public string LastModifiedLabel { get; }
        public string GameShortName { get; }

        public Color  GameColorDark { get; }
        public Color  GameColorLight { get; }
        public SKColor? MoireAccent { get; }

        public ImageSource? IconSource { get; }
        public bool HasIcon { get; }
        public bool HasNoIcon { get; }

        public SaveCardViewModel(SaveEntry entry)
        {
            Entry = entry;
            TrainerName = entry.TrainerName;
            VersionLabel = $"Pokémon {entry.Version}  ·  第 {entry.Generation} 世代";
            DetailLine = $"初训家 ID {entry.TrainerID}  ·  {entry.BoxCount} 个箱子  ·  {entry.PlayTime}";
            LastModifiedLabel = FormatRelativeTime(entry.LastModified);
            GameShortName = GetGameShortName(entry.Version);

            var (dark, light) = GameColors.Get(entry.Version);
            MoireAccent = GameColors.GetAccent(entry.Version);
            GameColorDark = Color.FromUint((uint)((dark.Alpha << 24) | (dark.Red << 16) | (dark.Green << 8) | dark.Blue));
            GameColorLight = Color.FromUint((uint)((light.Alpha << 24) | (light.Red << 16) | (light.Green << 8) | light.Blue));

            var iconFile = GetIconFileName(entry.Version);
            if (iconFile != null)
            {
                IconSource = ImageSource.FromStream(
                    ct => FileSystem.OpenAppPackageFileAsync($"gameicons/{iconFile}").WaitAsync(ct));
                HasIcon = true;
                HasNoIcon = false;
            }
            else
            {
                HasIcon = false;
                HasNoIcon = true;
            }
        }

        private static string FormatRelativeTime(DateTimeOffset dt)
        {
            var diff = DateTimeOffset.UtcNow - dt;
            if (diff.TotalMinutes < 1)  return "刚刚";
            if (diff.TotalHours   < 1)  return $"{(int)diff.TotalMinutes} 分钟前";
            if (diff.TotalDays    < 1)  return $"{(int)diff.TotalHours} 小时前";
            if (diff.TotalDays    < 7)  return $"{(int)diff.TotalDays} 天前";
            return dt.LocalDateTime.ToString("MMM d");
        }

        private static string? GetIconFileName(GameVersion v) => v switch
        {
            GameVersion.RD  => "red_vc.png",
            GameVersion.BU  => "blue_vc.png",
            GameVersion.GN  => "green_vc.png",
            GameVersion.YW  => "yellow_vc.png",
            GameVersion.GD  => "gold_vc.png",
            GameVersion.SI  => "silver_vc.png",
            GameVersion.C   => "crystal.png",
            GameVersion.D   => "diamond.png",
            GameVersion.P   => "pearl.png",
            GameVersion.R   => "ruby.png",
            GameVersion.S   => "sapphire.png",
            GameVersion.E   => "emerald.png",
            GameVersion.FR  => "fire_red.png",
            GameVersion.LG  => "leaf_green.png",
            GameVersion.Pt  => "platinum.png",
            GameVersion.HG   => "heartgold.png",
            GameVersion.SS   => "soulsilver.png",
            GameVersion.HGSS => "heartgold.png", // fallback: save has no Pokémon to probe
            GameVersion.B   => "black.png",
            GameVersion.W   => "white.png",
            GameVersion.B2  => "black2.png",
            GameVersion.W2  => "white2.png",
            GameVersion.X   => "x.png",
            GameVersion.Y   => "y.png",
            GameVersion.OR  => "omega_ruby.png",
            GameVersion.AS  => "alpha_sapphire.png",
            GameVersion.SN  => "sun.png",
            GameVersion.MN  => "moon.png",
            GameVersion.US  => "ultra_sun.png",
            GameVersion.UM  => "ultra_moon.png",
            GameVersion.GP  => "lets_go_pikachu.jpg",
            GameVersion.GE  => "lets_go_eevee.jpg",
            GameVersion.SW  => "sword.png",
            GameVersion.SH  => "shield.png",
            GameVersion.BD  => "brilliant_diamond.jpg",
            GameVersion.SP  => "shining_pearl.jpg",
            GameVersion.PLA => "legends_arceus.jpg",
            GameVersion.SL  => "scarlet.jpg",
            GameVersion.VL  => "violet.jpg",
            GameVersion.ZA  => "legends_za.png",
            _               => null,
        };

        internal static string GetGameShortName(GameVersion v) => v switch
        {
            GameVersion.RD  => "Red",
            GameVersion.BU  => "Blue",
            GameVersion.GN  => "Green",
            GameVersion.YW  => "Yel",
            GameVersion.GD  => "Gold",
            GameVersion.SI  => "Slvr",
            GameVersion.C   => "Crys",
            GameVersion.R   => "Ruby",
            GameVersion.S   => "Saph",
            GameVersion.E   => "Em",
            GameVersion.FR  => "FR",
            GameVersion.LG  => "LG",
            GameVersion.D   => "D",
            GameVersion.P   => "P",
            GameVersion.Pt  => "Pt",
            GameVersion.HG  => "HG",
            GameVersion.SS  => "SS",
            GameVersion.B   => "BLK",
            GameVersion.W   => "WHT",
            GameVersion.B2  => "BLK2",
            GameVersion.W2  => "WHT2",
            GameVersion.X   => "X",
            GameVersion.Y   => "Y",
            GameVersion.OR  => "OR",
            GameVersion.AS  => "AS",
            GameVersion.SN  => "Sun",
            GameVersion.MN  => "Moon",
            GameVersion.US  => "US",
            GameVersion.UM  => "UM",
            GameVersion.GP  => "LGP",
            GameVersion.GE  => "LGE",
            GameVersion.SW  => "Sw",
            GameVersion.SH  => "Sh",
            GameVersion.PLA => "PLA",
            GameVersion.BD  => "BD",
            GameVersion.SP  => "SP",
            GameVersion.SL  => "SL",
            GameVersion.VL  => "VL",
            _ => v.ToString()[..Math.Min(4, v.ToString().Length)],
        };
    }
}
