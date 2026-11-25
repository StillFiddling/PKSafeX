using Microsoft.Maui.Controls.Shapes;
using PKHeX.Mobile.Services;

namespace PKHeX.Mobile.Controls;

/// <summary>
/// 底部浮层：列出所有已导入存档，点一行即切换。
/// 由代码构建，箱子页与银行页共用同一外观和行为。
/// </summary>
public sealed class SaveSwitcherSheet : Grid
{
    private readonly SessionState _session;
    private readonly Func<SaveEntry, Task> _onPick;
    private readonly BoxView _scrim;
    private readonly Border _panel;
    private readonly VerticalStackLayout _list;
    private readonly Label _note;

    private static readonly Color PanelBg   = Color.FromArgb("#0D1528");
    private static readonly Color PanelLine = Color.FromArgb("#2A3F70");
    private static readonly Color TextMain  = Color.FromArgb("#EDF0FF");
    private static readonly Color TextSub   = Color.FromArgb("#C8D2E8");
    private static readonly Color TextDim   = Color.FromArgb("#8A97B8");
    private static readonly Color Accent    = Color.FromArgb("#3B8BFF");

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
            Text          = note ?? "银行里的宝可梦是全局共用的，切换存档不会改变银行内容。",
            FontSize      = 11,
            TextColor     = TextDim,
            LineBreakMode = LineBreakMode.WordWrap,
        };

        _panel = new Border
        {
            VerticalOptions = LayoutOptions.End,
            TranslationY    = 600,
            StrokeShape     = new RoundRectangle { CornerRadius = new CornerRadius(20, 20, 0, 0) },
            BackgroundColor = PanelBg,
            Stroke          = PanelLine,
            StrokeThickness = 1,
            Padding         = new Thickness(16, 14, 16, 24),
            Content = new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    new Label
                    {
                        Text       = "切换存档",
                        FontFamily = "NunitoExtraBold",
                        FontSize   = 16,
                        TextColor  = TextMain,
                    },
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
            _list.Add(new Label
            {
                Text      = "还没有导入其它存档。",
                FontSize  = 13,
                TextColor = TextDim,
                Margin    = new Thickness(2, 8, 2, 8),
            });
            return;
        }

        foreach (var entry in _session.LoadedSaves)
            _list.Add(BuildRow(entry));

        // 单文件添加但不在列表里的情况：告诉用户当前存档是哪一个
        if (_session.LoadedSaves.All(e => e.FileUri != _session.ActiveSaveFileUri))
        {
            _list.Add(new Label
            {
                Text      = $"当前：{_session.ActiveSaveFileName}（未在列表中）",
                FontSize  = 11,
                TextColor = TextDim,
                Margin    = new Thickness(2, 4, 2, 0),
            });
        }
    }

    private Border BuildRow(SaveEntry entry)
    {
        bool active = entry.FileUri == _session.ActiveSaveFileUri;

        var name = new Label
        {
            Text       = entry.TrainerName,
            FontFamily = "NunitoExtraBold",
            FontSize   = 15,
            TextColor  = active ? Accent : TextMain,
            MaxLines   = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        var sub = new Label
        {
            Text      = $"Pokémon {entry.Version} · {entry.FileName}",
            FontSize  = 11,
            TextColor = TextSub,
            MaxLines  = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        var check = new Label
        {
            Text            = active ? "当前" : "",
            FontFamily      = "NunitoBold",
            FontSize        = 11,
            TextColor       = Accent,
            VerticalOptions = LayoutOptions.Center,
            Margin          = new Thickness(8, 0, 0, 0),
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 8,
            Padding       = new Thickness(12, 8),
        };
        var left = new VerticalStackLayout { Spacing = 2, Children = { name, sub } };
        grid.Add(left);
        Grid.SetColumn(left, 0);
        Grid.SetRow(left, 0);

        Grid.SetColumn(check, 1);
        Grid.SetRow(check, 0);
        grid.Add(check);

        var border = new Border
        {
            StrokeShape     = new RoundRectangle { CornerRadius = 12 },
            BackgroundColor = Color.FromArgb(active ? "#182845" : "#111827"),
            Stroke          = active ? Accent : Colors.Transparent,
            StrokeThickness = 1,
            Content         = grid,
        };

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
