using PKHeX.Mobile.Services;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using static PKHeX.Mobile.Services.ThemeService;

namespace PKHeX.Mobile.Pages;

/// <summary>
/// 立绘资源二级页：HOME 立绘 / 动态立绘共用一套界面，通过路由参数 kind 区分。
/// 下载与 ZIP 导入都收在这一页，设置页只保留入口。
/// </summary>
[QueryProperty(nameof(Kind), "kind")]
public partial class SpriteSourcePage : ContentPage
{
    private const int MaxLogLines = 200;

    private readonly System.Text.StringBuilder _log = new();
    private int _logLines;
    private bool _importing;
    private bool _isAnim;

    /// <summary>home / anim</summary>
    public string Kind { get; set; } = "home";

    public SpriteSourcePage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _isAnim = string.Equals(Kind, "anim", StringComparison.OrdinalIgnoreCase);

        PageTitleLabel.Text          = _isAnim ? "动态立绘" : "HOME 立绘";
        DownloadTitleLabel.Text      = _isAnim ? "下载动态立绘" : "下载 HOME 立绘";
        ImportTitleLabel.Text        = _isAnim ? "导入动态立绘精灵包（ZIP）" : "导入 HOME 立绘精灵包（ZIP）";
        ImportStatusLabel.Text       = _isAnim
            ? "选择含 ani / ani-shiny 目录的 GIF 压缩包；导入后动态立绘无需再联网。"
            : "选择自己下载好的 ZIP 压缩包，解压后按规则写入本地缓存；可重复导入，已存在的图会跳过。";

#if ANDROID
        GamepadRouter.KeyReceived -= OnGamepadKey;
        GamepadRouter.KeyReceived += OnGamepadKey;
#endif
        if (_isAnim)
        {
            SpriteCacheService.BulkProgressChanged += OnProgress;
            SpriteCacheService.SlugCompleted       += OnSlugCompleted;
        }
        else
        {
            HomeSpriteCacheService.BulkProgressChanged += OnProgress;
            HomeSpriteCacheService.SlugCompleted       += OnSlugCompleted;
        }

        UpdateStatus();
        UpdateDetail();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
#if ANDROID
        GamepadRouter.KeyReceived -= OnGamepadKey;
#endif
        if (_isAnim)
        {
            SpriteCacheService.BulkProgressChanged -= OnProgress;
            SpriteCacheService.SlugCompleted       -= OnSlugCompleted;
        }
        else
        {
            HomeSpriteCacheService.BulkProgressChanged -= OnProgress;
            HomeSpriteCacheService.SlugCompleted       -= OnSlugCompleted;
        }
    }

    // ── 下载 ─────────────────────────────────────────────────────────────────

    private void OnDownloadTapped(object? sender, EventArgs e) => ToggleDownload();

    private void ToggleDownload()
    {
        if (_isAnim)
        {
            if (SpriteCacheService.IsBulkDownloading)
            {
                SpriteCacheService.StopBulk();
                return;
            }
            _log.Clear();
            _logLines = 0;
            LogLabel.Text = "";
            LogPanel.IsVisible = false;
            _ = SpriteCacheService.BulkDownloadAllAsync();
        }
        else
        {
            if (HomeSpriteCacheService.IsBulkDownloading)
            {
                HomeSpriteCacheService.StopBulk();
                return;
            }
            _log.Clear();
            _logLines = 0;
            LogLabel.Text = "";
            LogPanel.IsVisible = false;
            _ = HomeSpriteCacheService.BulkDownloadAsync();
        }
        UpdateStatus();
    }

    private void OnProgress(int done, int total)
    {
        RingCanvas.InvalidateSurface();
        UpdateStatus();
    }

    private void OnSlugCompleted(string slug, bool success)
        => AppendLog(slug, success);

    private void UpdateStatus()
    {
        var (done, total, failed) = _isAnim ? SpriteCacheService.BulkProgress : HomeSpriteCacheService.BulkProgress;
        bool running = _isAnim ? SpriteCacheService.IsBulkDownloading : HomeSpriteCacheService.IsBulkDownloading;

        if (running)
        {
            StatusLabel.Text = $"正在下载…  {done} / {total}（再次点按可停止）";
            RingLabel.Text   = $"{done}";
        }
        else if (total > 0)
        {
            string failNote = failed > 0 ? $"  ·  失败 {failed} 个" : "";
            StatusLabel.Text = $"已缓存 {done} 张{failNote}";
            RingLabel.Text   = "✓";
        }
        else
        {
            StatusLabel.Text = _isAnim
                ? "约 200 MB · 缓存全部宝可梦的动态立绘 GIF"
                : "约 120 MB · 缓存全部宝可梦的 HOME 立绘";
            RingLabel.Text = "";
        }
    }

    private void UpdateDetail()
    {
        int cached  = _isAnim ? SpriteCacheService.CountCached() : HomeSpriteCacheService.CountCached();
        double mb   = _isAnim ? SpriteCacheService.CacheSizeMb() : HomeSpriteCacheService.CacheSizeMb();
        DetailLabel.Text = cached > 0
            ? $"当前已缓存 {cached} 张 · 占用 {mb:F1} MB"
            : "当前还没有缓存任何立绘";
    }

    private void OnRingPaint(object sender, SKPaintSurfaceEventArgs e)
    {
        var progress = _isAnim ? SpriteCacheService.BulkProgress : HomeSpriteCacheService.BulkProgress;
        bool running = _isAnim ? SpriteCacheService.IsBulkDownloading : HomeSpriteCacheService.IsBulkDownloading;
        PaintProgressRing(e, progress, running);
    }

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

    // ── 导入 ZIP ─────────────────────────────────────────────────────────────

    private static readonly FilePickerFileType ZipFileType = new(
        new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.Android]      = ["application/zip", "application/x-zip-compressed", "application/octet-stream"],
            [DevicePlatform.WinUI]        = [".zip"],
            [DevicePlatform.MacCatalyst]  = ["public.zip-archive"],
            [DevicePlatform.iOS]          = ["public.zip-archive"],
        });

    private async void OnImportTapped(object? sender, EventArgs e)
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
            ImportStatusLabel.Text = "打开文件选择器失败：" + ex.Message;
            return;
        }

        if (picked is null) return;

        _importing = true;
        try
        {
            ImportStatusLabel.Text = "正在导入…（请保持应用在前台）";

            await using var stream = await picked.OpenReadAsync();
            var progress = new Progress<(int Done, int Total)>(p =>
                ImportStatusLabel.Text = $"正在导入… {p.Done} / {p.Total}");

            var result = await SpritePackInstaller.ImportAsync(stream, progress);

            UpdateDetail();
            ImportStatusLabel.Text = $"{result.Summary()}｜HOME {HomeSpriteCacheService.CacheSizeMb():F1} MB，动态 {SpriteCacheService.CacheSizeMb():F1} MB";

            if (result.Errors.Count > 0)
            {
                string detail = string.Join("\n", result.Errors.Take(8));
                await DisplayAlertAsync("导入完成（有部分失败）",
                    $"{result.Summary()}\n\n{detail}", "OK");
            }
        }
        catch (Exception ex)
        {
            ImportStatusLabel.Text = "导入失败：" + ex.Message;
        }
        finally
        {
            _importing = false;
        }
    }

    // ── 日志 ─────────────────────────────────────────────────────────────────

    private void AppendLog(string slug, bool success)
    {
        if (success) return;

        LogPanel.IsVisible = true;

        if (_logLines >= MaxLogLines)
        {
            var text  = _log.ToString();
            int cut   = 0;
            int found = 0;
            int dropCount = MaxLogLines / 4;
            while (found < dropCount && cut < text.Length)
            {
                if (text[cut++] == '\n') found++;
            }
            _log.Clear();
            _log.Append(text.AsSpan(cut));
            _logLines -= found;
        }

        _log.Append("✗ ");
        _log.AppendLine(slug);
        _logLines++;

        LogLabel.Text = _log.ToString();
        _ = LogScroll.ScrollToAsync(0, double.MaxValue, false);
    }

    // ── 导航 ─────────────────────────────────────────────────────────────────

    private async void OnBackClicked(object sender, EventArgs e)
    {
        try { await Shell.Current.GoToAsync(".."); }
        catch (Exception ex) { CrashLog.Write("SpriteSourcePage.OnBackClicked", ex); }
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
                OnBackClicked(this, EventArgs.Empty); break;
            case Android.Views.Keycode.ButtonA:
                ToggleDownload(); break;
        }
    }
#endif
}
