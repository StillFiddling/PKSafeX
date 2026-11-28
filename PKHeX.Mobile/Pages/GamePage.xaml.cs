using System.Diagnostics;
using Microsoft.Maui.Controls.Shapes;
using PKHeX.Core;
using PKHeX.Mobile.Controls;
using PKHeX.Mobile.Models;
using PKHeX.Mobile.Services;
using PKHeX.Mobile.Theme;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using static PKHeX.Mobile.Services.ThemeService;

namespace PKHeX.Mobile.Pages;

public partial class GamePage : ContentPage
{
    private const int Columns = 6;
    private const int Rows    = 5;

    private readonly FileSystemSpriteRenderer _sprites = new();
    private readonly GameStrings _strings = GameLang.Strings;

    private SaveFile? _sav;
    private PKM[] _currentBox = [];
    private int _boxIndex;
    private int _cursorSlot;
    private int _selectedSlot = -1;   // selected outline (A-confirmed), -1 = none
    private PKM? _previewPk;          // Pokémon shown in top panel (follows cursor)
    private int  _previewSpecies = -1; // debounce WebView reloads
    private CancellationTokenSource _loadBoxCts = new();
    private bool   _spriteWebViewReady; // true after first full HTML load
    private bool   _spriteHasCurrent;   // 当前预览对象是否真的有动图（Thor 预览栏用）
    private bool   _sheetWebViewReady;  // 手机详情卡动图是否已加载过 HTML
    private bool   _sheetHasCurrent;    // 当前详情卡对象是否真的有动图
    private int    _sheetAnimKey = -1;  // 详情卡当前动图对应的对象键（同种同形态同闪光则跳过重载）
    private int    _sheetAnimSeq;       // 详情卡动图请求序号，防竞态
    private int    _boxLoadSeq;         // 箱子加载序号，防旧预加载回写
    private double _canvasW, _canvasH;  // cached after first layout (TopSelectedPanel is invisible during initial layout pass)
    private bool _showLegalityBadges;
    private bool?[] _legalityCache = [];

    // X-toggle: false = info+radar (default), true = moves+compat
    private bool _detailToggled;

    // Landscape-phone cycle view: 0 = Sprite, 1 = Moves, 2 = Stats
    private int _landscapeDetailView;

    // Action menu (Start button)
    private bool _menuOpen;
    private int  _menuCursor; // 0 = Export, 1 = Items

    // Move mode
    private bool _moveMode;
    private PKM? _movePk;
    private int  _moveSourceBox;
    private int  _moveSourceSlot;

    // Cursor pulse animation
    private readonly Stopwatch _cursorTimer = Stopwatch.StartNew();
    private IDispatcherTimer? _pulseTimer;

    // Bounce animation: slot that just received the cursor
    private int  _bounceSlot   = -1;
    private long _bounceStartMs;

    // Box slide direction (-1 = prev box, +1 = next box, 0 = no slide)
    private int _boxSlideDir;

#if ANDROID
    // Pokémon cry playback（播放器与下载统一交给 CryService，这里只保留防抖）
    private string _lastCrySlug = "";
#endif

    // Items tab
    private bool _itemsTabActive;
    private PlayerBag? _bag;
    private int _activePouchIndex;
    private int _itemCursor = -1;
    private bool _itemEditMode;
    private List<ItemRow> _itemRows    = [];
    private List<ItemRow> _allItemRows = [];
    private string _itemSearchText    = "";
    private readonly List<Border> _pocketTabBorders = [];

    // Pre-built type badge views — updated in-place instead of recreated per cursor move
    private readonly Border[] _typeBadges       = CreateTypeBadgePool();
    private readonly Border[] _phoneTypeBadges  = CreateTypeBadgePool();

    // Search / filter
    private bool _searchMode = false;
    private record struct SearchSlot(int Box, int Slot, PKM Pk);
    private SearchSlot[] _searchResults = [];
    private PKM? CursorPk => _searchMode
        ? (_cursorSlot < _searchResults.Length ? _searchResults[_cursorSlot].Pk : null)
        : (_cursorSlot < _currentBox.Length    ? _currentBox[_cursorSlot]       : null);

    // Pre-computed grid layout (recalculated on canvas size change)
    private SKRect[] _slotRects = [];
    private float _gridSlotSize;
    private float _gridOffsetX;
    private float _gridOffsetY;
    private int _lastCanvasW;
    private int _lastCanvasH;
    private int _paintLogN;     // 诊断（临时）：记录本次出现后的前几次绘制

    // Radar animation
    private float[]                  _radarCurrent = new float[6];
    private float                    _radarVisMax  = 255f;
    private CancellationTokenSource? _radarAnimCts;

    private readonly ISecondaryDisplay _secondary;
    private readonly SessionState _session;
    private bool _isPhone;
    private bool _isLandscapePhone;
    private PKM? _phoneSheetPk;
    private bool _phoneSheetVisible;
    /// <summary>详情浮层显隐序号：每次 Show/Hide 自增，收尾动画校验序号避免清掉新卡片。</summary>
    private int PhoneSheetSeq;
    /// <summary>竖屏卡片高度上限（min(0.94 屏, 760)），由 ApplySheetSize 计算。</summary>
    private double _sheetMaxH;
    /// <summary>当前卡片实际高度（横屏固定值，竖屏为实测后 clamp 的结果）。</summary>
    private double _sheetCardH;

    public GamePage(ISecondaryDisplay secondary, SessionState session)
    {
        _secondary = secondary;
        _session   = session;
        InitializeComponent();

        // Seed the type badge pools into their containers once.
        // UpdateTypeBadges / UpdatePhoneSheetContent update them in-place from here on.
        foreach (var b in _typeBadges)      TypeBadgeRow.Children.Add(b);
        foreach (var b in _phoneTypeBadges) PhoneSheetTypeBadges.Children.Add(b);

        // 详情卡动图：先让静态图垫在下面，等 WebView 真正完成导航再隐藏，
        // 避免"动图还没渲染出来、静态图也没了"的空白。
        PhoneSheetWebView.Navigated += (_, _) =>
        {
            if (_sheetHasCurrent)
                PhoneSheetCanvas.IsVisible = false;
        };

        // Keep the radar frosted-glass box square regardless of row height.
        RadarBorder.SizeChanged += (_, _) =>
        {
            if (RadarBorder.Width > 0)
                RadarBorder.HeightRequest = RadarBorder.Width;
        };

        // Cache canvas bounds after first layout — TopSelectedPanel is invisible
        // during the initial layout pass so Width/Height are -1 until it shows.
        PreviewCanvas.SizeChanged += (_, _) =>
        {
            if (PreviewCanvas.Width > 0) _canvasW = PreviewCanvas.Width;
            if (PreviewCanvas.Height > 0) _canvasH = PreviewCanvas.Height;
        };

        // 记录箱子网格顶边在整页中的比例，供银行页把自己的网格摆到同一垂直区间。
        SizeChanged       += (_, _) => UpdateBoxGridTopRatio();
        BoxCanvas.SizeChanged += (_, _) => UpdateBoxGridTopRatio();
    }

    /// <summary>箱子网格画布顶边占整页高度的比例（0~1）；银行页用它对齐网格位置。</summary>
    public static double BoxGridTopRatio { get; private set; }

    /// <summary>箱子网格画布底边占整页高度的比例（0~1）；银行页把画布高度也对齐到这一区间。</summary>
    public static double BoxGridBottomRatio { get; private set; }

    private void UpdateBoxGridTopRatio()
    {
        if (Height <= 0 || BoxCanvas.Height <= 0) return;

        double offset = 0;
        VisualElement? cur = BoxCanvas;
        while (cur is not null && cur is not Page)
        {
            offset += cur.Frame.Y;
            cur     = cur.Parent as VisualElement;
        }

        if (offset <= 0) return;
        BoxGridTopRatio    = offset / Height;
        BoxGridBottomRatio = (offset + BoxCanvas.Height) / Height;
    }

    private static Border[] CreateTypeBadgePool() =>
    [
        CreateTypeBadge(),
        CreateTypeBadge(),
    ];

    private static Border CreateTypeBadge() => new()
    {
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
        Stroke       = Colors.Transparent,
        Padding      = new Thickness(10, 2),
        IsVisible    = false,
        Content      = new Label
        {
            FontFamily              = "NunitoExtraBold",
            FontSize                = 9,
            TextColor               = Colors.White,
            CharacterSpacing        = 0.5,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment   = TextAlignment.Center,
        },
    };

    // ──────────────────────────────────────────────
    //  Lifecycle
    // ──────────────────────────────────────────────

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        GamepadRouter.KeyReceived        -= OnGamepadKey;
        GamepadRouter.KeyReceived        += OnGamepadKey;
        GamepadRouter.BoxScrollRequested -= OnBoxScroll;
        GamepadRouter.BoxScrollRequested += OnBoxScroll;
#endif
        ItemRow.Changed -= OnItemChanged;
        ItemRow.Changed += OnItemChanged;
        BackCoordinator.Bind(HandleBackAction);
        BindPortraitHintTaps();
        var sav = _session.ActiveSave;
        if (sav is null) return;

        bool freshSave = _sav != sav;
        _sav = sav;

        // 若磁盘存档已被游戏更新，进页面时提示一次重新加载（有带走中的宝可梦时不打扰）
        if (_session.PendingMove is null && !_session.PendingFromBank)
            _ = CheckStaleOnEnterAsync();

        _isPhone = !_secondary.IsAvailable;

        // 从编辑器/银行返回：清掉残留选中态与浮层位移，避免"再点同格直跳编辑器"和卡片贴底
        DeselectSlot();
        PhoneSheetPanel.TranslationY = 0;
        PhoneSheetPanel.Scale        = 1;

        // Always reset so WebView reloads cleanly after any navigation away.
        // Also reset _previewSpecies so the scale/toggle changes from Settings
        // take effect even when returning to the same Pokémon.
        _spriteWebViewReady = false;
        _spriteHasCurrent   = false;
        _previewSpecies     = -1;

        // 详情卡动图同样整体复位：返回页面后让 WebView 重新加载干净
        _sheetAnimSeq++;
        _sheetWebViewReady = false;
        _sheetHasCurrent   = false;
        _sheetAnimKey      = -1;
        PhoneSheetWebView.IsVisible = false;
        PhoneSheetCanvas.IsVisible  = true;

        if (freshSave)
        {
            _boxIndex    = 0;
            _cursorSlot  = 0;
            DeselectSlot();

            ApplyTrainerIdentity(sav);
        }

        // If returning from bank with a withdrawn Pokémon, enter move mode
        if (_session.PendingMove != null && _session.PendingFromBank)
        {
            _movePk          = _session.PendingMove;
            _moveSourceBox   = -1; // originated from bank
            _moveSourceSlot  = -1;
            _moveMode        = true;
            _session.PendingMove  = null;
        }

        // Always reset species key so radar re-reads preferences (e.g. after returning from Settings)
        _previewSpecies = -1;
        _showLegalityBadges = Preferences.Default.Get(SettingsPage.KeyLegalityBadge, false);

        // Start cursor pulse timer (~60fps)
        if (_pulseTimer is null)
        {
            _pulseTimer = Dispatcher.CreateTimer();
            _pulseTimer.Interval = TimeSpan.FromMilliseconds(16);
            _pulseTimer.Tick += (_, _) =>
            {
                BoxCanvas.InvalidateSurface();
                _secondary.InvalidateBoxCanvas();
            };
        }
        _pulseTimer.Start();

        ThemeService.ThemeChanged -= OnThemeChanged;
        ThemeService.ThemeChanged += OnThemeChanged;
        SpriteBgImage.IsVisible = true;

        _secondary.Show();

        // Apply layout for current device mode and orientation
        if (_isPhone)
            ApplyPhoneLayout(Width > Height);
        else
        {
            // Thor: top screen (Row 0 = *) + second screen collapsed (Row 1 = 0)
            RootGrid.RowDefinitions[0].Height    = GridLength.Star;
            RootGrid.RowDefinitions[1].Height    = new GridLength(0);
            RootGrid.ColumnDefinitions[0].Width  = GridLength.Star;
            RootGrid.ColumnDefinitions[1].Width  = new GridLength(0);
            TopScreenPanel.IsVisible             = true;
            PhoneTrainerStrip.IsVisible          = false;
        }

        LoadBox(_boxIndex);
        BindBoxHintTaps();

#if ANDROID
        // 诊断（临时）：返回后两个时间点打印各行的实测高度，排查银行页/箱子页 2.5dp 位移
        _paintLogN = 0;
        Services.DiagLog.Add("game appear enter");
        void LogOnce(string when)
        {
            int absY = -1;
#if ANDROID
            if (BoxCanvas.Handler?.PlatformView is Android.Views.View pv)
            {
                var loc = new int[2];
                pv.GetLocationOnScreen(loc);
                absY = loc[1];
            }
#endif
            Services.DiagLog.Add($"game {when} page={Height:0.0} root={RootGrid.Height:0.0}/{RootGrid.Frame.Y:0.0} rs={RootGrid.RowSpacing:0.##}/{RootGrid.ColumnSpacing:0.##} strip={PhoneTrainerStrip.Height:0.0} nav={BoxNavRow.Height:0.0} search={BoxSearchRow.Height:0.0} canvas={BoxCanvas.Height:0.0} canvasY={BoxCanvas.Frame.Y:0.0} absY={absY} gridAbsY={(absY < 0 ? -1 : absY + _gridOffsetY):0} bottomY={BottomGrid.Frame.Y:0.0} bottom={BottomGrid.Height:0.0} info={InfoBarGrid.Height:0.0} infoY={InfoBarGrid.Frame.Y:0.0} draw=cW{_canvasW} cH{_canvasH} offY{_gridOffsetY:0.0} slot{_gridSlotSize:0.0} gridH{(_gridSlotSize * Rows + 6 * (Rows - 1)):0.0}");
        }
        Dispatcher.Dispatch(async () =>
        {
            await Task.Delay(700);  LogOnce("t700");
            await Task.Delay(900);  LogOnce("t1600");
        });
#endif
    }

    /// <summary>系统返回 / 滑动返回：道具页有未保存改动时先问是否保存，别直接退出去。</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_itemsTabActive)
        {
            _ = SwitchToBoxesTabAsync();
            return true;
        }
        return base.OnBackButtonPressed();
    }

    /// <summary>
    /// 系统返回 / 手势返回：与手柄 B 一致 —— 先关菜单/详情浮层，再处理道具页的未保存改动，
    /// 都无事可做时返回 false，交给全局回调 pop 回存档列表。
    /// </summary>
    private bool HandleBackAction()
    {
        if (_menuOpen)
        {
            CloseActionMenu();
            return true;
        }

        if (_isPhone && _phoneSheetVisible)
        {
            DeselectSlot();     // 收起浮层并清掉选中态，避免下次点同格直跳编辑器
            return true;
        }

        if (_itemsTabActive)
        {
            _ = SwitchToBoxesTabAsync();
            return true;
        }

        if (_moveMode)
        {
            CancelMoveMode();
            return true;
        }

        return false;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        CommitItems();
        ItemRow.Changed -= OnItemChanged;
        BackCoordinator.Unbind(HandleBackAction);
        // 隐藏浮层：只归零位移 + 透明，不再靠 TranslationY 挪出屏幕
        if (_isPhone)
        {
            PhoneSheetSeq++;            // 使任何在途的显隐动画收尾失效
            PhoneSheetPanel.TranslationY = 0;
            PhoneSheetPanel.Scale        = 1;
            PhoneSheetPanel.Opacity      = 0;
            PhoneSheetScrim.Opacity      = 0;
            PhoneDetailSheet.InputTransparent = true;
            _phoneSheetVisible = false;
            _phoneSheetPk = null;
        }
#if ANDROID
        GamepadRouter.KeyReceived        -= OnGamepadKey;
        GamepadRouter.BoxScrollRequested -= OnBoxScroll;
        CryService.Stop();   // 页面退出停声，避免切页后还在响 / 叠音
#endif
        ThemeService.ThemeChanged -= OnThemeChanged;
        _pulseTimer?.Stop();
        _radarAnimCts?.Cancel();
        _loadBoxCts.Cancel();
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (!_isPhone || width <= 0 || height <= 0) return;
        bool landscape = width > height;
        if (landscape != _isLandscapePhone)
            ApplyPhoneLayout(landscape);
    }

    /// <summary>
    /// Switches GamePage between portrait-phone and landscape-phone layouts.
    /// Portrait: trainer strip at top of box grid, bottom sheet for detail.
    /// Landscape: two-column split (TopScreenPanel left, box grid right) — mirrors Thor layout.
    /// Thor path is never touched by this method.
    /// </summary>
    private void ApplyPhoneLayout(bool landscape)
    {
        _isLandscapePhone = landscape;

        // 行/列间距只在横屏有意义：竖屏时 Row0 被压成 0 高、Column1 为 0 宽，
        // 但 2dp 间距照样从可用高度/宽度里扣，会让 BottomGrid 比银行页少 2dp，
        // 画布随之矮 2dp，网格垂直居中后就出现整体位移。竖屏归零彻底消除这个差。
        RootGrid.RowSpacing    = landscape ? 2 : 0;
        RootGrid.ColumnSpacing = landscape ? 2 : 0;

        if (landscape)
        {
            // 单栏：去掉左栏 TopScreenPanel，格子占满整宽；训练家与按键徽章移到底部
            RootGrid.RowDefinitions[0].Height    = GridLength.Star;
            RootGrid.RowDefinitions[1].Height    = new GridLength(0);
            RootGrid.ColumnDefinitions[0].Width  = GridLength.Star;
            RootGrid.ColumnDefinitions[1].Width  = new GridLength(0);
            Grid.SetRow(BottomGrid, 0);
            Grid.SetColumn(BottomGrid, 0);

            TopScreenPanel.IsVisible    = false;
            PhoneTrainerStrip.IsVisible = false;
            InfoBarGrid.IsVisible       = false;
            LsBottomBar.IsVisible       = true;

            // 竖向空间优先给格子：导航行压紧，搜索改作画布左上角的固定宽浮层
            BoxNavRow.Padding    = new Thickness(12, 2, 12, 2);

            // 搜索栏改作 overlay：挂在画布同一格（Row4）左上角，固定 200dp、不参与行测量。
            // Row3 空出来后画布自动变高，RecalcGridLayout 会按新尺寸重算并重新居中。
            Grid.SetRow(BoxSearchRow, 4);
            BoxSearchRow.HorizontalOptions = LayoutOptions.Start;
            BoxSearchRow.VerticalOptions   = LayoutOptions.Start;
            BoxSearchRow.WidthRequest      = 200;
            BoxSearchRow.Margin            = new Thickness(12, 2, 0, 0);
            BoxSearchRow.ZIndex            = 12;
            BoxSearchRow.HeightRequest     = 40;
            BoxSearchRow.IsVisible         = _landscapeSearchOpen;

            // ✕ 回到行内（框右侧），与竖屏一致；不再做框外独立圆钮
            SearchClearBtn.IsVisible    = true;

            // 放大镜：浮在 ◀▶ 行正下方（overlay，不占高度）；搜索打开时让位
            BoxSearchBtn.IsVisible   = !_landscapeSearchOpen;
            LsSearchDismiss.IsVisible = _landscapeSearchOpen;

            SyncLandscapeBottomBar();
            BuildLandscapeHints();
        }
        else
        {
            // Portrait: trainer strip at top, box fills screen, detail via bottom sheet
            RootGrid.RowDefinitions[0].Height    = new GridLength(0);
            RootGrid.RowDefinitions[1].Height    = GridLength.Star;
            RootGrid.ColumnDefinitions[0].Width  = GridLength.Star;
            RootGrid.ColumnDefinitions[1].Width  = new GridLength(0);
            Grid.SetRow(BottomGrid, 1);
            Grid.SetColumn(BottomGrid, 0);
            TopScreenPanel.IsVisible    = false;
            PhoneTrainerStrip.IsVisible = true;
            InfoBarGrid.IsVisible       = true;
            LsBottomBar.IsVisible       = false;

            // 搜索栏回到 Row3 整行（竖屏完全沿用原样）
            _landscapeSearchOpen           = false;
            Grid.SetRow(BoxSearchRow, 3);
            BoxSearchRow.HorizontalOptions = LayoutOptions.Fill;
            BoxSearchRow.VerticalOptions   = LayoutOptions.Fill;
            BoxSearchRow.WidthRequest      = -1;
            BoxSearchRow.ZIndex            = 0;
            BoxSearchRow.IsVisible         = true;
            SearchClearBtn.IsVisible       = true;   // 竖屏保留行内 ✕
            BoxSearchBtn.IsVisible         = false;
            LsSearchDismiss.IsVisible      = false;

            BoxNavRow.Padding          = new Thickness(16, 12, 16, 8);
            BoxSearchRow.HeightRequest = 44;
            BoxSearchRow.Margin        = new Thickness(12, 0, 12, 6);

            // Restore 3-column proportions so the panel is correct if we rotate back
            RestoreLandscapeColumns();
            _landscapeDetailView = 0;
        }

        // 详情卡片开着时转屏：内容 → 列排布 → 尺寸（先填内容再量高，否则量到旧内容）
        if (_phoneSheetVisible)
        {
            ApplySheetSize();
            if (_phoneSheetPk is { } rotPk) UpdatePhoneSheetContent(rotPk);
            ApplySheetContentLayout();
            ApplySheetHeight();
        }
    }

    // ──────────────────────────────────────────────
    //  横屏搜索浮层（固定 230dp，落在画布左侧留白条内）
    // ──────────────────────────────────────────────

    private bool _landscapeSearchOpen;

    private void OnSearchBoxTapped(object sender, TappedEventArgs e) => SetLandscapeSearch(true);

    private void OnSearchOverlayDismissTapped(object sender, TappedEventArgs e) => SetLandscapeSearch(false);

    private void OnClearSearchTapped(object sender, TappedEventArgs e)
    {
        // 清空才退出过滤；单纯收起浮层是保留过滤结果的
        SearchEntry.Text = string.Empty;
        if (_isLandscapePhone) SetLandscapeSearch(false);
    }

    private void SetLandscapeSearch(bool open)
    {
        if (!_isLandscapePhone) return;
        _landscapeSearchOpen = open;
        BoxSearchRow.IsVisible      = open;
        BoxSearchBtn.IsVisible      = !open;   // 浮层打开时让位，避免两个控件叠在左上角
        LsSearchDismiss.IsVisible   = open;
        if (open) Dispatcher.Dispatch(() => SearchEntry.Focus());
        else      SearchEntry.Unfocus();
    }

    private void OnThemeChanged()
    {
        SpriteBgImage.IsVisible = true;
        TopBgCanvas.InvalidateSurface();
        BoxCanvas.InvalidateSurface();
        RadarCanvas.InvalidateSurface();
    }

    // ──────────────────────────────────────────────
    //  训练家标识 / 横屏单栏底栏 / 存档切换
    // ──────────────────────────────────────────────

    /// <summary>把存档的身份信息同步到所有展示位（顶部、竖屏条、横屏底栏）。</summary>
    private void ApplyTrainerIdentity(SaveFile sav)
    {
        var game = $"Pokémon {sav.Version}";

        TrainerNameLabel.Text     = sav.OT;
        SaveGameLabel.Text        = game;
        TrainerTIDLabel.Text      = sav.TrainerTID7.ToString();
        TrainerPokedexLabel.Text  = sav.BoxCount.ToString();
        TrainerPlaytimeLabel.Text = sav.PlayTimeString;

        PhoneTrainerName.Text = sav.OT;
        PhoneGameLabel.Text   = game;

        LsTrainerName.Text = sav.OT;
        LsGameLabel.Text   = game;

        var iconFile = GetTrainerIconFile(sav);
        if (iconFile is null) return;

        var source = ImageSource.FromStream(
            ct => FileSystem.OpenAppPackageFileAsync($"gameicons/{iconFile}").WaitAsync(ct));
        TrainerGameIcon.Source = source;
        PhoneGameIcon.Source   = source;
        LsGameIcon.Source      = source;
    }

    private void SyncLandscapeBottomBar()
    {
        LsTrainerName.Text = PhoneTrainerName.Text;
        LsGameLabel.Text   = PhoneGameLabel.Text;
        LsGameIcon.Source  = PhoneGameIcon.Source;
    }

    /// <summary>横屏单栏中间的按键徽章（与竖屏提示条同一套样式）。每一枚都可点。</summary>
    private void BuildLandscapeHints()
    {
        if (LsHints.Count > 0) return;

        LsHints.Add(HintChips.Create("A", "#34D990", UiText.HintSelect,       () => OnHintKeyPressed("A")));
        LsHints.Add(HintChips.Create("Y", "#6BABFF", UiText.HintGrab,         () => OnHintKeyPressed("Y")));
        LsHints.Add(HintChips.Create("B", "#FF6B9D", UiText.HintBack,         () => OnHintKeyPressed("B")));
        LsHints.Add(HintChips.Create("R", "#60C0FF", UiText.HintCry,          () => OnHintKeyPressed("R")));

        // X 徽章已按用户反馈取消：详情浮层统一由 A（选中）打开，浮层右上角「切换」换视图。
        // 手柄上开动作菜单的键是 Start（不是 ≡），徽章按真实键名写
        LsHints.Add(HintChips.Create(UiText.KeyStartChar, "#F0C040", UiText.HintEdit, OpenActionMenu));
    }

    // ──────────────────────────────────────────────
    //  竖屏提示条：让 A / Y / B / R 四片与横屏一样可点
    //  （XAML 里只给「Start 编辑」那片挂了手势，其余是纯装饰）
    // ──────────────────────────────────────────────

    private bool _portraitHintsBound;

    private void BindPortraitHintTaps()
    {
        if (_portraitHintsBound) return;
        if (HintBadges.Children.Count < 4) return;
        _portraitHintsBound = true;

        string[] keys = ["A", "Y", "B", "R"];
        int i = 0;
        foreach (var child in HintBadges.Children)
        {
            if (i >= keys.Length) break;

            View? target = child is Microsoft.Maui.Controls.Layout layout && layout.Children.Count > 0
                ? layout.Children[0] as View
                : child as View;
            if (target is null) continue;

            var key = keys[i++];
            target.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => OnHintKeyPressed(key)),
            });
        }
    }

    private SaveSwitcherSheet? _saveSwitcher;

    private void EnsureSaveSwitcher()
    {
        if (_saveSwitcher is not null) return;
        _saveSwitcher = new SaveSwitcherSheet(_session, OnSavePickedAsync, UiText.BoxSwitchNote);
        Grid.SetRow(_saveSwitcher, 0);
        Grid.SetColumn(_saveSwitcher, 0);
        Grid.SetRowSpan(_saveSwitcher, 2);
        Grid.SetColumnSpan(_saveSwitcher, 2);
        RootGrid.Add(_saveSwitcher);
    }

    private void OnSaveChipTapped(object sender, TappedEventArgs e) => _ = OpenSaveSwitcherAsync();

    private async Task OpenSaveSwitcherAsync()
    {
        if (_session.SaveWriteBusy)
        {
            await DisplayAlertAsync("正在写入存档", "等这次写入完成再切换，避免写进错的存档。", "好");
            return;
        }
        if (_session.PendingMove is not null || _moveMode)
            await DisplayAlertAsync("先结束手上的移动",
                "正在搬动宝可梦。切存档会取消这次移动（银行里的宝可梦不会丢），先按 B 结束再切。", "好");
        if (!_session.SaveWriteBusy && _session.PendingMove is null && !_moveMode)
        {
            EnsureSaveSwitcher();
            await _saveSwitcher!.ShowAsync();
        }
    }

    private async Task OnSavePickedAsync(SaveEntry entry)
    {
        var err = await SaveSwitchService.SwitchAsync(_session, entry);
        if (err is not null)
        {
            await DisplayAlertAsync("切换失败", err, "OK");
            return;
        }

        _sav = _session.ActiveSave!;

        // 清编辑态
        _selectedSlot = -1;
        _moveMode     = false;
        _movePk       = null;
        _searchMode   = false;
        _searchResults = [];
        _cursorSlot   = 0;
        _boxIndex     = SaveSwitchService.RecallBoxIndex(_session.ActiveSaveFileUri, _sav.BoxCount);

        ApplyTrainerIdentity(_sav);
        if (_isLandscapePhone) SyncLandscapeBottomBar();

        if (_phoneSheetVisible) await HidePhoneDetailSheetAsync();

        _session.RescanNeeded = false;   // 只是切上下文，不需要重扫
        LoadBox(_boxIndex);
        UpdateTopPanel();
        UpdateInfoBar();
        BoxCanvas.InvalidateSurface();
    }

#if ANDROID
    private void OnBoxScroll(int dir)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (dir < 0) OnPrevBox(this, EventArgs.Empty);
            else         OnNextBox(this, EventArgs.Empty);
        });
#endif

    // ──────────────────────────────────────────────
    //  Box loading
    // ──────────────────────────────────────────────

    private async void LoadBox(int box)
    {
        if (_sav is null) return;

        // Cancel any in-flight load and abort its MAUI animation immediately.
        // This guarantees _boxIndex always reflects what is actually displayed.
        _loadBoxCts.Cancel();
        _loadBoxCts = new CancellationTokenSource();
        var ct = _loadBoxCts.Token;
        int loadSeq = ++_boxLoadSeq;

        BoxCanvas.CancelAnimations();
        BoxCanvas.TranslationX = 0;

        int slideDir = _boxSlideDir;
        _boxSlideDir = 0;

        try
        {
            // Slide out existing content (only when the canvas is visible — single-screen mode)
            bool canSlide = slideDir != 0 && BoxCanvas.Width > 10;
            if (canSlide)
            {
                await BoxCanvas.TranslateToAsync(-slideDir * BoxCanvas.Width, 0, 120, Easing.CubicIn);
                if (ct.IsCancellationRequested) { BoxCanvas.TranslationX = 0; return; }
            }

            string boxName;
            if (box == -1)
            {
                // Party view — virtual box before box 0
                var partyArr = new PKM[6];
                for (int i = 0; i < 6; i++)
                    partyArr[i] = _sav.GetPartySlotAtIndex(i);
                _currentBox = partyArr;
                boxName = "队伍";
            }
            else
            {
                _currentBox = _sav.GetBoxData(box);
                boxName = _sav is IBoxDetailName named
                    ? named.GetBoxName(box)
                    : $"Box {box + 1}";
            }
            BoxNameLabel.Text = boxName;
            if (_isPhone) PhoneBoxLabel.Text = boxName;

            // Update idle panel box info
            IdleBoxNameLabel.Text = boxName;
            int filled = _currentBox.Count(pk => pk.Species != 0);
            IdleBoxFillLabel.Text = $"{filled} / {_currentBox.Length} filled";

            if (!ct.IsCancellationRequested)
                _secondary.UpdateBoxGrid(
                    _currentBox, _cursorSlot, _selectedSlot,
                    _moveMode, _movePk, _moveSourceBox, _moveSourceSlot,
                    _boxIndex, boxName, _legalityCache, _showLegalityBadges);

            // Clear selected outline if the slot is now empty (e.g. Pokémon was moved/deleted in editor)
            if (_selectedSlot >= 0 && (_selectedSlot >= _currentBox.Length
                || _currentBox[_selectedSlot].Species == 0))
                _selectedSlot = -1;

            _legalityCache = new bool?[_currentBox.Length];

            // Update top panel immediately — don't wait for sprite preload.
            UpdateTopPanel();
            UpdateInfoBar();
            BoxCanvas.InvalidateSurface();

            // 首帧先画已有缓存，再后台补图重绘。
            // 原来这里 await 预加载（含联网）后才启动滑动动画，箱内某只 HOME 图取不到时
            // 会 3 源 ×2 次重试，观感就是"切箱子卡一秒"。
            var boxForPreload = _currentBox;
            if (canSlide)
            {
                // Pre-position canvas on the incoming side, then slide to rest
                BoxCanvas.TranslationX = slideDir * BoxCanvas.Width;
                await BoxCanvas.TranslateToAsync(0, 0, 140, Easing.CubicOut);
                if (ct.IsCancellationRequested) { BoxCanvas.TranslationX = 0; return; }
            }
            if (_showLegalityBadges) _ = RunLegalityBadgesAsync(boxForPreload);

            _ = PreloadBoxAndRepaintAsync(boxForPreload, ct, loadSeq);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // A navigation fired mid-load — snap canvas back and let the new load take over.
            BoxCanvas.TranslationX = 0;
        }
    }

    // ──────────────────────────────────────────────
    //  Box navigation
    // ──────────────────────────────────────────────

    /// <summary>后台补图：预加载完成后，仅当仍是同一箱、未被取消时重绘一次。</summary>
    private async Task PreloadBoxAndRepaintAsync(PKM[] box, CancellationToken ct, int loadSeq)
    {
        try
        {
            await _sprites.PreloadBoxAsync(box);
        }
        catch
        {
            return;
        }
        if (ct.IsCancellationRequested || loadSeq != _boxLoadSeq)
            return;
        BoxCanvas.InvalidateSurface();
    }

    private void OnPrevBox(object sender, EventArgs e)
    {
        if (_sav is null || _boxIndex <= -1) return;
        if (_boxIndex == 0 && !_sav.HasParty) return;
        _boxSlideDir = -1;
        _boxIndex--;
        DeselectSlot();
        LoadBox(_boxIndex);
    }

    private void OnNextBox(object sender, EventArgs e)
    {
        if (_sav is null || _boxIndex >= _sav.BoxCount - 1) return;
        _boxSlideDir = +1;
        _boxIndex++;
        DeselectSlot();
        LoadBox(_boxIndex);
    }

    // ──────────────────────────────────────────────
    //  Rendering — box grid (bottom screen)
    // ──────────────────────────────────────────────

    private void RecalcGridLayout(int canvasW, int canvasH)
    {
        if (canvasW == _lastCanvasW && canvasH == _lastCanvasH && _slotRects.Length == Columns * Rows)
            return;
        _lastCanvasW = canvasW;
        _lastCanvasH = canvasH;

        const float gap = 6f;
        const float padX = 14f, padY = 4f;

        float availW = canvasW - padX * 2 - gap * (Columns - 1);
        float availH = canvasH - padY * 2 - gap * (Rows - 1);
        float slotSize = MathF.Min(availW / Columns, availH / Rows);

        float gridW = slotSize * Columns + gap * (Columns - 1);
        float gridH = slotSize * Rows + gap * (Rows - 1);
        float offX = (canvasW - gridW) / 2f;
        float offY = (canvasH - gridH) / 2f;
        // 对齐到整像素：两页画布高相差不到 1px 时，居中结果会出现 0.5px 的亚像素偏移，
        // 落到屏幕上就是一边清晰一边发虚。取整后两页画出的网格位置逐像素一致。
        offX = MathF.Floor(offX);
        offY = MathF.Floor(offY);

        _gridSlotSize = slotSize;
        _gridOffsetX = offX;
        _gridOffsetY = offY;

        _slotRects = new SKRect[Columns * Rows];
        for (int i = 0; i < _slotRects.Length; i++)
        {
            int col = i % Columns;
            int row = i / Columns;
            float x = offX + col * (slotSize + gap);
            float y = offY + row * (slotSize + gap);
            _slotRects[i] = new SKRect(x, y, x + slotSize, y + slotSize);
        }
    }

    private void OnBoxPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        // 与页面背景同色，避免箱子页/银行页的画布各自形成一条色带
        canvas.Clear(ThemeService.PageBg);
        if (_currentBox.Length == 0) return;

        RecalcGridLayout(e.Info.Width, e.Info.Height);
#if ANDROID
        // 诊断（临时）：记录出现后的前几次绘制尺寸，用于捕捉进场瞬间的尺寸跳变
        if (_paintLogN < 8)
            Services.DiagLog.Add($"game paint#{++_paintLogN} cW={e.Info.Width} cH={e.Info.Height} offY={_gridOffsetY:0.0}");
#endif

        const float radius = 10f;

        // Pulse values
        float tBlue = (float)(_cursorTimer.Elapsed.TotalMilliseconds % 1800) / 1800f;
        float pulseBlue = 0.5f + 0.5f * MathF.Sin(tBlue * MathF.PI * 2);
        float tGreen = (float)(_cursorTimer.Elapsed.TotalMilliseconds % 1400) / 1400f;
        float pulseGreen = 0.5f + 0.5f * MathF.Sin(tGreen * MathF.PI * 2);

        for (int i = 0; i < _slotRects.Length; i++)
        {
            var rect = _slotRects[i];
            PKM pk;
            if (_searchMode)
            {
                if (i < _searchResults.Length) pk = _searchResults[i].Pk;
                else
                {
                    using var ep = new SKPaint { Color = ThemeService.SlotEmpty, IsAntialias = true };
                    canvas.DrawRoundRect(rect, radius, radius, ep);
                    using var eb = new SKPaint { Color = ThemeService.SlotBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                    canvas.DrawRoundRect(rect, radius, radius, eb);
                    continue;
                }
            }
            else
            {
                if (i >= _currentBox.Length) break;
                pk = _currentBox[i];
            }
            bool isCursor = i == _cursorSlot;
            bool isSelected = i == _selectedSlot && !_moveMode && !_searchMode;
            int effectiveSourceBox = _boxIndex == -1 ? -2 : _boxIndex;
            bool isSource = !_searchMode && _moveMode && _moveSourceBox == effectiveSourceBox && i == _moveSourceSlot;
            bool filled = pk.Species != 0;

            // ── Slot background ──
            var bgColor = filled ? ThemeService.SlotFilled : ThemeService.SlotEmpty;
            using var bgPaint = new SKPaint { Color = bgColor, IsAntialias = true };
            canvas.DrawRoundRect(rect, radius, radius, bgPaint);

            // Slot border
            using var borderPaint = new SKPaint
            {
                Color = ThemeService.SlotBorder,
                Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true,
            };
            canvas.DrawRoundRect(rect, radius, radius, borderPaint);

            // ── Sprite ──
            if (filled)
            {
                var sprite = _sprites.GetSprite(pk);
                float spriteScale = isCursor ? 0.75f : 0.70f; // 108% when cursor'd (0.70*1.08≈0.75)
                // Bounce: slot that just received the cursor pops out briefly
                if (i == _bounceSlot)
                {
                    long bounceElapsed = _cursorTimer.ElapsedMilliseconds - _bounceStartMs;
                    if (bounceElapsed < 300)
                    {
                        float bt = bounceElapsed / 300f;
                        float decay = (1f - bt) * (1f - bt); // ease-out quad
                        spriteScale += 0.12f * decay;
                    }
                }
                byte alpha = isSource ? (byte)70 : (byte)255;
                DrawSprite(canvas, sprite, rect, spriteScale, alpha);
            }

            // Ghost of grabbed Pokémon at cursor as drop preview
            if (_moveMode && isCursor && _movePk != null && !isSource)
            {
                var ghost = _sprites.GetSprite(_movePk);
                DrawSprite(canvas, ghost, rect, 0.70f, 110);
            }

            // ── Cursor system ──
            if (_moveMode && isCursor)
                DrawGreenCursor(canvas, rect, radius, pulseGreen);
            else if (isSelected)
                DrawSelectedCursor(canvas, rect, radius);
            else if (isCursor)
                DrawBlueCursor(canvas, rect, radius, pulseBlue);

            // ── Legality badge ──
            if (!_searchMode && _showLegalityBadges && i < _legalityCache.Length && _legalityCache[i] is bool legal)
            {
                var dotColor = legal ? new SKColor(60, 220, 110, 230) : new SKColor(255, 82, 82, 230);
                using var glowPaint = new SKPaint
                {
                    Color = dotColor.WithAlpha(80), IsAntialias = true,
                    ImageFilter = SKImageFilter.CreateBlur(4, 4),
                };
                float bx = rect.Right - 8f;
                float by = rect.Top + 8f;
                canvas.DrawCircle(bx, by, 5f, glowPaint);
                using var dotPaint = new SKPaint { Color = dotColor, IsAntialias = true };
                canvas.DrawCircle(bx, by, 3f, dotPaint);
            }

            // ── Held item indicator ──
            if (filled && pk.HeldItem > 0)
            {
                float ix = rect.Left + 8f;
                float iy = rect.Bottom - 8f;
                using var itemGlow = new SKPaint
                {
                    Color = new SKColor(240, 192, 64, 80), IsAntialias = true,
                    ImageFilter = SKImageFilter.CreateBlur(3, 3),
                };
                canvas.DrawCircle(ix, iy, 4f, itemGlow);
                using var itemDot = new SKPaint { Color = new SKColor(240, 192, 64, 220), IsAntialias = true };
                canvas.DrawCircle(ix, iy, 2.5f, itemDot);
            }
        }
    }

    private static void DrawSprite(SKCanvas canvas, SKBitmap sprite, SKRect slotRect, float scale, byte alpha)
    {
        float inner = slotRect.Width * scale;
        float aspect = sprite.Width > 0 ? (float)sprite.Width / sprite.Height : 1f;
        float drawW, drawH;
        if (aspect >= 1f) { drawW = inner; drawH = inner / aspect; }
        else { drawH = inner; drawW = inner * aspect; }
        float sx = slotRect.MidX - drawW / 2f;
        float sy = slotRect.MidY - drawH / 2f;
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(alpha) };
        canvas.DrawBitmap(sprite, SKRect.Create(sx, sy, drawW, drawH), paint);
    }

    private static void DrawBlueCursor(SKCanvas canvas, SKRect rect, float radius, float pulse)
    {
        // Layer 1: Fill + border
        using var fillPaint = new SKPaint { Color = new SKColor(59, 139, 255, 31), IsAntialias = true };
        canvas.DrawRoundRect(rect, radius, radius, fillPaint);
        using var strokePaint = new SKPaint
        {
            Color = SKColor.Parse("#5CA0FF"), Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f, IsAntialias = true,
        };
        canvas.DrawRoundRect(rect, radius, radius, strokePaint);

        // Layer 2: Outer glow ring
        float expand = 4f;
        var outerRect = new SKRect(rect.Left - expand, rect.Top - expand, rect.Right + expand, rect.Bottom + expand);
        float outerScale = 1f + 0.02f * pulse;
        canvas.Save();
        canvas.Scale(outerScale, outerScale, rect.MidX, rect.MidY);
        using var outerPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(92, 160, 255, (byte)(89 + 77 * pulse)),
            StrokeWidth = 2f, IsAntialias = true,
        };
        canvas.DrawRoundRect(outerRect, 13, 13, outerPaint);
        canvas.Restore();

        // Layer 3: Inner blur glow
        var glowRect = new SKRect(rect.Left - 1, rect.Top - 1, rect.Right + 1, rect.Bottom + 1);
        using var glowPaint = new SKPaint
        {
            Color = new SKColor(59, 139, 255, (byte)(46 + 31 * pulse)),
            ImageFilter = SKImageFilter.CreateBlur(6, 6),
            IsAntialias = true,
        };
        canvas.DrawRoundRect(glowRect, 11, 11, glowPaint);
    }

    private static void DrawSelectedCursor(SKCanvas canvas, SKRect rect, float radius)
    {
        // Static (no pulse) — brighter blue-white to distinguish from navigation cursor
        using var fillPaint = new SKPaint { Color = new SKColor(107, 171, 255, 30), IsAntialias = true };
        canvas.DrawRoundRect(rect, radius, radius, fillPaint);
        using var strokePaint = new SKPaint
        {
            Color = new SKColor(160, 200, 255), Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f, IsAntialias = true,
        };
        canvas.DrawRoundRect(rect, radius, radius, strokePaint);

        var outerRect = new SKRect(rect.Left - 4, rect.Top - 4, rect.Right + 4, rect.Bottom + 4);
        using var outerPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(107, 171, 255, 77),
            StrokeWidth = 2f, IsAntialias = true,
        };
        canvas.DrawRoundRect(outerRect, 13, 13, outerPaint);

        var glowRect = new SKRect(rect.Left - 1, rect.Top - 1, rect.Right + 1, rect.Bottom + 1);
        using var glowPaint = new SKPaint
        {
            Color = new SKColor(59, 139, 255, 51),
            ImageFilter = SKImageFilter.CreateBlur(7, 7),
            IsAntialias = true,
        };
        canvas.DrawRoundRect(glowRect, 11, 11, glowPaint);
    }

    private static void DrawGreenCursor(SKCanvas canvas, SKRect rect, float radius, float pulse)
    {
        // Layer 1
        using var fillPaint = new SKPaint { Color = new SKColor(60, 220, 110, 26), IsAntialias = true };
        canvas.DrawRoundRect(rect, radius, radius, fillPaint);
        using var strokePaint = new SKPaint
        {
            Color = SKColor.Parse("#3CDC6E"), Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f, IsAntialias = true,
        };
        canvas.DrawRoundRect(rect, radius, radius, strokePaint);

        // Layer 2: Outer glow ring (pulsing)
        float expand = 4f;
        var outerRect = new SKRect(rect.Left - expand, rect.Top - expand, rect.Right + expand, rect.Bottom + expand);
        float outerScale = 1f + 0.02f * pulse;
        canvas.Save();
        canvas.Scale(outerScale, outerScale, rect.MidX, rect.MidY);
        using var outerPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(60, 220, 110, (byte)(89 + 77 * pulse)),
            StrokeWidth = 2f, IsAntialias = true,
        };
        canvas.DrawRoundRect(outerRect, 13, 13, outerPaint);
        canvas.Restore();

        // Layer 3: Inner blur glow
        var glowRect = new SKRect(rect.Left - 1, rect.Top - 1, rect.Right + 1, rect.Bottom + 1);
        using var glowPaint = new SKPaint
        {
            Color = new SKColor(60, 220, 110, (byte)(38 + 26 * pulse)),
            ImageFilter = SKImageFilter.CreateBlur(7, 7),
            IsAntialias = true,
        };
        canvas.DrawRoundRect(glowRect, 11, 11, glowPaint);
    }

    // ──────────────────────────────────────────────
    //  Rendering — static sprite fallback (top-left)
    // ──────────────────────────────────────────────

    private void OnPreviewPaint(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_previewPk is null) return;

        var sprite = _sprites.GetSprite(_previewPk);
        float aspect = sprite.Width > 0 ? (float)sprite.Width / sprite.Height : 1f;
        float w = e.Info.Width, h = e.Info.Height;
        float drawW, drawH;
        // Contain: fit within canvas bounds preserving aspect ratio
        if (w / h <= aspect) { drawW = w; drawH = w / aspect; }
        else                 { drawH = h; drawW = h * aspect; }
        float sx = (w - drawW) / 2f;
        float sy = (h - drawH) / 2f;
        canvas.DrawBitmap(sprite, SKRect.Create(sx, sy, drawW, drawH));
    }

    // ──────────────────────────────────────────────
    //  Rendering — hexagonal stat radar (top-right)
    // ──────────────────────────────────────────────

    // HP=red, Atk=orange, Def=yellow, Spe=teal, SpD=blue, SpA=purple
    private static readonly SKColor[] StatColors =
    [
        new SKColor(255,  80,  80),   // HP
        new SKColor(255, 150,  50),   // Atk
        new SKColor(240, 210,  50),   // Def
        new SKColor( 50, 210, 160),   // Spe
        new SKColor( 80, 140, 255),   // SpD
        new SKColor(185,  90, 255),   // SpA
    ];

    private void OnRadarPaint(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_previewPk is null) return;

        const int n  = 6;
        float visMax = _radarVisMax;

        int[] ringValues =
        [
            (int)(visMax * 0.25f),
            (int)(visMax * 0.50f),
            (int)(visMax * 0.75f),
            (int)visMax,
        ];

        string[] labels = ["HP", "Atk", "Def", "Spe", "SpD", "SpA"];

        float margin = Math.Min(e.Info.Width, e.Info.Height) * 0.16f;
        float cx = e.Info.Width  / 2f;
        float cy = e.Info.Height / 2f;
        float r  = Math.Min(cx, cy) - margin;

        // ── Grid rings ──────────────────────────────────────────────────
        using var ringPaint      = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
        float ringLabelSz        = Math.Max(10f, r * 0.09f);
        using var ringFont       = new SKFont(SKTypeface.Default, ringLabelSz);
        using var ringLabelPaint = new SKPaint { Color = ThemeService.RadarStat, IsAntialias = true };

        for (int ri = 0; ri < ringValues.Length; ri++)
        {
            float frac = ringValues[ri] / visMax;
            float rr   = r * frac;
            ringPaint.Color = ThemeService.RadarGrid.WithAlpha((byte)(40 + ri * 25));
            DrawHexPath(canvas, cx, cy, rr, n, ringPaint);

            float lx = cx;
            float ly = cy - rr - ringLabelSz * 0.25f;
            canvas.DrawText(ringValues[ri].ToString(), lx, ly, SKTextAlign.Center, ringFont, ringLabelPaint);
        }

        // ── Colored axes ────────────────────────────────────────────────
        using var axisPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
        for (int i = 0; i < n; i++)
        {
            float angle = MathF.PI * 2 * i / n - MathF.PI / 2;
            axisPaint.Color = StatColors[i].WithAlpha(70);
            canvas.DrawLine(cx, cy, cx + r * MathF.Cos(angle), cy + r * MathF.Sin(angle), axisPaint);
        }

        // ── Pre-compute vertex positions ────────────────────────────────
        float[] vx = new float[n];
        float[] vy = new float[n];
        for (int i = 0; i < n; i++)
        {
            float angle = MathF.PI * 2 * i / n - MathF.PI / 2;
            float v  = Math.Clamp(_radarCurrent[i] / visMax, 0f, 1f);
            vx[i] = cx + r * v * MathF.Cos(angle);
            vy[i] = cy + r * v * MathF.Sin(angle);
        }

        // ── Colored wedge fills (triangle per stat from center) ─────────
        using var wedgePaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            using var wedgePath = new SKPath();
            wedgePath.MoveTo(cx, cy);
            wedgePath.LineTo(vx[i], vy[i]);
            wedgePath.LineTo(vx[j], vy[j]);
            wedgePath.Close();
            wedgePaint.Color = StatColors[i].WithAlpha(90);
            canvas.DrawPath(wedgePath, wedgePaint);
        }

        // ── Outline stroke ───────────────────────────────────────────────
        using var statPath = new SKPath();
        for (int i = 0; i < n; i++)
        {
            if (i == 0) statPath.MoveTo(vx[i], vy[i]);
            else        statPath.LineTo(vx[i], vy[i]);
        }
        statPath.Close();
        using var strokePaint = new SKPaint
        {
            Color = ThemeService.RadarLabel.WithAlpha(200),
            Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true,
        };
        canvas.DrawPath(statPath, strokePaint);

        // ── Colored vertex dots ──────────────────────────────────────────
        using var dotPaint = new SKPaint { IsAntialias = true };
        for (int i = 0; i < n; i++)
        {
            dotPaint.Color = StatColors[i];
            canvas.DrawCircle(vx[i], vy[i], 5f, dotPaint);
        }

        // ── Axis labels (name + value) ───────────────────────────────────
        float textR   = r + margin * 0.60f;
        float labelSz = Math.Max(11f, r * 0.11f);
        float valueSz = Math.Max(14f, r * 0.14f);
        using var labelFont  = new SKFont(SKTypeface.Default, labelSz);
        using var valueFont  = new SKFont(SKTypeface.Default, valueSz) { Embolden = true };
        using var namePaint  = new SKPaint { IsAntialias = true };
        using var valuePaint = new SKPaint { IsAntialias = true };

        for (int i = 0; i < n; i++)
        {
            float angle = MathF.PI * 2 * i / n - MathF.PI / 2;
            float lx = cx + textR * MathF.Cos(angle);
            float ly = cy + textR * MathF.Sin(angle);
            namePaint.Color  = StatColors[i].WithAlpha(180);
            valuePaint.Color = StatColors[i];
            canvas.DrawText(labels[i],                        lx, ly,                  SKTextAlign.Center, labelFont, namePaint);
            canvas.DrawText(((int)_radarCurrent[i]).ToString(), lx, ly + valueSz * 1.1f, SKTextAlign.Center, valueFont, valuePaint);
        }
    }

    private static float[] GetRadarStats(PKM pk)
    {
        var s = pk.GetStats(pk.PersonalInfo);
        // Clockwise from top: HP, Atk, Def, Spe, SpD, SpA
        return [(float)s[0], (float)s[1], (float)s[2], (float)s[5], (float)s[4], (float)s[3]];
    }

    private void StartRadarAnimation(float[] target)
    {
        bool adaptive = Preferences.Default.Get(SettingsPage.KeyRadarAdaptive, false);
        _radarVisMax = adaptive
            ? Math.Max(target.Max() / 0.78f, 80f)   // targets ~78% fill for highest stat
            : 255f;

        _radarAnimCts?.Cancel();
        _radarAnimCts = new CancellationTokenSource();
        var ct    = _radarAnimCts.Token;
        var start = _radarCurrent.ToArray();

        _ = Task.Run(async () =>
        {
            const int durationMs = 380;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                float raw  = (float)sw.ElapsedMilliseconds / durationMs;
                float t    = Math.Clamp(raw, 0f, 1f);
                // Ease in-out cubic
                float ease = t < 0.5f ? 4 * t * t * t : 1 - MathF.Pow(-2 * t + 2, 3) / 2;

                for (int i = 0; i < 6; i++)
                    _radarCurrent[i] = start[i] + (target[i] - start[i]) * ease;

                MainThread.BeginInvokeOnMainThread(() => RadarCanvas.InvalidateSurface());

                if (t >= 1f) break;
                await Task.Delay(16, ct).ConfigureAwait(false);
            }
        }, ct);
    }

    private static void DrawHexPath(SKCanvas canvas, float cx, float cy, float r, int n, SKPaint paint)
    {
        using var path = new SKPath();
        for (int i = 0; i < n; i++)
        {
            float angle = MathF.PI * 2 * i / n - MathF.PI / 2;
            float x = cx + r * MathF.Cos(angle);
            float y = cy + r * MathF.Sin(angle);
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        path.Close();
        canvas.DrawPath(path, paint);
    }

    // ──────────────────────────────────────────────
    //  Animated sprite (cached from Showdown CDN)
    // ──────────────────────────────────────────────

    private async void LoadAnimatedSprite(PKM pk)
    {
        if (!Preferences.Default.Get(SettingsPage.KeyAnimated3D, true))
        {
            // 3D sprites disabled — show static HOME sprite only
            _spriteHasCurrent = false;
            SpriteWebView.IsVisible = false;
            PreviewCanvas.IsVisible = true;
            PreviewCanvas.InvalidateSurface();
            return;
        }

        // 竞态保护：快速移动光标时，先发出的请求可能晚返回，不能覆盖当前选中。
        int requestKey = (pk.Species << 10) | (pk.Form << 1) | (pk.IsShiny ? 1 : 0);
        var dataUri = await ResolveAnimatedDataUriAsync(pk);
        if (requestKey != _previewSpecies)
            return;

        if (dataUri is null)
        {
            // Nothing cached and download failed — show static sprite and hide the stale gif
            _spriteHasCurrent = false;
            SpriteWebView.IsVisible = false;
            PreviewCanvas.IsVisible = true;
            PreviewCanvas.InvalidateSurface();
            return;
        }

        // Use CSS viewport units (vw/vh) for the sprite so sizing is always relative to
        // the WebView container — pixel values derived from MAUI layout measurements are
        // unreliable here because the layout pass may not have completed yet.
        int pct = _isLandscapePhone ? 55 : 80;

        _spriteHasCurrent = true;
        if (!_spriteWebViewReady)
        {
            SpriteWebView.IsVisible = true;
            PreviewCanvas.IsVisible = false;
            await Task.Delay(50);
            SpriteWebView.Source    = new HtmlWebViewSource { Html = BuildSpriteShell(dataUri, pct) };
            _spriteWebViewReady     = true;
        }
        else
        {
            var js = $$"""
                var s=document.getElementById('s');
                s.src='{{dataUri}}';
                s.style.maxWidth='{{pct}}vw';
                s.style.maxHeight='{{pct}}vh';
                """;
            await SpriteWebView.EvaluateJavaScriptAsync(js);
        }
    }

    /// <summary>
    /// 解析动图 data URI：形态优先、基础形态兜底。返回 null 表示没有可用动图。
    /// slug 必须来自英文表：中文字符经 ToShowdownSlug 的 [^a-z0-9] 过滤后为空串，
    /// 会拼出 ani/.gif 这类永远命中不了的路径（3D 立绘因此长期不显示）。
    /// </summary>
    private async Task<string?> ResolveAnimatedDataUriAsync(PKM pk)
    {
        var locale = EnStrings;
        var name = pk.Species < locale.specieslist.Length
            ? locale.specieslist[pk.Species]
            : pk.Species.ToString();

        var speciesSlug = ToShowdownSlug(name);
        var shiny       = pk.IsShiny;

        // Build form-specific slug (e.g. "giratina-origin", "meloetta-pirouette")
        string slug = speciesSlug;
        string? baseSlug = null;
        if (pk.Form != 0)
        {
            var formName = ShowdownParsing.GetStringFromForm(pk.Form, locale, pk.Species, pk.Context);
            var formSuffix = ToShowdownFormSuffix(formName);
            if (formSuffix.Length > 0)
            {
                slug     = $"{speciesSlug}-{formSuffix}";
                baseSlug = speciesSlug; // fallback if CDN doesn't have this form
            }
        }

        // Fetch from disk cache or download; falls back to base-form animated sprite
        var dataUri = await Services.SpriteCacheService.GetDataUriAsync(slug, shiny);
        if (dataUri is null && baseSlug is not null)
            dataUri = await Services.SpriteCacheService.GetDataUriAsync(baseSlug, shiny);
        return dataUri;
    }

    /// <summary>
    /// 手机详情卡（竖屏与横屏共用）的动图加载。手机端 TopScreenPanel 整体隐藏，
    /// 原来只写 SpriteWebView 的动图在手机上永远不可见，这里改用卡片内的 PhoneSheetWebView。
    /// </summary>
    private async Task LoadSheetAnimatedSpriteAsync(PKM pk)
    {
        int key = (pk.Species << 10) | (pk.Form << 1) | (pk.IsShiny ? 1 : 0);

        // 同一只（同种/同形态/同闪光）且已有动图在显示：不重载，避免每次点同一格闪一下
        if (key == _sheetAnimKey && _sheetHasCurrent && _sheetWebViewReady)
            return;

        int seq = ++_sheetAnimSeq;
        _sheetAnimKey = key;

        if (!Preferences.Default.Get(SettingsPage.KeyAnimated3D, true))
        {
            _sheetHasCurrent = false;
            PhoneSheetWebView.IsVisible = false;
            PhoneSheetCanvas.IsVisible = true;
            return;
        }

        var dataUri = await ResolveAnimatedDataUriAsync(pk);
        if (seq != _sheetAnimSeq)
            return;

        if (dataUri is null)
        {
            _sheetHasCurrent = false;
            PhoneSheetWebView.IsVisible = false;
            PhoneSheetCanvas.IsVisible = true;
            PhoneSheetCanvas.InvalidateSurface();
            return;
        }

        _sheetHasCurrent = true;
        PhoneSheetCanvas.IsVisible = true;   // 垫底，等 WebView 导航完成再收起
        PhoneSheetWebView.IsVisible = true;

        if (!_sheetWebViewReady)
        {
            PhoneSheetWebView.Source = new HtmlWebViewSource { Html = BuildSpriteShell(dataUri, 100) };
            _sheetWebViewReady = true;
        }
        else
        {
            await PhoneSheetWebView.EvaluateJavaScriptAsync(
                $"var s=document.getElementById('s');s.src='{dataUri}';");
        }

        if (seq != _sheetAnimSeq)
        {
            // 期间又切了对象，让新的那次请求决定最终显示
            return;
        }
    }

    private static string ToShowdownSlug(string speciesName)
        => Services.SpriteCacheService.ToShowdownSlug(speciesName);

    private static string ToShowdownFormSuffix(string formName)
        => Services.SpriteCacheService.ToShowdownFormSuffix(formName);

    private static string BuildSpriteShell(string src, int pct) => $$"""
        <!DOCTYPE html>
        <html><head>
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <style>*{margin:0;padding:0}body{background:transparent;display:flex;align-items:center;justify-content:center;width:100vw;height:100vh;overflow:hidden}</style>
        </head><body>
        <img id="s" src="{{src}}"
             style="max-width:{{pct}}vw;max-height:{{pct}}vh;width:auto;height:auto;object-fit:contain;image-rendering:pixelated;filter:drop-shadow(0 2px 6px rgba(0,0,0,0.6))">
        </body></html>
        """;

    // ──────────────────────────────────────────────
    //  Pokémon cry (Android)
    // ──────────────────────────────────────────────

#if ANDROID
    private void PlayCryForCurrentSlot()
    {
        if (_previewPk?.Species > 0)
        {
            var en = EnStrings.specieslist;
            var name = _previewPk.Species < en.Length
                ? en[_previewPk.Species]
                : _previewPk.Species.ToString();
            _lastCrySlug = ""; // clear debounce so R3 always re-plays
            PlayCry(name);
        }
    }

    private async void PlayCry(string speciesName)
    {
        var slug = ToShowdownSlug(speciesName);
        if (string.IsNullOrWhiteSpace(slug) || slug == _lastCrySlug) return;
        _lastCrySlug = slug;

        bool ok = await CryService.PlayAsync(slug);
        if (!ok && _lastCrySlug == slug) _lastCrySlug = "";   // 拿不到文件就放开防抖，允许重按重试
    }

    /// <summary>预取当前宝可梦的叫声（打开详情/选中格子时调用），点 R 时大概率已落地。</summary>
    private void PrefetchCry(PKM? pk)
    {
        if (pk is null || pk.Species <= 0) return;
        var en = EnStrings.specieslist;
        var name = pk.Species < en.Length ? en[pk.Species] : "";
        CryService.Prefetch(CryService.Slug(name));
    }
#endif

    // ──────────────────────────────────────────────
    //  Top screen background (gradient glow)
    // ──────────────────────────────────────────────

    private void OnTopBgPaint(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        float w = e.Info.Width;
        float h = e.Info.Height;

        // Semi-transparent overlay so text stays readable over the background image.
        // Light mode uses a heavier white wash to keep contrast; dark uses a light dark tint.
        var overlayColor = ThemeService.Current == PkTheme.Light
            ? new SKColor(242, 244, 248, 80)    // ~31% white wash — image shows through
            : new SKColor(7, 12, 26, 60);        // ~24% dark tint
        using var dimPaint = new SKPaint { Color = overlayColor };
        canvas.DrawRect(0, 0, w, h, dimPaint);

        // Radial glow — game accent color at low opacity
        var gameColor = _sav != null
            ? Theme.GameColors.Get(_sav.Version).Light
            : new SKColor(59, 139, 255);
        using var glowPaint1 = new SKPaint();
        glowPaint1.Shader = SKShader.CreateRadialGradient(
            new SKPoint(w * 0.2f, h * 0.5f), Math.Min(w, h) * 0.5f,
            [gameColor.WithAlpha(20), SKColors.Transparent],
            SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, w, h, glowPaint1);

        // Radial glow — AccentPurple
        using var glowPaint2 = new SKPaint();
        glowPaint2.Shader = SKShader.CreateRadialGradient(
            new SKPoint(w * 0.8f, h * 0.3f), Math.Min(w, h) * 0.45f,
            [new SKColor(167, 139, 250, 15), SKColors.Transparent],
            SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, w, h, glowPaint2);
    }

    // ──────────────────────────────────────────────
    //  Type badges
    // ──────────────────────────────────────────────

    private void UpdateTypeBadges(PKM pk)
    {
        int type1 = pk.PersonalInfo.Type1;
        int type2 = pk.PersonalInfo.Type2;
        bool dual = type2 != type1;

        ApplyTypeBadge(_typeBadges[0], type1, visible: true);
        ApplyTypeBadge(_typeBadges[1], type2, visible: dual);
    }

    private void ApplyTypeBadge(Border badge, int typeId, bool visible)
    {
        badge.IsVisible = visible;
        if (!visible) return;

        var typeName = typeId < _strings.types.Length ? _strings.types[typeId] : "???";
        badge.BackgroundColor = Theme.TypeColors.Map.TryGetValue(typeName, out var c)
            ? Color.FromUint((uint)((c.Alpha << 24) | (c.Red << 16) | (c.Green << 8) | c.Blue))
            : Color.FromArgb("#A8A878");
        ((Label)badge.Content!).Text = typeName.ToUpperInvariant();
    }

    // ──────────────────────────────────────────────
    //  Move rows
    // ──────────────────────────────────────────────

    private void UpdateMoveRows(PKM pk)
    {
        var moveNames = new[] { MoveName0, MoveName1, MoveName2, MoveName3 };
        var moveCats = new[] { MoveCat0, MoveCat1, MoveCat2, MoveCat3 };
        var movePPs = new[] { MovePP0, MovePP1, MovePP2, MovePP3 };
        var moveDots = new[] { MoveDot0, MoveDot1, MoveDot2, MoveDot3 };
        var moveRows = new[] { MoveRow0, MoveRow1, MoveRow2, MoveRow3 };

        int[] moves = [pk.Move1, pk.Move2, pk.Move3, pk.Move4];
        int[] pps = [pk.Move1_PP, pk.Move2_PP, pk.Move3_PP, pk.Move4_PP];

        for (int i = 0; i < 4; i++)
        {
            if (moves[i] == 0)
            {
                moveRows[i].IsVisible = false;
                continue;
            }
            moveRows[i].IsVisible = true;

            var moveName = moves[i] < _strings.movelist.Length
                ? _strings.movelist[moves[i]] : $"Move {moves[i]}";
            moveNames[i].Text = moveName;
            movePPs[i].Text = $"PP {pps[i]}";

            // Move type dot color
            var ctx = _sav?.Context ?? EntityContext.Gen9;
            var moveType = MoveInfo.GetType((ushort)moves[i], ctx);
            var moveTypeName = moveType < _strings.types.Length
                ? _strings.types[moveType] : "Normal";
            if (Theme.TypeColors.Map.TryGetValue(moveTypeName, out var typeColor))
                moveDots[i].Fill = new SolidColorBrush(
                    Color.FromUint((uint)((typeColor.Alpha << 24) | (typeColor.Red << 16) | (typeColor.Green << 8) | typeColor.Blue)));

            // Category placeholder
            moveCats[i].Text = "";
        }
    }

    // ──────────────────────────────────────────────
    //  Trainer icon helper
    // ──────────────────────────────────────────────

    internal static string? GetTrainerIconFile(SaveFile sav)
    {
        // SAV4HGSS always reports GameVersion.HGSS — never HG or SS individually.
        // Use majority vote across all party + all boxes (same logic as SaveDirectoryService).
        if (sav.Version == GameVersion.HGSS)
        {
            var resolved = SaveDirectoryService.ResolveHGSS(sav);
            if (resolved == GameVersion.SS) return "soulsilver.png";
            return "heartgold.png"; // default HG if empty save
        }
        return GetTrainerIconFile(sav.Version);
    }

    internal static string? GetTrainerIconFile(GameVersion v) => v switch
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

    // ──────────────────────────────────────────────
    //  Touch input
    // ──────────────────────────────────────────────

    private void OnBoxTapped(object sender, TappedEventArgs e)
    {
        if (_sav is null || sender is not View view) return;

        var point = e.GetPosition(view);
        if (point is null) return;

        // Use density to convert dp touch coords to pixel coords
        float density = (float)DeviceDisplay.MainDisplayInfo.Density;
        float px = (float)point.Value.X * density;
        float py = (float)point.Value.Y * density;

        // Hit-test against pre-computed slot rects
        int index = -1;
        int hitMax = _searchMode ? _searchResults.Length : _currentBox.Length;
        for (int i = 0; i < _slotRects.Length && i < hitMax; i++)
        {
            if (_slotRects[i].Contains(px, py)) { index = i; break; }
        }

        if ((uint)index >= (uint)hitMax) return;

        _cursorSlot = index;

        if (_searchMode)
        {
            NavigateToSearchResult(index);
            return;
        }

        if (_moveMode)
        {
            ExecuteMove();
            return;
        }

        UpdateTopPanel();
        UpdateInfoBar();

        if (_currentBox[index].Species != 0)
        {
            // 点格子只负责选中并打开详情；「再点一次进编辑器」的旧快捷已取消（用户 2026-11-26 定）
            SelectSlot(index);
        }
        else
        {
            DeselectSlot();
        }
    }

    private void NavigateToSearchResult(int index)
    {
        if ((uint)index >= (uint)_searchResults.Length) return;
        var result = _searchResults[index];
        _searchMode = false;
        _searchResults = [];
        MainThread.BeginInvokeOnMainThread(() => SearchEntry.Text = "");
        _boxIndex    = result.Box;
        _cursorSlot  = result.Slot;
        _boxSlideDir = 0;
        LoadBox(result.Box);
    }

    // ──────────────────────────────────────────────
    //  Gamepad input
    // ──────────────────────────────────────────────

#if ANDROID
    private void OnGamepadKey(Android.Views.Keycode keyCode, Android.Views.KeyEventActions action)
    {
        if (action != Android.Views.KeyEventActions.Down) return;
        MainThread.BeginInvokeOnMainThread(() => HandleGamepadKey(keyCode));
    }

    private void HandleGamepadKey(Android.Views.Keycode keyCode)
    {
        // Items tab intercepts all input while active
        if (_itemsTabActive) { HandleItemsKey(keyCode); return; }

        // Action menu intercepts all input while open
        if (_menuOpen)
        {
            switch (keyCode)
            {
                case Android.Views.Keycode.DpadUp:
                case Android.Views.Keycode.DpadLeft:
                    _menuCursor = Math.Max(0, _menuCursor - 1); UpdateMenuHighlight(); break;
                case Android.Views.Keycode.DpadDown:
                case Android.Views.Keycode.DpadRight:
                    _menuCursor = Math.Min(1, _menuCursor + 1); UpdateMenuHighlight(); break;
                case Android.Views.Keycode.ButtonA:
                    if (_menuCursor == 0) OnExportClicked(this, EventArgs.Empty);
                    else                  OnItemsMenuClicked(this, EventArgs.Empty);
                    CloseActionMenu(); break;
                case Android.Views.Keycode.ButtonB:
                case Android.Views.Keycode.ButtonStart:
                    CloseActionMenu(); break;
            }
            return;
        }

        switch (keyCode)
        {
            case Android.Views.Keycode.DpadUp:    MoveCursor(-Columns); break;
            case Android.Views.Keycode.DpadDown:  MoveCursor(+Columns); break;
            case Android.Views.Keycode.DpadLeft:  MoveCursor(-1);       break;
            case Android.Views.Keycode.DpadRight: MoveCursor(+1);       break;

            case Android.Views.Keycode.ButtonA:
                if (_moveMode) { ExecuteMove(); break; }
                if (_searchMode) { NavigateToSearchResult(_cursorSlot); break; }
                if (_cursorSlot < _currentBox.Length && _currentBox[_cursorSlot].Species != 0)
                    SelectSlot(_cursorSlot);   // 与点击一致：A 只打开详情，不再直达编辑器
                break;

            case Android.Views.Keycode.ButtonB:
                if (_moveMode) { CancelMoveMode(); break; }
                if (_selectedSlot >= 0) DeselectSlot();
                else OnMenuClicked(this, EventArgs.Empty);
                break;

            case KeyMap.BankOutKey: _ = SwapToBank(-1); break;
            case KeyMap.BankInKey: _ = SwapToBank(+1); break;

            case Android.Views.Keycode.ButtonX:
                // 横屏已取消 X 视图（详情统一由 A 打开）；竖屏沿用原行为（进数据库页）
                if (!_isLandscapePhone) OnSearchClicked(this, EventArgs.Empty);
                break;
            case Android.Views.Keycode.ButtonY:
                if (_moveMode) { CancelMoveMode(); break; }
                if (_searchMode) break;
                // Y 只有抓取：光标在空格时不跳礼物页（用户 2026-11-26 定稿）
                if (_cursorSlot < _currentBox.Length && _currentBox[_cursorSlot].Species != 0)
                    EnterMoveMode();
                break;
            case Android.Views.Keycode.ButtonSelect: OnSettingsClicked(this, EventArgs.Empty); break;
            case Android.Views.Keycode.ButtonStart:  OpenActionMenu(); break;
            case KeyMap.CryKey: PlayCryForCurrentSlot(); break;
        }
    }
#endif

    private static void Haptic() { try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { } }

    private void MoveCursor(int delta)
    {
        if (delta == -1 && _cursorSlot % Columns == 0)           return;
        if (delta == +1 && _cursorSlot % Columns == Columns - 1) return;

        int next = _cursorSlot + delta;
        int maxSlots = _searchMode ? _searchResults.Length : _currentBox.Length;
        if ((uint)next >= (uint)maxSlots) return;

        Haptic();
        _cursorSlot    = next;
        _bounceSlot    = next;
        _bounceStartMs = _cursorTimer.ElapsedMilliseconds;
        UpdateTopPanel();
        UpdateInfoBar();
        BoxCanvas.InvalidateSurface();
        _secondary.UpdateCursor(_cursorSlot, _selectedSlot, _moveMode, _movePk, _boxIndex);
    }

    private void OpenActionMenu()
    {
        _menuOpen   = true;
        _menuCursor = 0;

        // Apply theme colors in code — DynamicResource doesn't update on hidden elements
        ActionMenuPanel.BackgroundColor = ThemeColor("ThActionMenuPanel", "#0D1528");
        ActionMenuPanel.Stroke          = ThemeColor("ThActionMenuStroke", "#2A3F70");
        var textPrimary = ThemeColor("ThTextPrimary", "#EDF0FF");
        foreach (var label in new[] { ExportMenuLabel, ItemsMenuLabel })
            label.TextColor = textPrimary;

        ActionMenuOverlay.Opacity = 0;
        ActionMenuPanel.TranslationY = 48;
        ActionMenuOverlay.IsVisible = true;
        UpdateMenuHighlight();
        _ = ActionMenuOverlay.FadeToAsync(1, 180, Easing.CubicOut);
        _ = ActionMenuPanel.TranslateToAsync(0, 0, 180, Easing.CubicOut);
    }

    private async void CloseActionMenu()
    {
        _menuOpen = false;
        await Task.WhenAll(
            ActionMenuOverlay.FadeToAsync(0, 130, Easing.CubicIn),
            ActionMenuPanel.TranslateToAsync(0, 36, 130, Easing.CubicIn));
        ActionMenuOverlay.IsVisible = false;
        ActionMenuOverlay.Opacity   = 1;
        ActionMenuPanel.TranslationY = 0;
    }

    private static Color ThemeColor(string key, string fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
            return c;
        return Color.FromArgb(fallback);
    }

    private void UpdateMenuHighlight()
    {
        var normalBg  = ThemeColor("ThSettingsRow",      "#111827");
        var focusBg   = ThemeColor("ThSettingsRowFocus", "#182845");
        var accent    = ThemeColor("ThAccent",           "#3B8BFF");

        MenuItem_Export.BackgroundColor = _menuCursor == 0 ? focusBg : normalBg;
        MenuItem_Items.BackgroundColor  = _menuCursor == 1 ? focusBg : normalBg;
        MenuItem_Export.Stroke          = _menuCursor == 0 ? accent  : Colors.Transparent;
        MenuItem_Items.Stroke           = _menuCursor == 1 ? accent  : Colors.Transparent;
    }

    // ──────────────────────────────────────────────
    //  Top panel — follows cursor
    // ──────────────────────────────────────────────

    private void UpdateInfoBar()
    {
        if (_currentBox.Length == 0)
        {
            InfoSpeciesNum.Text = "";
            InfoSpeciesName.Text = "空箱子";
            return;
        }

        var pk = CursorPk;
        if (pk?.Species > 0)
        {
            var name = pk.Species < _strings.specieslist.Length
                ? _strings.specieslist[pk.Species] : pk.Species.ToString();
            InfoSpeciesNum.Text = $"#{pk.Species:000}";
            InfoSpeciesName.Text = _searchMode
                ? name
                : _moveMode
                    ? $"Moving {name}..."
                    : $"{name} · Lv.{pk.CurrentLevel}";
        }
        else
        {
            InfoSpeciesNum.Text = "";
            InfoSpeciesName.Text = _searchMode ? $"{_searchResults.Length} results" : "空位";
        }
    }

    private void UpdateTopPanel()
    {
        if (!_searchMode && _currentBox.Length == 0) return;
        var pk = CursorPk;
        if (pk?.Species > 0)
            ShowPokemonPreview(pk);
        else
            ShowIdlePanel();
    }

    private void ShowPokemonPreview(PKM pk)
    {
        _previewPk = pk;

        var speciesName = pk.Species < _strings.specieslist.Length
            ? _strings.specieslist[pk.Species] : pk.Species.ToString();
        var natureName = (int)pk.Nature < _strings.natures.Length
            ? _strings.natures[(int)pk.Nature] : "";

        // Name plate
        DetailSpeciesName.Text = speciesName + (pk.IsShiny ? "  ✦" : "");
        DetailLevelNature.Text = $"Lv.{pk.CurrentLevel}  ·  {natureName}";

        // Type badges
        UpdateTypeBadges(pk);

        // Moves
        UpdateMoveRows(pk);

        // Info panel fields
        DetailAbility.Text = pk.Ability >= 0 && pk.Ability < _strings.abilitylist.Length
            ? _strings.abilitylist[pk.Ability] : "—";
        DetailItem.Text = pk.HeldItem > 0 && pk.HeldItem < _strings.itemlist.Length
            ? _strings.itemlist[pk.HeldItem] : "None";
        DetailGender.Text = pk.Gender switch { 0 => "♂ Male", 1 => "♀ Female", _ => "—" };
        DetailBall.Text = pk.Ball > 0 && pk.Ball < _strings.balllist.Length
            ? _strings.balllist[pk.Ball] : "—";

        // Apply current toggle / landscape-cycle state
        if (_isLandscapePhone && TopScreenPanel.IsVisible)
            ApplyLandscapeDetailView();
        else
            ApplyDetailToggle();

        TopIdlePanel.IsVisible     = false;
        TopSelectedPanel.IsVisible = true;

        // Only reload WebView if the species/form/shiny changed (avoid flicker on cursor move)
        int key = (pk.Species << 10) | (pk.Form << 1) | (pk.IsShiny ? 1 : 0);
        if (key != _previewSpecies)
        {
            _previewSpecies = key;
            LoadAnimatedSprite(pk);
        }
        else if (_spriteHasCurrent && Preferences.Default.Get(SettingsPage.KeyAnimated3D, true))
        {
            SpriteWebView.IsVisible = true;
            PreviewCanvas.IsVisible = false;
        }

        PreviewCanvas.InvalidateSurface();
        StartRadarAnimation(GetRadarStats(pk));
    }

    private void ShowIdlePanel()
    {
        _previewPk = null;
        _detailToggled = false; // reset toggle when cursor leaves a Pokémon
        SpriteWebView.IsVisible    = false;
        _spriteHasCurrent          = false;
        PreviewCanvas.IsVisible    = true;
        TopIdlePanel.IsVisible     = true;
        TopSelectedPanel.IsVisible = false;
        if (_isLandscapePhone) LandscapeViewDots.IsVisible = false;
    }

    // ──────────────────────────────────────────────
    //  Phone detail bottom sheet
    // ──────────────────────────────────────────────

    private async Task ShowPhoneDetailSheetAsync(PKM pk)
    {
        _phoneSheetPk = pk;
        int seq = ++PhoneSheetSeq;      // 使任何在途的隐藏动画收尾失效

        // 顺序很关键：先定宽（占位高度依赖列宽）→ 填内容 → 排内容（列/行）
        // → 再量竖屏高度（必须在内容填完之后量，否则量到上一只的尺寸）
        ApplySheetSize();
        UpdatePhoneSheetContent(pk);
        ApplySheetContentLayout();
        ApplySheetHeight();
        PrefetchCry(pk);   // 后台预取叫声，避免第一次按 R 等网络

        PhoneSheetPanel.TranslationY = 0;   // 兜底：清掉历史位移残留
        PhoneDetailSheet.InputTransparent = false;
        PhoneSheetScrim.InputTransparent  = false;

        // 淡入 + 轻微放大（不再从底部上滑）
        PhoneSheetScrim.Opacity = 0;
        PhoneSheetPanel.Opacity = 0;
        PhoneSheetPanel.Scale   = 0.96;
        _phoneSheetVisible = true;          // 动画前就置位，淡入中按返回也能先关卡片

        var fadeIn  = PhoneSheetScrim.FadeToAsync(0.55, 200, Easing.CubicOut);
        var fadePnl = PhoneSheetPanel.FadeToAsync(1, 220, Easing.CubicOut);
        var scaleUp = PhoneSheetPanel.ScaleToAsync(1, 220, Easing.CubicOut);
        await Task.WhenAll(fadeIn, fadePnl, scaleUp);

        if (seq != PhoneSheetSeq) return;   // 期间被隐藏/切换过，收尾交给新的那次
    }

    private async Task HidePhoneDetailSheetAsync()
    {
        if (!_phoneSheetVisible) return;
        int seq = ++PhoneSheetSeq;
        _phoneSheetVisible = false;
        PhoneSheetScrim.InputTransparent = true;

        var fadeOut   = PhoneSheetScrim.FadeToAsync(0, 160, Easing.CubicIn);
        var fadePnl   = PhoneSheetPanel.FadeToAsync(0, 160, Easing.CubicIn);
        var scaleDown = PhoneSheetPanel.ScaleToAsync(0.96, 180, Easing.CubicIn);
        await Task.WhenAll(fadeOut, fadePnl, scaleDown);

        if (seq != PhoneSheetSeq) return;   // 期间又打开了新卡片，不要清掉它

        PhoneDetailSheet.InputTransparent = true;
        PhoneSheetPanel.TranslationY = 0;
        PhoneSheetPanel.Scale        = 1;
        _phoneSheetPk = null;
    }

    /// <summary>瞬时隐藏详情卡片（无动画），用于「编辑宝可梦」这类紧接着导航的场景。</summary>
    private void HidePhoneDetailSheetImmediate()
    {
        PhoneSheetSeq++;              // 让在途的淡入/淡出收尾全部失效
        _phoneSheetVisible = false;
        PhoneSheetPanel.Opacity = 0;
        PhoneSheetPanel.Scale   = 1;
        PhoneSheetPanel.TranslationY = 0;
        PhoneSheetScrim.Opacity = 0;
        PhoneDetailSheet.InputTransparent = true;
        PhoneSheetScrim.InputTransparent  = true;
        _phoneSheetPk = null;
    }

    private void UpdatePhoneSheetContent(PKM pk)
    {
        var speciesName = pk.Species < _strings.specieslist.Length
            ? _strings.specieslist[pk.Species] : pk.Species.ToString();
        var natureName = (int)pk.Nature < _strings.natures.Length
            ? _strings.natures[(int)pk.Nature] : "";

        PhoneSheetSpecies.Text    = speciesName + (pk.IsShiny ? "  ✦" : "");
        PhoneSheetLevelNature.Text = $"Lv.{pk.CurrentLevel}  ·  {natureName}";

        // Type badges — update pooled views in-place
        {
            int t1 = pk.PersonalInfo.Type1;
            int t2 = pk.PersonalInfo.Type2;
            bool dual = t2 != t1;
            ApplyTypeBadge(_phoneTypeBadges[0], t1, visible: true);
            ApplyTypeBadge(_phoneTypeBadges[1], t2, visible: dual);
        }

        // Moves
        var phoneNames = new[] { PhoneMoveName0, PhoneMoveName1, PhoneMoveName2, PhoneMoveName3 };
        var phonePPs   = new[] { PhoneMovePP0, PhoneMovePP1, PhoneMovePP2, PhoneMovePP3 };
        var phoneDots  = new[] { PhoneMoveDot0, PhoneMoveDot1, PhoneMoveDot2, PhoneMoveDot3 };
        var phoneRows  = new[] { PhoneMoveRow0, PhoneMoveRow1, PhoneMoveRow2, PhoneMoveRow3 };
        int[] moves = [pk.Move1, pk.Move2, pk.Move3, pk.Move4];
        int[] pps   = [pk.Move1_PP, pk.Move2_PP, pk.Move3_PP, pk.Move4_PP];
        var ctx = _sav?.Context ?? EntityContext.Gen9;
        for (int i = 0; i < 4; i++)
        {
            if (moves[i] == 0) { phoneRows[i].IsVisible = false; continue; }
            phoneRows[i].IsVisible = true;
            phoneNames[i].Text = moves[i] < _strings.movelist.Length
                ? _strings.movelist[moves[i]] : $"Move {moves[i]}";
            phonePPs[i].Text = $"PP {pps[i]}";
            var moveTypeName = MoveInfo.GetType((ushort)moves[i], ctx) is var mt && mt < _strings.types.Length
                ? _strings.types[mt] : "Normal";
            if (Theme.TypeColors.Map.TryGetValue(moveTypeName, out var tc))
                phoneDots[i].Fill = new SolidColorBrush(
                    Color.FromUint((uint)((tc.Alpha << 24) | (tc.Red << 16) | (tc.Green << 8) | tc.Blue)));
        }

        // 能力视图 —— 注意 Stat_* 是「队伍区战斗数值」，盒子里的宝可梦恒为 0；
        // 因此这里按 种族值 + IV + EV + 等级 + 性格 现场计算（GetStats 返回 [HP,ATK,DEF,SPE,SPA,SPD]）
        {
            ushort[] st = pk.PartyStatsPresent
                ? [ (ushort)pk.Stat_HPMax, (ushort)pk.Stat_ATK, (ushort)pk.Stat_DEF,
                    (ushort)pk.Stat_SPE, (ushort)pk.Stat_SPA, (ushort)pk.Stat_SPD ]
                : pk.GetStats(pk.PersonalInfo);
            PhoneStatHP.Text  = st[0].ToString();
            PhoneStatATK.Text = st[1].ToString();
            PhoneStatDEF.Text = st[2].ToString();
            PhoneStatSPE.Text = st[3].ToString();
            PhoneStatSPA.Text = st[4].ToString();
            PhoneStatSPD.Text = st[5].ToString();
        }

        // 详情视图
        PhoneInfoAbility.Text = pk.Ability < _strings.abilitylist.Length
            ? _strings.abilitylist[pk.Ability] : "—";
        PhoneInfoItem.Text = pk.HeldItem > 0 && pk.HeldItem < _strings.itemlist.Length
            ? _strings.itemlist[pk.HeldItem] : "无";
        PhoneInfoGender.Text = pk.Gender switch { 0 => "♂ 雄性", 1 => "♀ 雌性", _ => "无性别" };
        PhoneInfoBall.Text = pk.Ball < _strings.balllist.Length
            ? _strings.balllist[pk.Ball] : "—";

        PhoneSheetCanvas.InvalidateSurface();

        // 手机详情卡动图：拿不到就保持静态图，不阻塞内容刷新
        _ = LoadSheetAnimatedSpriteAsync(pk);
    }

    // ──────────────────────────────────────────────
    //  详情卡片尺寸与内容排布
    // ──────────────────────────────────────────────

    /// <summary>卡片宽度：宽 min(屏宽×0.92, 560)（横竖屏相同）。竖屏高度先复位，由 ApplySheetHeight 量出后写回。</summary>
    private void ApplySheetSize()
    {
        double w = Width  > 0 ? Width  : (Window?.Width  ?? 400);
        double h = Height > 0 ? Height : (Window?.Height ?? 700);
        PhoneSheetPanel.WidthRequest = Math.Min(w * 0.92, 560);
        _sheetMaxH = Math.Min(h * 0.94, 760);

        if (_isLandscapePhone)
        {
            // 横屏：保持 test6 的固定卡片高（用户已验收，勿改）
            PhoneSheetPanel.HeightRequest      = _sheetMaxH;
            PhoneSheetPanel.MaximumHeightRequest = double.PositiveInfinity;   // 必须复位：优先级高于 HeightRequest
            PhoneSheetPanel.MinimumHeightRequest = -1;
            _sheetCardH = _sheetMaxH;
        }
        else
        {
            // 竖屏：先解除一切高度约束，等内容填完再测；测量期间 Maximum 必须为 ∞，否则量出来就是上限
            PhoneSheetPanel.HeightRequest        = -1;
            PhoneSheetPanel.MaximumHeightRequest = double.PositiveInfinity;
            PhoneSheetPanel.MinimumHeightRequest = -1;
            _sheetCardH = 0;
        }
    }

    /// <summary>
    /// 竖屏按内容量高。卡片内部是 Auto,*,Auto 的 Grid，MAUI 在受约束测量时会把 * 行吃满剩余高度
    /// （所以直接让 HeightRequest=-1 会让卡片顶到 0.94 屏上限）。这里显式做一次无约束测量拿内容高度，
    /// clamp 到 [240, min(0.94屏,760)] 后写回 HeightRequest；横屏保持固定高，直接返回。
    /// </summary>
    private void ApplySheetHeight()
    {
        if (_isLandscapePhone) { _sheetCardH = PhoneSheetPanel.HeightRequest; return; }

        double maxH = _sheetMaxH > 0 ? _sheetMaxH : 700;
        double cardW = PhoneSheetPanel.WidthRequest > 0 ? PhoneSheetPanel.WidthRequest : 360;
        double desired = 0;
        try { desired = PhoneSheetPanel.Measure(cardW, double.PositiveInfinity).Height; }
        catch { /* 测量失败走兜底 */ }

        double target = desired > 0 ? Math.Clamp(desired, 240, maxH) : maxH;
        PhoneSheetPanel.HeightRequest = target;
        _sheetCardH = target;
    }

    /// <summary>
    /// 招式块按 4 招式占位：竖屏给招式网格设最小高度，使 1~4 招的卡片高度一致。
    /// 用一行实测行高（空文本时临时填占位字）推 2×2 高度，与招式数和名字长短无关；失败兜底 72dp。
    /// </summary>
    private void ApplyMovesReserveHeight()
    {
        if (_isLandscapePhone)
        {
            PhoneSheetMovesGrid.MinimumHeightRequest = 0;
            return;
        }

        var row   = PhoneMoveRow0;
        var name  = PhoneMoveName0;
        var pp    = PhoneMovePP0;
        bool  savedVis  = row.IsVisible;
        string savedName = name.Text;
        string savedPp   = pp.Text;

        double rowH = -1;
        try
        {
            row.IsVisible = true;
            if (string.IsNullOrEmpty(name.Text)) name.Text = "招式";
            if (string.IsNullOrEmpty(pp.Text))   pp.Text   = "PP 99";

            double colW = ((PhoneSheetPanel.WidthRequest > 0 ? PhoneSheetPanel.WidthRequest : 360) - 32 - 8) / 2;
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

        PhoneSheetMovesGrid.MinimumHeightRequest = rowH > 20 ? (rowH * 2 + 6) : 72;
    }

    /// <summary>
    /// 横屏且内容区够高时排三列（招式 | 能力 | 详情），否则单列串联（ScrollView 兜底）。
    /// 内容区高 ≈ 卡片高 - Header(104) - 底栏(44) - 内边距与行距(约 48)。
    /// </summary>
    private void ApplySheetContentLayout()
    {
        double cardH = _sheetCardH > 0 ? _sheetCardH
                     : (PhoneSheetPanel.HeightRequest > 0 ? PhoneSheetPanel.HeightRequest : 400);
        double cardW = PhoneSheetPanel.WidthRequest  > 0 ? PhoneSheetPanel.WidthRequest  : 360;
        double contentH = cardH - 196;
        bool threeCol = _isLandscapePhone && contentH >= 160 && cardW >= 500;

        PhoneSheetContent.ColumnDefinitions.Clear();
        PhoneSheetContent.RowDefinitions.Clear();

        View[] views = [PhoneSheetMovesView, PhoneSheetStatsView, PhoneSheetInfoView];
        for (int i = 0; i < views.Length; i++)
            views[i].IsVisible = true;

        if (threeCol)
        {
            for (int i = 0; i < views.Length; i++)
                PhoneSheetContent.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            PhoneSheetContent.RowDefinitions.Add(new RowDefinition(GridLength.Star));

            for (int i = 0; i < views.Length; i++)
            {
                Grid.SetColumn(views[i], i);
                Grid.SetRow(views[i], 0);
                views[i].VerticalOptions = LayoutOptions.Fill;
            }
        }
        else
        {
            PhoneSheetContent.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (int i = 0; i < views.Length; i++)
                PhoneSheetContent.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            for (int i = 0; i < views.Length; i++)
            {
                Grid.SetColumn(views[i], 0);
                Grid.SetRow(views[i], i);
                views[i].VerticalOptions = LayoutOptions.Start;
            }
        }

        ApplyMovesReserveHeight();
        _ = PhoneSheetScroll.ScrollToAsync(0, 0, false);
    }

    private void OnPhoneSheetSpritePaint(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_phoneSheetPk is null) return;
        var sprite = _sprites.GetSprite(_phoneSheetPk);
        float aspect = sprite.Width > 0 ? (float)sprite.Width / sprite.Height : 1f;
        float w = e.Info.Width, h = e.Info.Height;
        float drawW, drawH;
        if (w / h <= aspect) { drawW = w; drawH = w / aspect; }
        else                 { drawH = h; drawW = h * aspect; }
        canvas.DrawBitmap(sprite, SKRect.Create((w - drawW) / 2f, (h - drawH) / 2f, drawW, drawH));
    }

    private void OnPhoneSheetScrimTapped(object? sender, TappedEventArgs e) => DeselectSlot();

    private void OnPhoneSheetEditTapped(object? sender, TappedEventArgs e)
    {
        // 不走淡出：160ms 动画会和紧接着的页面推入抢同一个 UI 线程，观感就是"点了半秒没反应"。
        // 瞬时隐藏卡片 + 清选中态，但**不能**动 _cursorSlot —— OpenEditor 靠它定位格子。
        _selectedSlot = -1;
        BoxCanvas.InvalidateSurface();
        HidePhoneDetailSheetImmediate();
        _ = OpenEditor();
    }

    // ──────────────────────────────────────────────
    //  X toggle: info+radar  ↔  moves+compat
    // ──────────────────────────────────────────────

    private void ToggleDetailView()
    {
        if (_previewPk is null) return;
        _detailToggled = !_detailToggled;
        ApplyDetailToggle();
        if (_detailToggled)
            UpdateCompatPanel(_previewPk);
    }

    private void ApplyDetailToggle()
    {
        // Left column only — moves are always shown in right column
        RadarBorder.IsVisible = !_detailToggled;
        CompatPanel.IsVisible =  _detailToggled;
    }

    /// <summary>
    /// Expand one of the three TopSelectedPanel columns to fill the full left panel
    /// and collapse the other two. Only active in landscape-phone mode.
    /// </summary>
    private void ApplyLandscapeDetailView()
    {
        var cols = TopSelectedPanel.ColumnDefinitions;
        bool isSprite = _landscapeDetailView == 0;
        TypeBadgeRow.IsVisible    = isSprite;
        DetailNameBlock.IsVisible = isSprite;

        switch (_landscapeDetailView)
        {
            case 0: // Sprite — restore whichever canvas was active
                cols[0].Width = new GridLength(0);
                cols[1].Width = GridLength.Star;
                cols[2].Width = new GridLength(0);
                bool use3D = _spriteHasCurrent && Preferences.Default.Get(SettingsPage.KeyAnimated3D, true);
                SpriteWebView.IsVisible = use3D;
                PreviewCanvas.IsVisible = !use3D;
                if (!use3D) PreviewCanvas.InvalidateSurface();
                break;
            case 1: // Moves — hide sprite so it doesn't bleed through
                cols[0].Width = new GridLength(0);
                cols[1].Width = new GridLength(0);
                cols[2].Width = GridLength.Star;
                SpriteWebView.IsVisible = false;
                PreviewCanvas.IsVisible = false;
                break;
            case 2: // Stats — hide sprite, always show radar not compat panel
                cols[0].Width = GridLength.Star;
                cols[1].Width = new GridLength(0);
                cols[2].Width = new GridLength(0);
                SpriteWebView.IsVisible = false;
                PreviewCanvas.IsVisible = false;
                _detailToggled = false;
                ApplyDetailToggle();
                RadarCanvas.InvalidateSurface();
                break;
        }

        // Column width changes don't always force an immediate layout pass on Android.
        // Calling requestLayout() on the native view guarantees the next frame redraws correctly.
#if ANDROID
        (TopSelectedPanel.Handler?.PlatformView as Android.Views.View)?.RequestLayout();
#endif

        LandscapeViewDots.IsVisible = true;
        ViewDot0.Opacity = _landscapeDetailView == 0 ? 1.0 : 0.35;
        ViewDot1.Opacity = _landscapeDetailView == 1 ? 1.0 : 0.35;
        ViewDot2.Opacity = _landscapeDetailView == 2 ? 1.0 : 0.35;
    }

    /// <summary>Restore the original 3-column proportions (used when leaving landscape-phone mode).</summary>
    private void RestoreLandscapeColumns()
    {
        var cols = TopSelectedPanel.ColumnDefinitions;
        cols[0].Width = new GridLength(3.5, GridUnitType.Star);
        cols[1].Width = new GridLength(4.5, GridUnitType.Star);
        cols[2].Width = new GridLength(3.0, GridUnitType.Star);
        LandscapeViewDots.IsVisible = false;
    }

    private void UpdateCompatPanel(PKM pk)
    {
        // Clear existing rows (keep the header label at index 0)
        while (CompatList.Children.Count > 1)
            CompatList.Children.RemoveAt(1);

        var saves = _session.LoadedSaves;
        if (saves.Count == 0)
        {
            CompatList.Children.Add(new Label
            {
                Text = "没有其他已加载的存档",
                FontFamily = "Nunito",
                FontSize = 10,
                TextColor = Color.FromArgb("#88AABBCC"),
            });
            return;
        }

        foreach (var save in saves)
        {
            var status = GetCompatStatus(pk, save);
            var dotColor = status switch
            {
                CompatStatus.Green  => Color.FromArgb("#4ADE80"),
                CompatStatus.Yellow => Color.FromArgb("#FACC15"),
                _                   => Color.FromArgb("#F87171"),
            };
            var statusText = status switch
            {
                CompatStatus.Green  => "直接",
                CompatStatus.Yellow => "转移",
                _                   => "不兼容",
            };

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection(
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)),
                ColumnSpacing = 6,
            };

            row.Add(new Ellipse
            {
                Fill = new SolidColorBrush(dotColor),
                WidthRequest = 8, HeightRequest = 8,
                VerticalOptions = LayoutOptions.Center,
            }, 0, 0);

            var nameStack = new VerticalStackLayout { Spacing = 0 };
            nameStack.Add(new Label
            {
                Text = save.FileName,
                FontFamily = "NunitoBold",
                FontSize = 11,
                TextColor = Color.FromArgb("#EDF0FF"),
                LineBreakMode = LineBreakMode.TailTruncation,
            });
            nameStack.Add(new Label
            {
                Text = $"{save.Version}  ·  {save.TrainerName}",
                FontFamily = "Nunito",
                FontSize = 9,
                TextColor = Color.FromArgb("#7080A0"),
            });
            row.Add(nameStack, 1, 0);

            row.Add(new Label
            {
                Text = statusText,
                FontFamily = "Nunito",
                FontSize = 9,
                TextColor = dotColor,
                VerticalOptions = LayoutOptions.Center,
            }, 2, 0);

            var rowBorder = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                BackgroundColor = Color.FromArgb("#0D1A33"),
                Stroke = Color.FromArgb("#1A2A44"),
                StrokeThickness = 1,
                Padding = new Thickness(8, 6),
                Content = row,
            };
            CompatList.Children.Add(rowBorder);
        }
    }

    private enum CompatStatus { Green, Yellow, Red }

    private static CompatStatus GetCompatStatus(PKM pk, SaveEntry save)
    {
        // Same format/generation: directly placeable
        if (save.Generation == pk.Format)
            return CompatStatus.Green;

        // Different generation: check forward-transfer path
        if (EntityConverter.IsConvertibleToFormat(pk, (byte)save.Generation))
            return CompatStatus.Yellow;

        return CompatStatus.Red;
    }

    private async Task RunLegalityBadgesAsync(PKM[] snapshot)
    {
        var results = await Task.Run(() =>
            snapshot.Select(pk => pk.Species == 0 ? (bool?)null
                                                   : (bool?)new PKHeX.Core.LegalityAnalysis(pk).Valid)
                    .ToArray());
        _legalityCache = results;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            BoxCanvas.InvalidateSurface();
            // Push the filled cache to the secondary screen — it only received the
            // empty array at LoadBox time and has no other way to learn it was filled.
            _secondary.UpdateBoxGrid(
                _currentBox, _cursorSlot, _selectedSlot,
                _moveMode, _movePk, _moveSourceBox, _moveSourceSlot,
                _boxIndex,
                _boxIndex == -1 ? "队伍"
                    : (_boxIndex < (_sav?.BoxCount ?? 0) && _sav is IBoxDetailName n
                        ? n.GetBoxName(_boxIndex) : $"Box {_boxIndex + 1}"),
                _legalityCache, _showLegalityBadges);
        });
    }

    /// <summary>Mark slot with selected outline (does not change the top panel display).</summary>
    private void SelectSlot(int slot)
    {
        if (_currentBox[slot].Species == 0) return;
        _selectedSlot = slot;
        BoxCanvas.InvalidateSurface();
        if (_isPhone) _ = ShowPhoneDetailSheetAsync(_currentBox[slot]);
    }

    /// <summary>Clear selected outline.</summary>
    private void DeselectSlot()
    {
        _selectedSlot = -1;
        BoxCanvas.InvalidateSurface();
        if (_isPhone && _phoneSheetVisible) _ = HidePhoneDetailSheetAsync();
    }

    // ──────────────────────────────────────────────
    //  Navigation
    // ──────────────────────────────────────────────

    private void OnEditClicked(object sender, EventArgs e)     => _ = OpenEditor();
    private void OnDeselectClicked(object sender, EventArgs e) => DeselectSlot();

    private async Task OpenEditor()
    {
        // Always edit the slot the cursor is currently on
        if (_cursorSlot < 0 || _cursorSlot >= _currentBox.Length) return;
        if (_currentBox[_cursorSlot].Species == 0) return;
        if (_boxIndex == -1)
            await Shell.Current.GoToAsync($"{nameof(PkmEditorPage)}?party=1&slot={_cursorSlot}");
        else
            await Shell.Current.GoToAsync($"{nameof(PkmEditorPage)}?box={_boxIndex}&slot={_cursorSlot}");
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        => _ = UpdateSearchAsync(e.NewTextValue ?? "");

    private async Task UpdateSearchAsync(string query)
    {
        if (_sav is null) return;
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            _searchMode = false;
            _searchResults = [];
            _cursorSlot = 0;
            BoxCanvas.InvalidateSurface();
            return;
        }
        _searchMode = true;
        var results = new List<SearchSlot>();
        var sav = _sav;
        await Task.Run(() =>
        {
            for (int b = 0; b < sav.BoxCount; b++)
            {
                var box = sav.GetBoxData(b);
                for (int s = 0; s < box.Length; s++)
                {
                    var pk = box[s];
                    if (pk.Species == 0 || pk.Species >= _strings.specieslist.Length) continue;
                    if (_strings.specieslist[pk.Species].Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                        results.Add(new SearchSlot(b, s, pk));
                }
            }
        });
        _searchResults = [.. results];
        if (_cursorSlot >= _searchResults.Length) _cursorSlot = 0;
        UpdateTopPanel();
        UpdateInfoBar();
        BoxCanvas.InvalidateSurface();
        await _sprites.PreloadBoxAsync(_searchResults.Select(r => r.Pk).ToArray());
        BoxCanvas.InvalidateSurface();
    }

    private void OnActionOverlayTapped(object sender, TappedEventArgs e) => CloseActionMenu();
    private void OnSaveHintTapped(object sender, TappedEventArgs e)     => OpenActionMenu();

    private async void OnMenuClicked(object sender, EventArgs e)
        => await Shell.Current.GoToAsync("..");

    private async void OnSearchClicked(object sender, EventArgs e)
        => await Shell.Current.GoToAsync(nameof(DatabasePage));

    private async void OnGiftsClicked(object sender, EventArgs e)
        => await Shell.Current.GoToAsync(nameof(MysteryGiftDBPage));

    private async void OnSettingsClicked(object sender, EventArgs e)
        => await Shell.Current.GoToAsync(nameof(SettingsPage));

    /// <summary>
    /// 带冲突保护的整份写回：磁盘存档若已被游戏更新，先让用户选择，绝不在用户不知情时覆盖游戏进度。
    /// 返回 true 表示确实写入了文件。
    /// </summary>
    private async Task<bool> WriteSaveGuardedAsync(bool announce)
    {
        if (_sav is null) return false;
        if (string.IsNullOrEmpty(_session.ActiveSaveFileUri))
        {
            await DisplayAlertAsync("保存失败", "找不到原文件位置，请改用「导出存档」。", "OK");
            return false;
        }
        try
        {
            // 写回前先判断磁盘存档是否比内存新（玩家可能刚在游戏里存过档）
            if (await SaveSyncService.IsStaleAsync(_session))
            {
                var choice = await DisplayActionSheetAsync(
                    "存档已在游戏内更新",
                    "取消",
                    "强制覆盖（会丢失游戏进度）",
                    "重新加载最新存档");

                if (choice == "重新加载最新存档")
                {
                    if (await SaveSyncService.ReloadFromDiskAsync(_session))
                    {
                        _sav = _session.ActiveSave!;
                        LoadBox(_boxIndex);
                        UpdateTopPanel();
                        await DisplayAlertAsync("已重新加载", "已读取磁盘上的最新存档，软件内未保存的改动已放弃。", "OK");
                    }
                    else
                    {
                        await DisplayAlertAsync("重新加载失败", "磁盘上的存档无法解析，未做任何写入。", "OK");
                    }
                    return false;
                }

                if (choice != "强制覆盖（会丢失游戏进度）")
                    return false;
            }

            await SaveSyncService.ForceWriteAsync(_session);
            if (announce)
                await DisplayAlertAsync("已保存", $"已写回 {_session.ActiveSaveFileName}。", "OK");
            else
                Toast("已写回存档文件");
            return true;
        }
        catch (Exception ex)
        {
            // 权限问题正常情况下由 FileService 给出可直接照做的中文原因；
            // 只有探测通过、真正写入时才被系统拒绝的兜底情况，才需要替换掉系统英文原文。
            string msg = ex.Message;
#if ANDROID
            if (ex is Java.Lang.SecurityException)
                msg = "系统拒绝了这次写入。请到「文件夹管理」重新添加这张存档（或改用「添加文件夹」），"
                    + "并确认首页顶部「所有文件访问」已授权。\n\n" + ex.Message;
#endif
            await DisplayAlertAsync("保存失败", msg, "OK");
            return false;
        }
    }

    /// <summary>短暂的轻提示，不打断操作。</summary>
    private void Toast(string text)
    {
#if ANDROID
        Android.Widget.Toast.MakeText(Android.App.Application.Context, text,
            Android.Widget.ToastLength.Short)?.Show();
#endif
    }

    /// <summary>进入箱子页时检查磁盘存档是否已被游戏更新，提示一次重新加载。</summary>
    private async Task CheckStaleOnEnterAsync()
    {
        if (_sav is null) return;
        if (string.IsNullOrEmpty(_session.ActiveSaveFingerprint)) return;

        var disk = await SaveSyncService.ReadDiskBytesAsync(_session);
        if (disk is null) return;

        var fp = SaveSyncService.Fingerprint(disk);
        if (fp == _session.ActiveSaveFingerprint) return;
        if (_session.PromptedStaleFingerprint == fp) return;
        _session.PromptedStaleFingerprint = fp;

        bool reload = await DisplayAlertAsync("存档已被更新",
            "磁盘上的存档比软件里加载的更新（可能是你在游戏里保存过）。\n\n是否重新加载最新进度？\n\n"
            + "先不重新加载也可以继续操作：之后的存入 / 取出只会把「这一格」的改动合并到最新存档上，不会覆盖你在游戏里的进度。",
            "重新加载", "稍后");

        if (!reload) return;

        if (await SaveSyncService.ReloadFromDiskAsync(_session))
        {
            _sav = _session.ActiveSave!;
            LoadBox(_boxIndex);
            UpdateTopPanel();
        }
        else
        {
            await DisplayAlertAsync("重新加载失败", "磁盘上的存档无法解析，仍使用软件内已加载的版本。", "OK");
        }
    }

    private async void OnExportClicked(object sender, EventArgs e)
    {
        if (_sav is null) return;
        try
        {
            var data = _sav.Write().ToArray();
            await new FileService().ExportFileAsync(data, _session.ActiveSaveFileName);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("导出失败", ex.Message, "OK");
        }
    }
}
