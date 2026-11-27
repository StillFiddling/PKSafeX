using PKHeX.Core;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 存档切换：点左下角训练家/存档名后，把当前编辑上下文换成另一份已导入存档。
/// 箱子页与银行页共用，保证"切换"只有一套语义。
/// </summary>
public static class SaveSwitchService
{
    /// <summary>当前存档在列表里对应的条目（可能为空：例如通过单文件添加但未进列表）。</summary>
    public static SaveEntry? ActiveEntry(SessionState session)
        => session.LoadedSaves.FirstOrDefault(e => e.FileUri == session.ActiveSaveFileUri);

    public static bool IsActive(SessionState session, SaveEntry entry)
        => entry.FileUri == session.ActiveSaveFileUri;

    /// <summary>
    /// 执行切换（不含 UI）：解析目标存档 → 更新 Session → 清跨页移动状态 → 重设写回基线。
    /// 返回 null 表示成功，否则是给用户看的失败原因。
    /// </summary>
    public static async Task<string?> SwitchAsync(SessionState session, SaveEntry target)
    {
        if (session.SaveWriteBusy) return "正在写入存档，请稍后再切换。";
        if (IsActive(session, target)) return null;

        SaveFile? sav;
        try
        {
            // test11 问题7 P1：同 LoadSaveAsync，优先用磁盘真实字节，读不到才回落缓存条目
            byte[] bytes = target.RawData.ToArray();
            try
            {
                var disk = await new FileService().ReadBackAsync(target.FileUri);
                if (disk is { Length: > 0 }) bytes = disk;
            }
            catch { /* 读不到就沿用缓存 */ }

            sav = await Task.Run(() =>
            {
                var data = bytes.ToArray(); // SwishCrypto 会原地解密，必须拷贝
                SaveUtil.TryGetSaveFile(data, out var s);
                return s;
            });
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }

        if (sav is null)
            return $"无法解析「{target.FileName}」，可能不是受支持的存档格式。";

        // 清掉"带走中"的宝可梦：银行原格本来就没动，清掉只是丢选中，不会丢宝可梦
        session.PendingMove       = null;
        session.PendingSourceBox  = -1;
        session.PendingSourceSlot = -1;
        session.PendingFromBank   = false;

        session.ActiveSave            = sav;
        session.ActiveSaveFileName    = target.FileName;
        session.ActiveSaveFileUri     = target.FileUri;
        session.ActiveSaveFingerprint = "";
        SaveSyncService.CaptureBaseline(session, sav);

        return null;
    }

    /// <summary>记住某份存档上次停留的箱号（仅内存，不落盘）。</summary>
    public static int RecallBoxIndex(string fileUri, int boxCount)
    {
        if (string.IsNullOrEmpty(fileUri) || boxCount <= 0) return 0;
        var idx = BoxIndexStore.Get(fileUri);
        return idx >= 0 && idx < boxCount ? idx : 0;
    }

    public static void RememberBoxIndex(string fileUri, int boxIndex)
    {
        if (string.IsNullOrEmpty(fileUri) || boxIndex < 0) return;
        BoxIndexStore.Set(fileUri, boxIndex);
    }

    /// <summary>会话级箱号记忆。</summary>
    private static class BoxIndexStore
    {
        private static readonly Dictionary<string, int> _map = [];
        public static int Get(string key) { lock (_map) return _map.TryGetValue(key, out var v) ? v : -1; }
        public static void Set(string key, int box) { lock (_map) _map[key] = box; }
    }
}
