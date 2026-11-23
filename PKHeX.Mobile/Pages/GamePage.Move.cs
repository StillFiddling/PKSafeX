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
    private bool _staleNoticeOnceShown;

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

        var msg = dest?.Species > 0
            ? $"{MoveSpeciesName(pk)} Lv.{pk.CurrentLevel} 将从银行取出，放入{destName}。\n该位置现有 {MoveSpeciesName(dest)}，确认后会把它换回银行原格。"
            : $"{MoveSpeciesName(pk)} Lv.{pk.CurrentLevel} 将从银行取出，放入{destName}。";

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
        bool merged = false;
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
                await bank.SaveAsync();
            }

            _session.PendingSourceBox  = -1;
            _session.PendingSourceSlot = -1;
            _session.PendingFromBank   = false;
        }
        catch (Exception ex)
        {
            // 回滚：把目标格还原成取出前的样子（银行没动）
            var restore = dest?.Species > 0 ? dest : _sav.BlankPKM;
            if (_boxIndex == -1)
                _sav.SetPartySlotAtIndex(restore, _cursorSlot);
            else
                _sav.SetBoxSlotAtIndex(restore, _boxIndex, _cursorSlot);

            CancelMoveMode();
            LoadBox(_boxIndex);

            await DisplayAlertAsync("取出失败，已回退",
                $"写回存档失败，宝可梦仍留在银行，箱子没有变化。\n\n{ex.Message}", "OK");
            return;
        }
        finally
        {
            _moveExecuting = false;
        }

        CancelMoveMode();
        DeselectSlot();
        _previewSpecies = -1;
        LoadBox(_boxIndex);

        if (merged && !_staleNoticeOnceShown)
        {
            _staleNoticeOnceShown = true;
            await DisplayAlertAsync("已取出（含并最新进度）",
                "检测到你在游戏里已经保存过比软件更新的存档。\n本次操作只把这一格的改动合并进最新存档，你在游戏里的进度没有被覆盖。",
                "OK");
        }
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
