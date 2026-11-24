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
            await DisplayAlertAsync("没有存档", "请先在首页加载一个存档，再来导入礼物。", "OK");
            return;
        }

        if (!entry.Gift.IsCardCompatible(sav, out var reason))
        {
            await DisplayAlertAsync("不兼容", reason, "OK");
            return;
        }

        var gift = entry.Gift;

        if (!gift.IsEntity || gift.Species <= 0)
        {
            await DisplayAlertAsync("只支持宝可梦礼物",
                "这张是道具礼物，当前版本还不能把道具写进存档。\n请选择宝可梦礼物卡。", "OK");
            return;
        }

        var detail = BuildGiftDetail(gift);
        if (FindExistingCopy(gift, sav, out var where))
            detail += $"\n\n提示：{where} 已有一份同样的礼物宝可梦。";

        var confirm = await DisplayAlertAsync(gift.CardTitle, detail, "选择位置", "取消");
        if (!confirm) return;

        var dest = await DisplayActionSheetAsync("导入到", "取消", null, "箱子", "银行");
        if (dest is null || dest == "取消") return;

        var pk = gift.ConvertToPKM(sav);
        sav.AdaptToSaveFile(pk);

        if (dest == "箱子")
        {
            if (!TryFindEmptyBoxSlot(sav, out int targetBox, out int targetSlot))
            {
                await DisplayAlertAsync("空间不足", "所有箱子都满了，可以先清出空格或改存银行。", "OK");
                return;
            }

            sav.SetBoxSlotAtIndex(pk, targetBox, targetSlot);
            await DisplayAlertAsync("完成",
                $"已放入 箱子 {targetBox + 1} · 第 {targetSlot + 1} 格。\n记得到首页保存存档。", "OK");
            return;
        }

        // 银行
        var bank = BankService.Instance;
        int bankBox = -1, bankSlot = -1;
        for (int b = 0; b < bank.Boxes.Count; b++)
        {
            int empty = bank.FindFirstEmpty(b);
            if (empty >= 0) { bankBox = b; bankSlot = empty; break; }
        }
        if (bankBox < 0)
        {
            bank.CreateBox($"Bank {bank.Boxes.Count + 1}");
            bankBox  = bank.Boxes.Count - 1;
            bankSlot = bank.FindFirstEmpty(bankBox);
        }
        if (bankBox < 0 || bankSlot < 0)
        {
            await DisplayAlertAsync("银行空间不足", "银行里没有可用空格。", "OK");
            return;
        }

        bank.Deposit(bankBox, bankSlot, pk);
        await DisplayAlertAsync("完成",
            $"已存入 银行 第 {bankBox + 1} 箱 · 第 {bankSlot + 1} 格。", "OK");
    }

    private static bool TryFindEmptyBoxSlot(SaveFile sav, out int box, out int slot)
    {
        for (box = 0; box < sav.BoxCount; box++)
        {
            var data = sav.GetBoxData(box);
            for (slot = 0; slot < data.Length; slot++)
            {
                if (data[slot].Species == 0) return true;
            }
        }
        box = slot = -1;
        return false;
    }

    /// <summary>箱子里或银行里是否已经有同一张卡的产物（同种 + 同 OT + 同 ID）。</summary>
    private static bool FindExistingCopy(MysteryGift gift, SaveFile sav, out string where)
    {
        where = "";

        for (int b = 0; b < sav.BoxCount; b++)
        {
            var data = sav.GetBoxData(b);
            for (int s = 0; s < data.Length; s++)
            {
                var pk = data[s];
                if (pk.Species != gift.Species) continue;
                if (pk.TID16 != gift.TID16 || pk.SID16 != gift.SID16) continue;
                if (pk.OriginalTrainerName != gift.OriginalTrainerName) continue;

                where = $"箱子 {b + 1} 第 {s + 1} 格";
                return true;
            }
        }

        var bank = BankService.Instance;
        for (int b = 0; b < bank.Boxes.Count; b++)
        {
            var slots = bank.Boxes[b].Slots;
            for (int s = 0; s < slots.Count; s++)
            {
                var slot = slots[s];
                if (slot is null || slot.Species != gift.Species) continue;

                var pk = slot.ToPKM();
                if (pk is null) continue;
                if (pk.TID16 != gift.TID16 || pk.SID16 != gift.SID16) continue;
                if (pk.OriginalTrainerName != gift.OriginalTrainerName) continue;

                where = $"银行 第 {b + 1} 箱 第 {s + 1} 格";
                return true;
            }
        }

        return false;
    }

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
