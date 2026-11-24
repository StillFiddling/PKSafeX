using System.ComponentModel;
using System.Text;
using PKHeX.Core;
using PKHeX.Mobile.Services;

namespace PKHeX.Mobile.Pages;

public partial class MysteryGiftDBPage : ContentPage
{
    private readonly GameStrings _strings = GameLang.Strings;
    private List<GiftEntry> _all = [];
    private List<GiftEntry> _filtered = [];
    private int _gpIndex = -1;
    private GiftEntry? _highlightedEntry;
    private DateTime _lastListNav = DateTime.MinValue;

    private readonly SessionState _session;

    public MysteryGiftDBPage(SessionState session)
    {
        _session = session;
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        GamepadRouter.KeyReceived += OnGamepadKey;
#endif
        MysteryGiftLibraryService.EnsureLoaded();
        UpdateLibraryBar();

        if (_all.Count == 0)
            BuildIndex();
        ApplyFilter();
    }

    private void UpdateLibraryBar()
    {
        var count = MysteryGiftLibraryService.CountFiles();
        LibraryCountLabel.Text = count > 0
            ? $"自备礼物卡：{count} 张（已并入列表）"
            : "自备礼物卡：0 张，点右侧导入";
        LibraryPathLabel.Text = MysteryGiftLibraryService.LibraryPath;
    }

    private async void OnImportTapped(object? sender, TappedEventArgs e)
    {
#if ANDROID
        var picker = new Platforms.Android.AndroidFilePicker();
        var uri = await picker.PickFileAsync();
        if (string.IsNullOrEmpty(uri)) return;

        var (ok, message) = await MysteryGiftLibraryService.ImportAsync(uri);
        if (!ok)
        {
            await DisplayAlertAsync("未导入", message, "OK");
            UpdateLibraryBar();
            return;
        }

        _all = [];
        BuildIndex();
        UpdateLibraryBar();
        ApplyFilter();

        await DisplayAlertAsync("已导入", $"{message}。当前自备卡 {MysteryGiftLibraryService.CountFiles()} 张。", "OK");
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
#if ANDROID
        GamepadRouter.KeyReceived -= OnGamepadKey;
#endif
    }

#if ANDROID
    private void OnGamepadKey(Android.Views.Keycode keyCode, Android.Views.KeyEventActions action)
    {
        if (action != Android.Views.KeyEventActions.Down) return;
        MainThread.BeginInvokeOnMainThread(() => HandleGamepadKey(keyCode));
    }

    private void HandleGamepadKey(Android.Views.Keycode keyCode)
    {
        if (ReceivedOverlay.IsVisible)
        {
            if (keyCode == Android.Views.Keycode.ButtonB)
                ReceivedOverlay.IsVisible = false;
            return;
        }

        switch (keyCode)
        {
            case Android.Views.Keycode.ButtonB:
                _ = Shell.Current.GoToAsync(".."); break;

            case Android.Views.Keycode.DpadUp:
                MoveListCursor(-1); break;

            case Android.Views.Keycode.DpadDown:
                MoveListCursor(+1); break;

            case Android.Views.Keycode.ButtonA:
                if (_gpIndex >= 0 && _gpIndex < _filtered.Count)
                    _ = InjectGift(_filtered[_gpIndex]);
                break;

            case Android.Views.Keycode.ButtonY:
                SearchEntry.Focus(); break;
        }
    }
#endif

    private void MoveListCursor(int delta)
    {
        if ((DateTime.UtcNow - _lastListNav).TotalMilliseconds < 150) return;
        _lastListNav = DateTime.UtcNow;

        if (_filtered.Count == 0) return;
        if (_highlightedEntry is not null) _highlightedEntry.IsHighlighted = false;
        _gpIndex = Math.Clamp(_gpIndex + delta, 0, _filtered.Count - 1);
        _highlightedEntry = _filtered[_gpIndex];
        _highlightedEntry.IsHighlighted = true;

        GiftList.ScrollTo(_gpIndex, -1, ScrollToPosition.MakeVisible, false);
    }

    private void BuildIndex()
    {
        _all = [];
        foreach (var gift in EncounterEvent.GetAllEvents())
        {
            var title = gift.CardHeader;
            string subInfo;
            ImageSource? sprite = null;

            if (gift.IsEntity && gift.Species > 0)
            {
                var speciesName = gift.Species < _strings.specieslist.Length
                    ? _strings.specieslist[gift.Species] : gift.Species.ToString();
                subInfo = $"{speciesName}  Lv.{gift.Level}";

                sprite = LocalSpriteFirst(gift.Species, gift.Form);
            }
            else if (!gift.IsEntity)
            {
                subInfo = "道具礼物";
            }
            else
            {
                subInfo = "";
            }

            _all.Add(new GiftEntry(gift, title, subInfo, $"Gen {gift.Generation}", sprite));
        }
    }

    /// <summary>本地 HOME 图优先（离线可用），没有才回落到网络。</summary>
    private static ImageSource LocalSpriteFirst(ushort species, byte form)
    {
        var slug  = HomeSpriteCacheService.GetHomeSlug(species, form);
        var local = Path.Combine(HomeSpriteCacheService.CacheRootPath, $"{slug}.png");
        if (File.Exists(local)) return ImageSource.FromFile(local);

        return new UriImageSource
        {
            Uri = new Uri(HomeSpriteCacheService.GetHomeUrl(species, form, false)),
            CacheValidity = TimeSpan.FromDays(30),
        };
    }

    private void ApplyFilter()
    {
        var sav = _session.ActiveSave;
        var text = SearchEntry.Text?.Trim() ?? "";
        var compatOnly = CompatibleOnly.IsToggled && sav is not null;

        _filtered = _all.Where(e =>
        {
            if (text.Length > 0 &&
                !e.Title.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                !e.SubInfo.Contains(text, StringComparison.OrdinalIgnoreCase))
                return false;

            if (compatOnly && !e.Gift.IsCardCompatible(sav!, out _))
                return false;

            return true;
        }).ToList();

        if (_highlightedEntry is not null) { _highlightedEntry.IsHighlighted = false; _highlightedEntry = null; }
        _gpIndex = -1;
        GiftList.ItemsSource = _filtered;
    }

    private void OnFilterChanged(object sender, EventArgs e) => ApplyFilter();

    private async void OnGiftSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not GiftEntry entry)
            return;
        GiftList.SelectedItem = null;
        await InjectGift(entry);
    }

    private async Task InjectGift(GiftEntry entry)
    {
        GiftList.SelectedItem = null;

        var sav = _session.ActiveSave;
        if (sav is null)
        {
            await DisplayAlertAsync("没有存档", "请先在首页加载一个存档，再来接收礼物。", "OK");
            return;
        }

        var gift = entry.Gift;
        var detail = BuildGiftDetail(gift);

        // 只有「数据卡」（直接来自 .wc7/.pcd 等文件）才能写进礼物槽
        if (gift is not DataMysteryGift dataGift)
        {
            await DisplayAlertAsync("无法写入", detail + "\n这一类卡片没有可写入的原始数据，暂时不能写进礼物槽。", "OK");
            return;
        }

        // 第八/九世代没有可写礼物槽
        if (!TryGetStorage(sav, out var storage, out var slotError))
        {
            await DisplayAlertAsync(gift.CardTitle, detail + "\n\n" + slotError, "OK");
            return;
        }

        if (!CardTypeSupported(storage, dataGift, out var expected))
        {
            await DisplayAlertAsync("卡片格式不匹配",
                $"{detail}\n这张卡是 {gift.GetType().Name} 格式，当前存档的礼物槽只接受 {expected}。\n跨世代的卡不能直接写入礼物槽。",
                "OK");
            return;
        }

        int freeSlot = FindEmptyGiftSlot(storage);
        if (freeSlot < 0)
        {
            await DisplayAlertAsync("礼物槽已满",
                "当前存档的礼物槽已经装满。\n可以先在「我接收的礼物」里删掉不需要的卡，再来接收。", "OK");
            return;
        }

        if (GiftAlreadyPresent(storage, gift, out var existingIndex))
            detail += $"\n\n提示：礼物槽第 {existingIndex + 1} 格里已经有这张卡。";

        var confirm = await DisplayAlertAsync(gift.CardTitle,
            detail +
            $"\n\n接收后会写入礼物槽第 {freeSlot + 1} 格。\n回游戏里打开神秘礼物 / 找配信员领取，宝可梦由游戏生成。",
            "接收礼物", "取消");
        if (!confirm) return;

        int writtenSlot = -1;
        try
        {
            var result = await SaveSyncService.ApplyAndWriteAsync(_session, s =>
            {
                if (s is not IMysteryGiftStorageProvider provider) return;
                var st = provider.MysteryGiftStorage;

                int idx = FindEmptyGiftSlot(st);
                if (idx < 0) return;

                st.SetMysteryGift(idx, dataGift);
                ClearReceivedFlag(st, gift);
                if (st is MysteryBlock4 gen4)
                    gen4.IsDeliveryManActive = true; // 四代要手动点亮配信员

                writtenSlot = idx;
            });

            switch (result)
            {
                case SaveWriteResult.Written when writtenSlot >= 0:
                    await DisplayAlertAsync("已接收",
                        $"已写入礼物槽第 {writtenSlot + 1} 格，存档已保存。\n回游戏里打开神秘礼物 / 找配信员领取。",
                        "OK");
                    break;

                case SaveWriteResult.MergedIntoLatest when writtenSlot >= 0:
                    await DisplayAlertAsync("已接收（含并最新进度）",
                        "检测到你在游戏里已经保存过比软件更新的存档。\n" +
                        $"本次只把「礼物槽第 {writtenSlot + 1} 格」合并进最新存档，其它进度没有被覆盖。\n回游戏里领取即可。",
                        "OK");
                    break;

                case SaveWriteResult.NoUri:
                    await DisplayAlertAsync("无法写入",
                        "找不到原存档文件位置（可能是从压缩包加载的存档）。\n请在首页导出存档后再试。", "OK");
                    break;

                default:
                    await DisplayAlertAsync("无法写入",
                        "存档已被游戏更新且无法解析，或礼物槽已满。\n请回首页重新加载存档后再试。", "OK");
                    break;
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("接收失败，存档未改动", ex.Message, "OK");
        }
    }

    // ──────────────────────────────────────────────
    //  礼物槽读写
    // ──────────────────────────────────────────────

    private static bool TryGetStorage(SaveFile sav, out IMysteryGiftStorage storage, out string error)
    {
        storage = null!;
        error   = "";

        if (sav is not IMysteryGiftStorageProvider provider)
        {
            error = $"《{sav.Version}》这一代存档没有可写入的礼物槽（第八、九世代暂不支持）。";
            return false;
        }

        storage = provider.MysteryGiftStorage;
        return true;
    }

    private static bool CardTypeSupported(IMysteryGiftStorage storage, DataMysteryGift gift, out string expected)
    {
        var allowed = new HashSet<Type>();
        for (int i = 0; i < storage.GiftCountMax; i++)
            allowed.Add(storage.GetMysteryGift(i).GetType());

        expected = string.Join(" / ", allowed.Select(t => t.Name));
        return allowed.Contains(gift.GetType());
    }

    private static int FindEmptyGiftSlot(IMysteryGiftStorage storage)
    {
        for (int i = 0; i < storage.GiftCountMax; i++)
        {
            if (storage.GetMysteryGift(i).IsEmpty) return i;
        }
        return -1;
    }

    private static void ClearReceivedFlag(IMysteryGiftStorage storage, MysteryGift gift)
    {
        if (storage is not IMysteryGiftFlags flags) return;
        if (gift.CardID <= 0 || gift.CardID >= flags.MysteryGiftReceivedFlagMax) return;
        flags.SetMysteryGiftReceivedFlag(gift.CardID, false);
    }

    private static bool GiftAlreadyPresent(IMysteryGiftStorage storage, MysteryGift gift, out int index)
    {
        index = -1;
        if (gift.CardID <= 0) return false;

        for (int i = 0; i < storage.GiftCountMax; i++)
        {
            var card = storage.GetMysteryGift(i);
            if (card.IsEmpty || card is not MysteryGift m) continue;
            if (m.CardID != gift.CardID) continue;

            index = i;
            return true;
        }
        return false;
    }

    // ──────────────────────────────────────────────
    //  我接收的礼物
    // ──────────────────────────────────────────────

    private async void OnReceivedTapped(object? sender, TappedEventArgs e)
    {
        var sav = _session.ActiveSave;
        if (sav is null)
        {
            await DisplayAlertAsync("没有存档", "请先在首页加载一个存档。", "OK");
            return;
        }

        if (!TryGetStorage(sav, out var storage, out var error))
        {
            await DisplayAlertAsync("暂不支持", error, "OK");
            return;
        }

        BuildReceivedRows(sav, storage);
        ReceivedOverlay.IsVisible = true;
    }

    private void OnReceivedCloseTapped(object? sender, TappedEventArgs e)
        => ReceivedOverlay.IsVisible = false;

    private void BuildReceivedRows(SaveFile sav, IMysteryGiftStorage storage)
    {
        ReceivedList.Children.Clear();

        int count = 0;
        for (int i = 0; i < storage.GiftCountMax; i++)
        {
            var card = storage.GetMysteryGift(i);
            if (card.IsEmpty) continue;
            count++;

            string title, sub;
            if (card is MysteryGift m)
            {
                title = m.CardHeader;
                sub = m.IsEntity && m.Species > 0
                    ? $"{SpeciesName(m.Species)} Lv.{m.Level} · Gen {m.Generation}"
                    : $"道具礼物 · Gen {m.Generation}";
            }
            else
            {
                title = $"第 {i + 1} 格";
                sub   = "无法识别的卡片";
            }

            int slotIndex = i;

            var stack = new VerticalStackLayout { Spacing = 1 };
            stack.Add(new Label
            {
                Text = $"{i + 1}. {title}",
                FontFamily = "NunitoBold", FontSize = 11,
                TextColor = Res("ThTextPrimary", "#EDF0FF"),
                LineBreakMode = LineBreakMode.TailTruncation,
            });
            stack.Add(new Label
            {
                Text = sub,
                FontFamily = "Nunito", FontSize = 10,
                TextColor = Res("ThTextSecondary", "#8B97B0"),
            });

            var border = new Border
            {
                StrokeShape     = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                BackgroundColor = Res("ThHintBg", "#132038"),
                Stroke          = Res("ThHintStroke", "#2A3A5A"),
                StrokeThickness = 1,
                Padding         = new Thickness(10, 7),
                Content         = stack,
            };
            border.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(() => _ = DeleteReceivedGiftAsync(slotIndex)),
            });

            ReceivedList.Children.Add(border);
        }

        if (count == 0)
        {
            ReceivedList.Children.Add(new Label
            {
                Text = "礼物槽是空的，还没有接收过任何卡片。",
                FontFamily = "Nunito", FontSize = 11,
                TextColor = Res("ThTextSecondary", "#8B97B0"),
                Margin = new Thickness(2, 6, 2, 6),
            });
        }

        ReceivedTitle.Text = $"我接收的礼物（{count}）";
        ReceivedHint.Text  = $"{sav.Version} · 礼物槽 {storage.GiftCountMax} 格，点一行可删除";
    }

    private async Task DeleteReceivedGiftAsync(int index)
    {
        var sav = _session.ActiveSave;
        if (sav is null) return;
        if (!TryGetStorage(sav, out var storage, out _)) return;

        var card = storage.GetMysteryGift(index);
        if (card.IsEmpty) return;

        var name = card is MysteryGift m ? m.CardHeader : $"第 {index + 1} 格";
        if (!await DisplayAlertAsync("删除礼物卡",
            $"{name}\n删除后回游戏里就领不到这张卡了，确定删除吗？", "删除", "取消"))
            return;

        try
        {
            var result = await SaveSyncService.ApplyAndWriteAsync(_session, s =>
            {
                if (s is not IMysteryGiftStorageProvider provider) return;
                var st = provider.MysteryGiftStorage;

                var empty = st.GetMysteryGift(index);
                empty.Clear();
                st.SetMysteryGift(index, empty);
            });

            if (result is SaveWriteResult.NoUri or SaveWriteResult.StaleUnreadable)
            {
                await DisplayAlertAsync("删除失败，存档未改动",
                    result == SaveWriteResult.NoUri
                        ? "找不到原存档文件位置，请改用「导出」。"
                        : "存档已被游戏更新且无法解析，请回首页重新加载存档后再试。", "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("删除失败，存档未改动", ex.Message, "OK");
        }

        var latest = _session.ActiveSave;
        if (latest is not null && TryGetStorage(latest, out var refreshed, out _))
            BuildReceivedRows(latest, refreshed);
    }

    private static Color Res(string key, string fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color c)
            return c;
        return Color.FromArgb(fallback);
    }

    private string SpeciesName(ushort species)
        => species > 0 && species < _strings.specieslist.Length
            ? _strings.specieslist[species]
            : species.ToString();

    private string BuildGiftDetail(MysteryGift gift)
    {
        var sb = new StringBuilder();
        sb.Append("卡片：").AppendLine(gift.CardHeader);
        sb.Append("世代：").Append(gift.Generation).Append("    版本：").AppendLine(gift.Version.ToString());

        var name = gift.Species > 0 && gift.Species < _strings.specieslist.Length
            ? _strings.specieslist[gift.Species] : gift.Species.ToString();

        sb.Append("宝可梦：").Append(name).Append("    等级：").AppendLine(gift.Level.ToString());
        sb.Append("原训练家：").Append(gift.OriginalTrainerName)
          .Append("    ID：").Append(gift.TID16).Append('/').AppendLine(gift.SID16.ToString());
        sb.Append("携带道具：").AppendLine(gift.HeldItem > 0 ? gift.HeldItem.ToString() : "无");
        sb.Append("闪光：").Append(ShinyLabel(gift.Shiny))
          .Append("    命运相遇：").AppendLine(gift.FatefulEncounter ? "是" : "否");

        return sb.ToString();
    }

    private static string ShinyLabel(Shiny shiny) => shiny switch
    {
        Shiny.Never       => "锁定不闪",
        Shiny.Always      => "必定闪光",
        Shiny.AlwaysStar  => "必定闪光（星）",
        Shiny.AlwaysSquare => "必定闪光（方）",
        Shiny.FixedValue  => "固定 PID",
        _                 => "随机",
    };

}

public class GiftEntry(MysteryGift gift, string title, string subInfo,
    string genLabel, ImageSource? spriteSource) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public MysteryGift Gift    { get; } = gift;
    public string Title        { get; } = title;
    public string SubInfo      { get; } = subInfo;
    public string GenLabel     { get; } = genLabel;
    public ImageSource? SpriteSource { get; } = spriteSource;

    private bool _isHighlighted;
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            if (_isHighlighted == value) return;
            _isHighlighted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
        }
    }
}
