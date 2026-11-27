using PKHeX.Core;
using PKHeX.Mobile.Services;

namespace PKHeX.Mobile.Pages;

// Move-mode logic: grab, cancel, execute, swap to bank.
public partial class GamePage
{
    // ──────────────────────────────────────────────
    //  Move mode
    // ──────────────────────────────────────────────

    private void EnterMoveMode()
    {
        if (_sav is null) return;
        if (_cursorSlot >= _currentBox.Length) return;
        if (_currentBox[_cursorSlot].Species == 0) return;
        _movePk         = _currentBox[_cursorSlot].Clone();
        _moveSourceBox  = _boxIndex == -1 ? -2 : _boxIndex; // -2 = party, -1 = bank (reserved)
        _moveSourceSlot = _cursorSlot;
        _moveMode       = true;
        BoxCanvas.InvalidateSurface();
    }

    private void CancelMoveMode()
    {
        _moveMode = false;
        _movePk   = null;
        _session.PendingSourceBox = -1; // release bank reference if cancelled
        BoxCanvas.InvalidateSurface();
    }

    private bool _moveExecuting;

    private async void ExecuteMove()
    {
        if (_moveExecuting) return;
        if (_movePk is null || _sav is null) return;

        if (_moveSourceBox == -1)
        {
            await ExecuteBankWithdrawAsync();
            return;
        }

        // Box-to-box (or party) swap
        var destPk = _currentBox[_cursorSlot];

        if (_boxIndex == -1)
            _sav.SetPartySlotAtIndex(_movePk, _cursorSlot);
        else
            _sav.SetBoxSlotAtIndex(_movePk, _boxIndex, _cursorSlot);

        if (_moveSourceBox == -2)
            _sav.SetPartySlotAtIndex(destPk, _moveSourceSlot);
        else
            _sav.SetBoxSlotAtIndex(destPk, _moveSourceBox, _moveSourceSlot);

        CancelMoveMode();
        DeselectSlot();
        _previewSpecies = -1;
        LoadBox(_boxIndex);
    }

    // ──────────────────────────────────────────────
    //  从银行取出：确认 → 先写存档 → 再清银行
    // ──────────────────────────────────────────────

    private string MoveSpeciesName(PKM pk)
        => pk.Species > 0 && pk.Species < _strings.specieslist.Length
            ? _strings.specieslist[pk.Species]
            : "?";

    private async Task ExecuteBankWithdrawAsync()
    {
        var bank = BankService.Instance;
        var pk   = _movePk!;

        // 1) 形态转换检查（不通过则中止，银行完全不动）
        var target = pk;
        if (pk.GetType() != _sav!.PKMType)
        {
            var converted = EntityConverter.ConvertToType(pk, _sav.PKMType, out _);
            if (converted is null)
            {
                await DisplayAlertAsync("无法取出",
                    $"这只宝可梦（{pk.GetType().Name}）无法传送到当前游戏（{_sav.PKMType.Name}）。\n银行未做任何改动。",
                    "OK");
                CancelMoveMode();
                return;
            }
            target = converted;
        }

        var dest     = _currentBox[_cursorSlot];
        var destName = _boxIndex == -1
            ? $"队伍第 {_cursorSlot + 1} 位"
            : $"箱子 {_boxIndex + 1} 第 {_cursorSlot + 1} 格";

        // 问题3【严重】目标位置已有宝可梦：直接拦下。
        // 旧行为是把被顶出来的那只"换回银行原格"，看起来就是两只调换；
        // 用户定稿改为与存入同一套提示，换位置再放。银行与存档都不动。
        if (dest is not null && dest.Species > 0)
        {
            var empty   = FirstEmptySlotInBox();
            var hint    = _boxIndex == -1 ? "请更换一个位置" : "请更换一个空格";
            await DisplayAlertAsync("这个位置已有宝可梦",
                $"当前位置已有 {MoveSpeciesName(dest)}，{hint}", "好");

            if (empty >= 0 && empty != _cursorSlot)
            {
                _cursorSlot = empty;
                UpdateTopPanel();
                UpdateInfoBar();
                BoxCanvas.InvalidateSurface();
                _secondary.UpdateCursor(_cursorSlot, _selectedSlot, _moveMode, _movePk, _boxIndex);
                Haptic();   // S4：跳格给一次触觉反馈
            }

            RefreshCanvasAfterDialog();
            return;   // 保持取出模式，宝可梦仍在手上、银行未改
        }

        var msg = $"{MoveSpeciesName(pk)} Lv.{pk.CurrentLevel} 将从银行取出，放入{destName}。";

        msg += "\n\n确认后：先写回存档，成功后才清空银行原格。\n取消后：宝可梦原样留在银行，不做任何改动。";

        bool ok = await DisplayAlertAsync("确认取出？", msg, "确认取出", "取消");
        if (!ok)
        {
            // 取消：宝可梦仍在银行（内存与磁盘都没动）
            CancelMoveMode();
            LoadBox(_boxIndex);
            return;
        }

        _moveExecuting = true;
        _session.SaveWriteBusy = true;
        bool merged = false;
        bool bankClearFailed = false;
        try
        {
            // 2) 放入目标格，并安全写回：若磁盘存档比内存新（玩家在游戏里又存过），
            //    只把"放入这一格"合并到最新存档，绝不整份覆盖游戏进度。
            var result = await SaveSyncService.ApplyAndWriteAsync(_session, s =>
            {
                if (_boxIndex == -1)
                    s.SetPartySlotAtIndex(target, _cursorSlot);
                else
                    s.SetBoxSlotAtIndex(target, _boxIndex, _cursorSlot);
            });

            if (result is SaveWriteResult.NoUri or SaveWriteResult.StaleUnreadable)
                throw new InvalidOperationException(result == SaveWriteResult.NoUri
                    ? "找不到原存档文件位置，请改用「导出」。"
                    : "存档已被游戏更新且无法解析，请回首页重新加载存档后再试。");

            merged = result == SaveWriteResult.MergedIntoLatest;
            _sav = _session.ActiveSave ?? _sav;

            // 3) 存档已落盘，才处理银行来源槽
            var srcBox  = _session.PendingSourceBox;
            var srcSlot = _session.PendingSourceSlot;
            if (srcBox >= 0)
            {
                if (dest?.Species > 0)
                    bank.Deposit(srcBox, srcSlot, dest, persist: false); // 目标格原来那只换回银行
                else
                    bank.ClearSlot(srcBox, srcSlot, persist: false);

                // 1.1.1 R3：银行这一步失败不能让异常冒到 catch——那里写的是
                // 「箱子没有变化」，而此刻存档其实已经写成功了，是假话。
                if (!await bank.TrySaveAsync())
                {
                    // 内存恢复成「银行还留着这只」，与磁盘保持一致；
                    // 用取出的原始 pk（不是形态转换后的 target），字节才不变。
                    bank.Deposit(srcBox, srcSlot, pk, persist: false);
                    bankClearFailed = true;
                }
            }

            _session.PendingSourceBox  = -1;
            _session.PendingSourceSlot = -1;
            _session.PendingFromBank   = false;
        }
        catch (Exception ex)
        {
            // 内存存档已由 SaveSyncService 回滚，但回滚会换新的 SaveFile 实例，
            // 这里必须把 _sav 重新指向会话里的那份，否则界面会继续画被改脏的旧对象
            _sav = _session.ActiveSave ?? _sav;

            CancelMoveMode();
            LoadBox(_boxIndex);

            await DisplayAlertAsync("取出失败，已回退",
                $"写回存档失败，宝可梦仍留在银行，箱子没有变化。\n\n{ex.Message}", "OK");
            RefreshCanvasAfterDialog();
            return;
        }
        finally
        {
            _moveExecuting = false;
            _session.SaveWriteBusy = false;
        }

        CancelMoveMode();
        DeselectSlot();
        _previewSpecies = -1;
        LoadBox(_boxIndex);

        if (bankClearFailed)
        {
            await DisplayAlertAsync("已取出，但仓库没清理",
                $"宝可梦已写入{destName}，存档写回成功。\n\n"
                + "软件仓库（bank.json）保存失败，银行原格仍留着同一只的副本。"
                + "请到银行页把那一格删掉，避免重复。",
                "OK");
            RefreshCanvasAfterDialog();
            return;
        }

        if (merged)
        {
            // test11 问题7：合并本身是正常兜底，不该用弹窗打断，改成轻提示
            Toast("已合并到最新存档");
            RefreshCanvasAfterDialog();
        }
    }

    /// <summary>本次箱子里第一个空格的下标；没有空格返回 -1。</summary>
    private int FirstEmptySlotInBox()
    {
        for (int i = 0; i < _currentBox.Length; i++)
        {
            var pk = _currentBox[i];
            if (pk is null || pk.Species == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// 弹窗遮罩撤下后补一次重绘：弹窗打开前调用的 LoadBox 重绘会被遮罩盖住，
    /// 遮罩撤走时 Skia 不会自动重画，会留下半截格子的残影。
    /// </summary>
    private void RefreshCanvasAfterDialog()
    {
        BoxCanvas.InvalidateSurface();
        Dispatcher.Dispatch(async () =>
        {
            await Task.Delay(40);
            BoxCanvas.InvalidateSurface();
        });
    }

    private async Task SwapToBank(int dir)
    {
        if (_moveMode && _movePk != null)
        {
            _session.PendingMove     = _movePk;
            _session.PendingFromBank = _moveSourceBox == -1;
            if (!_session.PendingFromBank)
            {
                _session.PendingSourceBox  = _moveSourceBox;
                _session.PendingSourceSlot = _moveSourceSlot;
            }
            _moveMode = false;
            _movePk   = null;
        }

        _session.BankSlideDir = dir;
        await Shell.Current.GoToAsync(nameof(BankPage), false);
    }
}
