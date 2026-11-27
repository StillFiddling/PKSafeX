using Microsoft.Maui.Controls.Shapes;
using PKHeX.Mobile.Services;

namespace PKHeX.Mobile.Controls;

/// <summary>
/// 底部浮层：列出所有已导入存档，点一行即切换。
/// 由代码构建，箱子页与银行页共用同一外观和行为。
/// 颜色全部走主题动态资源（ThActionMenuPanel / ThCardBg / ThHintBg / ThText*），
/// 浅色主题下会自动变浅；副标题口径与图鉴页的存档选择行一致。
/// </summary>
public sealed class SaveSwitcherSheet : Grid
{
    private readonly SessionState _session;
    private readonly Func<SaveEntry, Task> _onPick;
    private readonly BoxView _scrim;
    private readonly Border _panel;
    private readonly VerticalStackLayout _list;
    private readonly Label _note;
    private readonly Label _title;

    private static readonly Color Accent = Color.FromArgb("#3B8BFF");

    public bool IsOpen { get; private set; }

    public SaveSwitcherSheet(SessionState session, Func<SaveEntry, Task> onPick, string? note = null)
    {
        _session = session;
        _onPick  = onPick;

        IsVisible        = false;
        InputTransparent = true;
        BackgroundColor  = Colors.Transparent;

        _scrim = new BoxView { BackgroundColor = Colors.Black, Opacity = 0, InputTransparent = true };
        _scrim.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => _ = HideAsync()) });
        Add(_scrim);

        _list = new VerticalStackLayout { Spacing = 6 };
        _note = new Label
        {
            Text          = note ?? UiText.BankSwitchNote,
            FontSize      = 11,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        _note.SetDynamicResource(Label.TextColorProperty, "ThTextDim");

        _title = new Label
        {
            Text       = "切换存档",
            FontFamily = "NunitoExtraBold",
            FontSize   = 16,
        };
        _title.SetDynamicResource(Label.TextColorProperty, "ThTextPrimary");

        _panel = new Border
        {
            VerticalOptions = LayoutOptions.End,
            TranslationY    = 600,
            StrokeShape     = new RoundRectangle { CornerRadius = new CornerRadius(20, 20, 0, 0) },
            StrokeThickness = 1,
            Padding         = new Thickness(16, 14, 16, 24),
            Content = new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    _title,
                    new ScrollView
                    {
                        Orientation    = ScrollOrientation.Vertical,
                        MaximumHeightRequest = 320,
                        Content        = _list,
                    },
                    _note,
                },
            },
        };
        _panel.SetDynamicResource(VisualElement.BackgroundColorProperty, "ThActionMenuPanel");
        _panel.SetDynamicResource(Border.StrokeProperty, "ThActionMenuStroke");
        Add(_panel);
    }

    public async Task ShowAsync()
    {
        if (IsOpen) return;
        BuildRows();

        IsVisible        = true;
        InputTransparent = false;
        _scrim.InputTransparent = false;

        var fade  = _scrim.FadeToAsync(0.55, 200, Easing.CubicOut);
        var slide = _panel.TranslateToAsync(0, 0, 240, Easing.CubicOut);
        await Task.WhenAll(fade, slide);

        IsOpen = true;
    }

    public async Task HideAsync()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _scrim.InputTransparent = true;

        var fade  = _scrim.FadeToAsync(0, 160, Easing.CubicIn);
        var slide = _panel.TranslateToAsync(0, 600, 200, Easing.CubicIn);
        await Task.WhenAll(fade, slide);

        InputTransparent = true;
        IsVisible        = false;
    }

    private void BuildRows()
    {
        _list.Clear();

        if (_session.LoadedSaves.Count == 0)
        {
            var empty = new Label
            {
                Text      = "还没有导入其它存档。",
                FontSize  = 13,
                Margin    = new Thickness(2, 8, 2, 8),
            };
            empty.SetDynamicResource(Label.TextColorProperty, "ThTextDim");
            _list.Add(empty);
            return;
        }

        foreach (var entry in _session.LoadedSaves)
            _list.Add(BuildRow(entry));

        // 单文件添加但不在列表里的情况：告诉用户当前存档是哪一个
        if (_session.LoadedSaves.All(e => e.FileUri != _session.ActiveSaveFileUri))
        {
            var current = new Label
            {
                Text      = $"当前：{_session.ActiveSaveFileName}（未在列表中）",
                FontSize  = 11,
                Margin    = new Thickness(2, 4, 2, 0),
            };
            current.SetDynamicResource(Label.TextColorProperty, "ThTextDim");
            _list.Add(current);
        }
    }

    /// <summary>副标题口径与图鉴页一致：版本 · 训练家ID · 游戏时间 · 箱子数。
    /// 字段为空时退化回「版本 · 文件名」，避免拼出「Y ·  ·  · 0箱」这种空档。</summary>
    private static string BuildMeta(SaveEntry entry)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(entry.Version.ToString()))
            parts.Add(entry.Version.ToString());
        if (!string.IsNullOrWhiteSpace(entry.TrainerID))
            parts.Add(entry.TrainerID);
        if (!string.IsNullOrWhiteSpace(entry.PlayTime))
            parts.Add(entry.PlayTime);
        if (entry.BoxCount > 0)
            parts.Add($"{entry.BoxCount}箱");

        if (parts.Count <= 1)
            return string.IsNullOrWhiteSpace(entry.FileName) ? string.Empty : entry.FileName;
        return string.Join(" · ", parts);
    }

    private Border BuildRow(SaveEntry entry)
    {
        bool active = entry.FileUri == _session.ActiveSaveFileUri;

        // 行首游戏图标（与图鉴页视觉一致）
        var icon = new Image
        {
            WidthRequest   = 30,
            HeightRequest  = 30,
            Aspect         = Aspect.AspectFill,
            VerticalOptions = LayoutOptions.Center,
        };
        var iconFile = Pages.GamePage.GetTrainerIconFile(entry.Version);
        if (iconFile is not null)
        {
            icon.Source = ImageSource.FromStream(
                ct => FileSystem.OpenAppPackageFileAsync($"gameicons/{iconFile}").WaitAsync(ct));
        }

        var name = new Label
        {
            Text          = string.IsNullOrWhiteSpace(entry.TrainerName) ? entry.FileName : entry.TrainerName,
            FontFamily    = "NunitoExtraBold",
            FontSize      = 14,
            MaxLines      = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        name.SetDynamicResource(Label.TextColorProperty, "ThTextPrimary");

        var sub = new Label
        {
            Text          = BuildMeta(entry),
            FontFamily    = "Nunito",
            FontSize      = 10,
            MaxLines      = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        sub.SetDynamicResource(Label.TextColorProperty, "ThTextDim");

        var check = new Label
        {
            Text            = active ? "当前" : string.Empty,
            FontFamily      = "NunitoBold",
            FontSize        = 11,
            TextColor       = Accent,
            VerticalOptions = LayoutOptions.Center,
            Margin          = new Thickness(6, 0, 0, 0),
        };

        var text = new VerticalStackLayout
        {
            Spacing         = 2,
            VerticalOptions = LayoutOptions.Center,
            Children        = { name, sub },
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 9,
            Padding       = new Thickness(10, 8),
        };
        grid.Add(icon, 0);
        grid.Add(text, 1);
        grid.Add(check, 2);

        var border = new Border
        {
            StrokeShape     = new RoundRectangle { CornerRadius = 10 },
            StrokeThickness = 1,
            Content         = grid,
        };
        border.SetDynamicResource(VisualElement.BackgroundColorProperty, active ? "ThHintBg" : "ThCardBg");
        border.SetDynamicResource(Border.StrokeProperty, "ThHintStroke");

        border.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                if (active || _session.SaveWriteBusy) return;
                await HideAsync();
                await _onPick(entry);
            }),
        });

        return border;
    }
}
