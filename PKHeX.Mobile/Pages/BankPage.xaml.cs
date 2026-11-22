using Microsoft.Maui.Controls.Shapes;
using PKHeX.Core;
using PKHeX.Mobile.Services;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using static PKHeX.Mobile.Services.ThemeService;

namespace PKHeX.Mobile.Pages;

public partial class BankPage : ContentPage
{
    private const int Columns = 6;
    private const int Rows    = 5;

    private readonly ISecondaryDisplay        _secondary;
    private readonly SessionState             _session;
    private readonly BankService              _bank    = new();
    private readonly FileSystemSpriteRenderer _sprites = new();
    private readonly GameStrings              _strings = GameLang.Strings;

    private PKM?[] _currentSlots = new PKM?[BankService.SlotsPerBox];
    private int    _boxIndex;
    private int    _cursorSlot;
    private int    _selectedSlot = -1;

    // Move state (withdraw: grabbed from bank; deposit: arrived from game)
    private bool _moveMode;   // true = grabbed from bank (withdraw grab)
    private PKM? _movePk;
    private int  _moveSourceBox;
    private int  _moveSourceSlot;

    // Detail overlay state (same layout as GamePage PhoneDetailSheet)
    private PKM? _detailPk;
    private int  _detailSlot = -1;
    private bool _detailVisible;

    public BankPage(ISecondaryDisplay secondary, SessionState session)
    {
        _secondary = secondary;
        _session   = session;
        InitializeComponent();
    }

    // ──────────────────────────────────────────────
    //  Lifecycle
    // ──────────────────────────────────────────────

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        GamepadRouter.KeyReceived        -= OnGamepadKey;
        GamepadRouter.KeyReceived        += OnGamepadKey;
        GamepadRouter.BoxScrollRequested -= OnBoxScroll;
        GamepadRouter.BoxScrollRequested += OnBoxScroll;
#endif
        ThemeService.ThemeChanged -= OnThemeChanged;
        ThemeService.ThemeChanged += OnThemeChanged;
        // Slide in the inner grid (not `this`) so hit-testing is always correct
        RootGrid.TranslationX = _session.BankSlideDir < 0 ? -500 : 500;
        await RootGrid.TranslateToAsync(0, 0, 260, Easing.CubicInOut);

        // Warn once if bank.json was corrupt on load (backup saved alongside it)
        var backupPath = BankService.TakeCorruptionWarning();
        if (backupPath is not null)
            await DisplayAlertAsync("银行数据已损坏",
                $"Your bank file was unreadable and has been reset. A backup was saved to:\n{backupPath}",
                "OK");

        UpdateModeBanner();
        FillSaveHeader();
        LoadBox(_boxIndex);
        BuildHintBar();
    }

    // 顶部「当前存档」信息（与箱子页同款）
    private void FillSaveHeader()
    {
        var sav = _session.ActiveSave;
        if (sav is null)
        {
            BankTrainerNameLabel.Text = UiText.NoSaveLoaded;
            BankSaveGameLabel.Text    = "";
            BankSaveMetaLabel.Text     = "";
            BankGameIcon.Source        = null;
            return;
        }

        BankTrainerNameLabel.Text = sav.OT;
        BankSaveGameLabel.Text    = UiText.GameName(sav.Version.ToString());
        BankSaveMetaLabel.Text    = UiText.SaveMeta((int)sav.TrainerTID7, (int)sav.TrainerSID7, sav.BoxCount);

        var iconFile = GamePage.GetTrainerIconFile(sav);
        if (iconFile != null)
            BankGameIcon.Source = ImageSource.FromStream(
                ct => FileSystem.OpenAppPackageFileAsync($"gameicons/{iconFile}").WaitAsync(ct));
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
#if ANDROID
        GamepadRouter.KeyReceived        -= OnGamepadKey;
        GamepadRouter.BoxScrollRequested -= OnBoxScroll;
#endif
        ThemeService.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() => BankCanvas.InvalidateSurface());

    // ──────────────────────────────────────────────
    //  Box loading
    // ──────────────────────────────────────────────

    private async void LoadBox(int box)
    {
        _boxIndex      = box;
        _currentSlots  = _bank.GetBoxData(box);
        BankNameLabel.Text  = LocalizeBankName(box < _bank.Boxes.Count ? _bank.Boxes[box].Name : $"银行 {box + 1}");
        BoxIndexLabel.Text  = $"{box + 1} / {_bank.Boxes.Count}";

        await _sprites.PreloadBoxAsync(_currentSlots.Select(p => p ?? CreateBlankPKM()).ToArray());
        BankCanvas.InvalidateSurface();
        UpdateHoverLabel();
    }

    private static PKM CreateBlankPKM() => new PK9();

    // ──────────────────────────────────────────────
    //  Box navigation
    // ──────────────────────────────────────────────

    private void OnPrevBox(object sender, EventArgs e)
    {
        if (_boxIndex <= 0) return;
        _boxIndex--;
        DeselectSlot();
        LoadBox(_boxIndex);
    }

    private void OnNextBox(object sender, EventArgs e)
    {
        if (_boxIndex >= _bank.Boxes.Count - 1) return;
        _boxIndex++;
        DeselectSlot();
        LoadBox(_boxIndex);
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
    //  Rendering
    // ──────────────────────────────────────────────

    private void OnBankPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(ThemeService.CanvasBg);
        if (_currentSlots.Length == 0) return;

        const float gap    = 6f;
        const float padX   = 14f, padY = 4f;
        const float radius = 10f;

        float availW   = e.Info.Width  - padX * 2 - gap * (Columns - 1);
        float availH   = e.Info.Height - padY * 2 - gap * (Rows - 1);
        float slotSize = MathF.Min(availW / Columns, availH / Rows);
        float gridW    = slotSize * Columns + gap * (Columns - 1);
        float gridH    = slotSize * Rows    + gap * (Rows - 1);
        float offX     = (e.Info.Width  - gridW) / 2f;
        float offY     = (e.Info.Height - gridH) / 2f;

        for (int i = 0; i < BankService.SlotsPerBox; i++)
        {
            int   col = i % Columns, row = i / Columns;
            float x   = offX + col * (slotSize + gap);
            float y   = offY + row * (slotSize + gap);
            var   rect = new SKRect(x, y, x + slotSize, y + slotSize);
            var   pk   = i < _currentSlots.Length ? _currentSlots[i] : null;

            bool isCursor   = i == _cursorSlot;
            bool isSelected = i == _selectedSlot;
            bool isSource   = _moveMode && _boxIndex == _moveSourceBox && i == _moveSourceSlot;
            bool filled     = pk?.Species > 0;

            // Slot background
            var bgColor = filled ? ThemeService.SlotFilled : ThemeService.SlotEmpty;
            using var bgPaint = new SKPaint { Color = bgColor, IsAntialias = true };
            canvas.DrawRoundRect(rect, radius, radius, bgPaint);

            using var borderPaint = new SKPaint
            {
                Color = ThemeService.SlotBorder,
                Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true,
            };
            canvas.DrawRoundRect(rect, radius, radius, borderPaint);

            // Sprite �?ghost opacity at source slot
            if (filled)
            {
                var sprite = _sprites.GetSprite(pk!);
                float scale = isCursor ? 0.75f : 0.70f;
                float inner = slotSize * scale;
                float aspect = sprite.Width > 0 ? (float)sprite.Width / sprite.Height : 1f;
                float drawW, drawH;
                if (aspect >= 1f) { drawW = inner; drawH = inner / aspect; }
                else              { drawH = inner; drawW = inner * aspect; }
                float sx = rect.MidX - drawW / 2f;
                float sy = rect.MidY - drawH / 2f;
                byte alpha = isSource ? (byte)70 : (byte)255;
                using var spritePaint = new SKPaint { Color = SKColors.White.WithAlpha(alpha) };
                canvas.DrawBitmap(sprite, SKRect.Create(sx, sy, drawW, drawH), spritePaint);
            }

            // Ghost of pending deposit at cursor
            if (isCursor && _session.PendingMove != null && !_session.PendingFromBank && _movePk == null)
            {
                var ghost = _sprites.GetSprite(_session.PendingMove);
                float inner = slotSize * 0.70f;
                float aspect = ghost.Width > 0 ? (float)ghost.Width / ghost.Height : 1f;
                float drawW, drawH;
                if (aspect >= 1f) { drawW = inner; drawH = inner / aspect; }
                else              { drawH = inner; drawW = inner * aspect; }
                float sx = rect.MidX - drawW / 2f, sy = rect.MidY - drawH / 2f;
                using var ghostPaint = new SKPaint { Color = SKColors.White.WithAlpha(110) };
                canvas.DrawBitmap(ghost, SKRect.Create(sx, sy, drawW, drawH), ghostPaint);
            }

            // Ghost of grabbed Pokémon in withdraw move mode
            if (isCursor && _moveMode && _movePk != null && !isSource)
            {
                var ghost = _sprites.GetSprite(_movePk);
                float inner = slotSize * 0.70f;
                float aspect = ghost.Width > 0 ? (float)ghost.Width / ghost.Height : 1f;
                float drawW, drawH;
                if (aspect >= 1f) { drawW = inner; drawH = inner / aspect; }
                else              { drawH = inner; drawW = inner * aspect; }
                float sx = rect.MidX - drawW / 2f, sy = rect.MidY - drawH / 2f;
                using var ghostPaint = new SKPaint { Color = SKColors.White.WithAlpha(110) };
                canvas.DrawBitmap(ghost, SKRect.Create(sx, sy, drawW, drawH), ghostPaint);
            }

            // Cursor outline
            if (isCursor)
            {
                var color = _moveMode
                    ? new SKColor(60, 220, 110, 230)
                    : _session.PendingMove != null
                    ? new SKColor(60, 200, 255, 230)
                    : new SKColor(80, 160, 255, 200);
                using var p = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = 3f, IsAntialias = true };
                canvas.DrawRoundRect(rect, radius, radius, p);
            }

            if (isSelected)
            {
                using var p = new SKPaint { Color = new SKColor(200, 170, 80, 160), Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, IsAntialias = true };
                canvas.DrawRoundRect(rect, radius, radius, p);
            }

            if (isSource)
            {
                using var p = new SKPaint { Color = new SKColor(220, 80, 60, 190), Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
                canvas.DrawRoundRect(rect, radius, radius, p);
            }
        }
    }

    // ──────────────────────────────────────────────
    //  Touch input
    // ──────────────────────────────────────────────

    private void OnBankTapped(object sender, TappedEventArgs e)
    {
        if (sender is not View view) return;
        var point = e.GetPosition(view);
        if (point is null) return;

        float slotSize = (float)Math.Min(view.Width / Columns, view.Height / Rows);
        float offX = (float)(view.Width  - slotSize * Columns) / 2f;
        float offY = (float)(view.Height - slotSize * Rows)    / 2f;
        int col   = (int)((point.Value.X - offX) / slotSize);
        int row   = (int)((point.Value.Y - offY) / slotSize);
        int index = row * Columns + col;
        if ((uint)index >= BankService.SlotsPerBox) return;

        _cursorSlot = index;
        HandleActivate();
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
        if (_detailVisible)
        {
            switch (keyCode)
            {
                case Android.Views.Keycode.ButtonB:
                case Android.Views.Keycode.ButtonA:
                    _ = HideDetailAsync(); return;
                case KeyMap.CryKey:
                    PlayDetailCry(); return;
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
                HandleActivate(); break;

            case Android.Views.Keycode.ButtonY:
                if (_moveMode) { CancelMoveMode(); break; }
                if (_cursorSlot < _currentSlots.Length && _currentSlots[_cursorSlot]?.Species > 0)
                    EnterWithdrawMode(); break;

            case Android.Views.Keycode.ButtonB:
                if (_moveMode) { CancelMoveMode(); break; }
                if (_selectedSlot >= 0) { DeselectSlot(); break; }
                _session.PendingMove = null; // cancel any pending deposit
                _ = SwapToGame();
                break;

            case KeyMap.BankOutKey: _ = SwapToGame(-1); break;
            case KeyMap.BankInKey: _ = SwapToGame(+1); break;

            case KeyMap.CryKey:
                if (_cursorSlot < _currentSlots.Length && _currentSlots[_cursorSlot]?.Species > 0)
                    PlayCry(_currentSlots[_cursorSlot]!.Species);
                break;

            case Android.Views.Keycode.ButtonSelect:
                OpenBankManageMenu(); break;
        }
    }

#endif

    private void MoveCursor(int delta)
    {
        if (delta == -1 && _cursorSlot % Columns == 0)           return;
        if (delta == +1 && _cursorSlot % Columns == Columns - 1) return;
        int next = _cursorSlot + delta;
        if ((uint)next >= BankService.SlotsPerBox) return;
        _cursorSlot = next;
        BankCanvas.InvalidateSurface();
        UpdateHoverLabel();
    }

    // ──────────────────────────────────────────────
    //  Actions
    // ──────────────────────────────────────────────

    private void HandleActivate()
    {
        // Deposit mode: arriving from game with a Pokémon to place
        if (_session.PendingMove != null && !_session.PendingFromBank)
        {
            ExecuteDeposit();
            return;
        }

        // Withdraw move mode: drop grabbed Pokémon (swap with slot contents)
        if (_moveMode && _movePk != null)
        {
            ExecuteWithdrawDrop();
            return;
        }

        // 正常模式：选中并弹出详情浮层（与箱子页同款布局�?
        if (_cursorSlot < _currentSlots.Length && _currentSlots[_cursorSlot]?.Species > 0)
        {
            SelectSlot(_cursorSlot);
            _ = ShowDetailAsync(_currentSlots[_cursorSlot]!);
        }
        else
        {
            DeselectSlot();
            _ = ShowTipAsync("银行里不能新建宝可梦，只能在箱子里点 存入银行 把宝可梦存进来。");
        }
    }

    private void ExecuteDeposit()
    {
        var pk = _session.PendingMove!;
        _bank.Deposit(_boxIndex, _cursorSlot, pk);

        // Clear source game slot and immediately write back the save file so the
        // bank and the save on disk stay in sync. Without writeback the slot is only
        // cleared in memory; if the user never manually saves, the Pokémon appears
        // in both the save file and the bank (apparent clone).
        if (_session.ActiveSave != null && _session.PendingSourceBox == -2)
        {
            // Source was a party slot
            _session.ActiveSave.SetPartySlotAtIndex(
                _session.ActiveSave.BlankPKM,
                _session.PendingSourceSlot);
            _session.PendingSourceBox  = -1;
            _session.PendingSourceSlot = -1;
        }
        if (_session.ActiveSave != null && _session.PendingSourceBox >= 0)
        {
            _session.ActiveSave.SetBoxSlotAtIndex(
                _session.ActiveSave.BlankPKM,
                _session.PendingSourceBox,
                _session.PendingSourceSlot);
            _session.PendingSourceBox  = -1;
            _session.PendingSourceSlot = -1;

            if (!string.IsNullOrEmpty(_session.ActiveSaveFileUri))
            {
                var data = _session.ActiveSave.Write().ToArray();
                _ = Task.Run(async () =>
                {
                    try { await new FileService().WriteBackAsync(data, _session.ActiveSaveFileUri); }
                    catch { /* writeback failure is non-fatal; user can still manually save */ }
                });
            }
        }

        _session.PendingMove = null;
        LoadBox(_boxIndex);
        UpdateModeBanner();
    }

    private void EnterWithdrawMode()
    {
        _movePk         = _currentSlots[_cursorSlot]!.Clone();
        _moveSourceBox  = _boxIndex;
        _moveSourceSlot = _cursorSlot;
        _moveMode       = true;
        BankCanvas.InvalidateSurface();
        UpdateModeBanner();
    }

    private void ExecuteWithdrawDrop()
    {
        // Move grabbed Pokémon to the cursor slot (current box).
        // Clear or swap the original source slot, which may be in a different box.
        var destPk = _currentSlots[_cursorSlot];
        if (destPk?.Species > 0)
            _bank.Deposit(_moveSourceBox, _moveSourceSlot, destPk); // swap dest �?source
        else
            _bank.ClearSlot(_moveSourceBox, _moveSourceSlot);       // vacate source
        _bank.Deposit(_boxIndex, _cursorSlot, _movePk!);            // place grabbed �?dest
        CancelMoveMode();
        LoadBox(_boxIndex);
    }

    private void CancelMoveMode()
    {
        _moveMode       = false;
        _movePk         = null;
        _moveSourceBox  = -1;
        _moveSourceSlot = -1;
        BankCanvas.InvalidateSurface();
        UpdateModeBanner();
    }

    private void SelectSlot(int slot)
    {
        _selectedSlot = slot;
        BankCanvas.InvalidateSurface();
    }

    private void DeselectSlot()
    {
        _selectedSlot = -1;
        BankCanvas.InvalidateSurface();
    }

    // ──────────────────────────────────────────────
    //  Bank �?Game swap
    // ──────────────────────────────────────────────

    private async Task SwapToGame(int exitDir = 0)
    {
        // If in withdraw move mode, carry Pokémon to game
        if (_moveMode && _movePk != null)
        {
            _session.PendingMove       = _movePk;
            _session.PendingSourceBox  = _boxIndex;
            _session.PendingSourceSlot = _moveSourceSlot;
            _session.PendingFromBank   = true;
            _moveMode = false;
            _movePk   = null;
        }

        if (exitDir != 0) _session.BankSlideDir = exitDir;

        double exitX = _session.BankSlideDir < 0 ? -500 : 500;
        await RootGrid.TranslateToAsync(exitX, 0, 260, Easing.CubicInOut);
        RootGrid.TranslationX = 0;
        await Shell.Current.GoToAsync("..", false);
    }

    // ──────────────────────────────────────────────
    //  UI helpers
    // ──────────────────────────────────────────────

    private void UpdateModeBanner()
    {
        if (_session.PendingMove != null && !_session.PendingFromBank)
        {
            var name = _session.PendingMove.Species < _strings.specieslist.Length
                ? _strings.specieslist[_session.PendingMove.Species] : "?";
            ModeLabel.Text      = $"存入中：{name}  Lv.{_session.PendingMove.CurrentLevel}  - 按 A 放入";
            ModeLabel.TextColor = Color.FromArgb("#44DDAA");
            ModeBanner.BackgroundColor = Color.FromArgb("#0A1E0E");
            ModeBanner.IsVisible = true;
        }
        else if (_moveMode && _movePk != null)
        {
            var name = _movePk.Species < _strings.specieslist.Length
                ? _strings.specieslist[_movePk.Species] : "?";
            ModeLabel.Text      = $"取出中：{name}  Lv.{_movePk.CurrentLevel}  - 用 L1/R1 带到箱子页，B 取消";
            ModeLabel.TextColor = Color.FromArgb("#DDAA44");
            ModeBanner.BackgroundColor = Color.FromArgb("#1E1A0A");
            ModeBanner.IsVisible = true;
        }
        else
        {
            ModeBanner.IsVisible = false;
        }
    }

    private void UpdateHoverLabel()
    {
        var pk = _cursorSlot < _currentSlots.Length ? _currentSlots[_cursorSlot] : null;
        if (pk?.Species > 0)
        {
            var name = pk.Species < _strings.specieslist.Length ? _strings.specieslist[pk.Species] : "?";
            HoverLabel.Text = $"#{pk.Species:000} {name}  Lv.{pk.CurrentLevel}";
        }
        else
        {
            HoverLabel.Text = "空格子";
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Bank box management menu
    // ──────────────────────────────────────────────────────────────────────────

    private void OpenBankManageMenu()
    {
        var name = LocalizeBankName(_boxIndex < _bank.Boxes.Count ? _bank.Boxes[_boxIndex].Name : $"银行 {_boxIndex + 1}");
        _secondary.ShowBankManageMenu(_boxIndex, name, _bank.Boxes.Count, OnBankManageAction);
    }

    // Phone-accessible manage menu: tapping the box name in the header
    private async void OnBoxHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (_secondary.IsAvailable) return; // Thor uses second-screen menu
        var action = await DisplayActionSheetAsync("管理箱子", "取消", null,
            "重命名箱子", "新建箱子", "删除箱子");
        switch (action)
        {
            case "重命名箱子": OnBankManageAction("rename"); break;
            case "新建箱子": OnBankManageAction("add"); break;
            case "删除箱子": OnBankManageAction("remove"); break;
        }
    }

    private void OnBankManageAction(string action)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            switch (action)
            {
                case "rename":
                    await PromptRenameBox();
                    break;

                case "add":
                    _bank.CreateBox($"银行 {_bank.Boxes.Count + 1}");
                    _boxIndex = _bank.Boxes.Count - 1;
                    LoadBox(_boxIndex);
                    break;

                case "remove":
                    await PromptRemoveBox();
                    break;

                // "close" �?do nothing, menu already dismissed
            }
        });
    }

    private async Task PromptRemoveBox()
    {
        if (_bank.Boxes.Count <= 1)
        {
            await DisplayAlertAsync("无法删除", "至少要保留一个箱子。", "OK");
            return;
        }

        bool isEmpty = _bank.IsBoxEmpty(_boxIndex);
        string boxName = _bank.Boxes[_boxIndex].Name;

        if (!isEmpty)
        {
            bool confirmed = await DisplayAlertAsync(
                "删除箱子",
                $"\"{boxName}\" contains Pokémon. They will be permanently deleted. Continue?",
                "删除", "取消");
            if (!confirmed) return;
        }

        _bank.RemoveBox(_boxIndex);
        _boxIndex = Math.Max(0, _boxIndex - 1);
        LoadBox(_boxIndex);
    }

    private async Task PromptRenameBox()
    {
        var current = _boxIndex < _bank.Boxes.Count ? _bank.Boxes[_boxIndex].Name : "";
        var name = await DisplayPromptAsync("重命名箱子", "输入新箱子名称：", initialValue: current, maxLength: 24);
        if (name is null) return;
        _bank.RenameBox(_boxIndex, name.Trim().Length > 0 ? name.Trim() : current);
        BankNameLabel.Text = _bank.Boxes[_boxIndex].Name;
    }

    // 老版本保存下来的箱名是 "Bank N"，显示时统一转成中文
    internal static string LocalizeBankName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? string.Empty;
        if (raw.StartsWith("Bank ")) return "银行 " + raw.Substring(5);
        return raw;
    }

}