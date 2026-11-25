using PKHeX.Core;

namespace PKHeX.Mobile.Services;

/// <summary>
/// Singleton service that owns all mutable cross-page application state,
/// replacing the static properties that previously lived on <c>App</c>.
/// </summary>
public class SessionState
{
    /// <summary>The currently loaded save file, set by MainPage after a successful parse.</summary>
    public SaveFile? ActiveSave { get; set; }

    /// <summary>All save entries discovered by the last directory scan.</summary>
    public List<SaveEntry> LoadedSaves { get; set; } = [];

    /// <summary>Original filename of the loaded save, used for export.</summary>
    public string ActiveSaveFileName { get; set; } = "save.bin";

    /// <summary>SAF content URI of the loaded save, used for write-back.</summary>
    public string ActiveSaveFileUri { get; set; } = "";

    /// <summary>
    /// 加载（或上次写回）时磁盘存档内容的 SHA-256 指纹。
    /// 写回前用它判断文件是否已被游戏/其它程序更新，避免覆盖玩家进度。
    /// </summary>
    public string ActiveSaveFingerprint { get; set; } = "";

    /// <summary>已经提示过"存档被外部更新"的那个指纹，避免重复弹窗。</summary>
    public string PromptedStaleFingerprint { get; set; } = "";

    // ── Cross-page Pokémon move (box ↔ bank) ──────────────────────────────────

    /// <summary>PKM currently being carried across a bank/box swap.</summary>
    public PKM? PendingMove { get; set; }

    /// <summary>Box index the move originated from (-1 = bank).</summary>
    public int PendingSourceBox { get; set; } = -1;

    /// <summary>Slot index the move originated from.</summary>
    public int PendingSourceSlot { get; set; } = -1;

    /// <summary>True when the move originated from the bank (withdraw), false when from a game box (deposit).</summary>
    public bool PendingFromBank { get; set; }

    /// <summary>Direction of the last bank swap: -1 = L1 (bank enters from left), +1 = R1 (bank enters from right).</summary>
    public int BankSlideDir { get; set; } = -1;

    /// <summary>Set to true by SettingsPage/WelcomePage when watched directories change so MainPage re-scans on next appearance.</summary>
    public bool RescanNeeded { get; set; } = true;

    /// <summary>
    /// 正在把存档写回磁盘（存入/取出/编辑保存）。
    /// 为 true 时禁止切换存档，避免"写进了旧存档、当前存档已经换了"。
    /// </summary>
    public bool SaveWriteBusy { get; set; }

    // ── 待增量加入的路径（管理页/设置页写入，首页消费）──────────────────────

    private static readonly List<(string Uri, bool IsFile)> _pendingAdds = [];

    /// <summary>由管理页等拿不到 SessionState 实例的页面置位，首页取用后清空。</summary>
    public static bool PendingFullRescan { get; set; }

    /// <summary>登记一个需要「只读这一个」的路径，避免为此触发全量重扫。</summary>
    public static void RequestAdd(string uri, bool isFile)
    {
        lock (_pendingAdds)
        {
            if (!_pendingAdds.Any(p => p.Uri == uri && p.IsFile == isFile))
                _pendingAdds.Add((uri, isFile));
        }
    }

    /// <summary>取出并清空待加入的路径。</summary>
    public static List<(string Uri, bool IsFile)> TakePendingAdds()
    {
        lock (_pendingAdds)
        {
            var copy = _pendingAdds.ToList();
            _pendingAdds.Clear();
            return copy;
        }
    }

    public static int PendingAddCount
    {
        get { lock (_pendingAdds) return _pendingAdds.Count; }
    }
}
