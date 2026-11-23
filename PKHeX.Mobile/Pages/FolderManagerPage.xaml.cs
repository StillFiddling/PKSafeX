using PKHeX.Mobile.Services;

namespace PKHeX.Mobile.Pages;

public partial class FolderManagerPage : ContentPage
{
    private readonly SaveDirectoryService _dirService = new();
    private List<WatchedEntry> _items = [];
    private int _listCursor = -1;

#if ANDROID
    private readonly IDirectoryPicker _dirPicker = new Platforms.Android.AndroidDirectoryPicker();
    private readonly ISaveFilePicker  _filePicker = new Platforms.Android.AndroidFilePicker();
#else
    private readonly IDirectoryPicker _dirPicker  = new NullDirectoryPicker();
    private readonly ISaveFilePicker  _filePicker = new NullFilePicker();
#endif

    public FolderManagerPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        GamepadRouter.KeyReceived += OnGamepadKey;
#endif
        UpdatePermissionBanner();
        RefreshList();
    }

    /// <summary>没授权时显示提示条；刚授权回来则自动重扫一次。</summary>
    private void UpdatePermissionBanner()
    {
        bool granted = AllFilesAccess.IsGranted();
        PermissionBanner.IsVisible = !granted;

        if (granted && _wasUngranted)
        {
            _wasUngranted = false;
            SessionState.PendingFullRescan = true;  // 授权回来，回首页重扫
        }
        if (!granted) _wasUngranted = true;
    }

    private bool _wasUngranted;

    private void OnGrantClicked(object sender, EventArgs e)
    {
        AllFilesAccess.MarkAsked();
        AllFilesAccess.OpenSettings();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
#if ANDROID
        GamepadRouter.KeyReceived -= OnGamepadKey;
#endif
    }

    private void RefreshList()
    {
        _items = [
            .. _dirService.GetWatchedDirectories().Select(u => new WatchedEntry(u, false)),
            .. _dirService.GetWatchedFiles().Select(u => new WatchedEntry(u, true)),
        ];
        DirList.ItemsSource = null;
        DirList.ItemsSource = _items;
        _listCursor = _items.Count > 0 ? 0 : -1;
    }

    private async void OnAddFolderClicked(object sender, EventArgs e)
    {
        var uri = await _dirPicker.PickDirectoryAsync();
        if (uri is null) return;

        await EnsureReadableFolderAsync(uri);

        _dirService.AddDirectory(uri);
        SessionState.RequestAdd(uri, false);   // 只增量扫这个文件夹
        RefreshList();
    }

    /// <summary>
    /// 加文件夹前就确认权限：没开「所有文件访问」时，模拟器（AzaharPlus/Citra/melonDS 等）
    /// 的存档基本都在 Android/data 或受限目录里，SAF 读了也是 0 个。
    /// </summary>
    private async Task<bool> EnsureReadableFolderAsync(string uri)
    {
        if (AllFilesAccess.IsGranted()) return true;

        var decoded = System.Uri.UnescapeDataString(uri);
        bool inAndroidData = decoded.Contains("Android/data", StringComparison.OrdinalIgnoreCase);

        bool go = await DisplayAlertAsync(
            "需要「所有文件访问」权限",
            (inAndroidData
                ? "这个文件夹在 Android/data 里，系统文件选择器读不到里面的存档。\n\n"
                : "模拟器的存档大多在 Android/data 或受限目录里，系统选择器读不到。\n\n")
            + "要让软件读到（例如 AzaharPlus 的 3DS 存档），需要授予「所有文件访问」权限。\n"
            + "开启后回到这里，列表会自动重扫。",
            "去授权", "以后再说");

        if (go)
        {
            AllFilesAccess.MarkAsked();
            AllFilesAccess.OpenSettings();
            return false;
        }
        return true;   // 用户选择先不授权：仍然把文件夹加进去，但会读不到
    }

    private async void OnAddFileClicked(object sender, EventArgs e)
    {
        var uri = await _filePicker.PickFileAsync();
        if (uri is null) return;

        // 先验证能不能识别成存档，避免加进去首页还是空的
        var probe = await _dirService.ScanFileAsync(uri);
        if (probe is null)
        {
            await DisplayAlertAsync("无法识别",
                "这个文件不是可识别的宝可梦存档，请确认选的是存档本身（3DS 的 main / NDS 的 .dsv / GBA 的 .sav）。",
                "好");
            return;
        }

        _dirService.AddFile(uri);
        SessionState.RequestAdd(uri, true);    // 只读这一个，不动其它卡片
        RefreshList();
        await DisplayAlertAsync("已新增存档",
            $"第 {probe.Generation} 世代 · {probe.Version}\n训练家：{probe.TrainerName}",
            "好");
    }

    private async void OnRemoveClicked(object sender, EventArgs e)
    {
        WatchedEntry? entry = null;
        if (sender is Button btn)
            entry = btn.CommandParameter as WatchedEntry;
        else if (sender is TapGestureRecognizer tap)
            entry = tap.CommandParameter as WatchedEntry;

        if (entry is null) return;

        bool ok = await DisplayAlertAsync(
            "移出列表",
            $"{DescribeUri(entry.Uri)}\n\n只从软件的存档列表里移除这个路径，"
            + "不会删除手机上的存档文件。确定移出吗？",
            "移出", "取消");
        if (!ok) return;

        try
        {
            // 三份列表都清，历史版本可能把同一路径存在不同分类里
            _dirService.RemoveFile(entry.Uri);
            _dirService.RemoveDirectory(entry.Uri);
            _dirService.RemoveEdenRoot(entry.Uri);

            // 写后回读校验，避免"点了没反应"却看不出原因
            bool stillThere = _dirService.GetWatchedFiles().Contains(entry.Uri)
                           || _dirService.GetWatchedDirectories().Contains(entry.Uri)
                           || _dirService.GetEdenRoots().Contains(entry.Uri);

            if (stillThere)
            {
                CrashLog.Write("FolderManagerPage.OnRemoveClicked",
                    new Exception($"移出后仍存在：{entry.Uri}"));
                await DisplayAlertAsync("移出失败",
                    "这个路径没能从列表里去掉，已记录日志。请重试一次；仍然失败的话把 crash.log 发我。", "好");
                return;
            }

            SessionState.PendingFullRescan = true;  // 回首页时重扫，保证列表同步
            RefreshList();
        }
        catch (Exception ex)
        {
            CrashLog.Write("FolderManagerPage.OnRemoveClicked", ex);
            await DisplayAlertAsync("移出失败", ex.Message, "好");
        }
    }

    /// <summary>把 URI 拆成人能看懂的一段（末尾两级路径）。</summary>
    private static string DescribeUri(string uri)
    {
        var decoded = System.Uri.UnescapeDataString(uri);
        var parts = decoded.Split(':', '/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return decoded;
        var tail = string.Join('/', parts.Skip(Math.Max(0, parts.Length - 2)));
        return tail;
    }

    private async void OnBackClicked(object sender, EventArgs e)
        => await Shell.Current.GoToAsync("..");

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
                _ = Shell.Current.GoToAsync("..");
                break;
            case Android.Views.Keycode.DpadUp:
                MoveCursor(-1); break;
            case Android.Views.Keycode.DpadDown:
                MoveCursor(+1); break;
            case Android.Views.Keycode.ButtonX:
                RemoveFocused(); break;
            case Android.Views.Keycode.ButtonL1:
            case Android.Views.Keycode.Button5:
                OnAddFolderClicked(this, EventArgs.Empty); break;
            case Android.Views.Keycode.ButtonR1:
            case Android.Views.Keycode.Button6:
                OnAddFileClicked(this, EventArgs.Empty); break;
        }
    }
#endif

    private void MoveCursor(int delta)
    {
        if (_items.Count == 0) return;
        _listCursor = Math.Clamp(_listCursor + delta, 0, _items.Count - 1);
        DirList.SelectedItem = _items[_listCursor];
        DirList.ScrollTo(_listCursor, -1, ScrollToPosition.MakeVisible, false);
    }

    private void RemoveFocused()
    {
        if (_listCursor < 0 || _listCursor >= _items.Count) return;
        // 走同一条「移出列表」确认流程
        var tap = new TapGestureRecognizer { CommandParameter = _items[_listCursor] };
        OnRemoveClicked(tap, EventArgs.Empty);
    }

    // ── View model ────────────────────────────────────────────────────────

    public sealed record WatchedEntry(string Uri, bool IsFile)
    {
        public string Icon  => IsFile ? Theme.Ph.FloppyDisk : Theme.Ph.Folder;
        public string Label => ExtractLabel(Uri);

        private static string ExtractLabel(string uri)
        {
            var decoded = System.Uri.UnescapeDataString(uri);
            var idx = Math.Max(decoded.LastIndexOf(':'), decoded.LastIndexOf('/'));
            return idx >= 0 && idx < decoded.Length - 1 ? decoded[(idx + 1)..] : decoded;
        }
    }

    // ── Stubs for non-Android ─────────────────────────────────────────────

    private sealed class NullDirectoryPicker : IDirectoryPicker
    {
        public Task<string?> PickDirectoryAsync() => Task.FromResult<string?>(null);
    }

    private sealed class NullFilePicker : ISaveFilePicker
    {
        public Task<string?> PickFileAsync() => Task.FromResult<string?>(null);
    }
}
