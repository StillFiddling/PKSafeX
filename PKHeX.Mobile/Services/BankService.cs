using System.Text.Json;
using PKHeX.Core;

namespace PKHeX.Mobile.Services;

public class BankService
{
    public const int SlotsPerBox = 30;
    private const string FileName = "bank.json";

    /// <summary>
    /// 全应用唯一实例。银行是全局数据，任何页面都必须用它读写，
    /// 否则各页面各自持有一份快照，整份覆盖写会把别人刚存进去的条目抹掉。
    /// </summary>
    public static BankService Instance { get; } = new();

    private static string FilePath =>
        Path.Combine(FileSystem.AppDataDirectory, FileName);

    private List<BankBox> _boxes;
    public IReadOnlyList<BankBox> Boxes => _boxes;

    public BankService() => _boxes = Load();

    // ── Persistence ───────────────────────────────────────────────
    //
    // 写盘改为「先写 .tmp，备份旧文件为 .bak，再原子替换」，
    // 避免写到一半被杀进程导致 bank.json 截断、整库归零。
    // 所有写操作经同一把锁串行，调用顺序即落盘顺序。

    private static readonly SemaphoreSlim _ioLock = new(1, 1);

    /// <summary>最近一次保存失败的原因；成功时清空。</summary>
    public string? LastError { get; private set; }

#if DEBUG
    /// <summary>仅调试构建生效：强制让下一次保存失败，用来验证存入/取出里的容错分支。</summary>
    public static bool SimulateSaveFailure { get; set; }
#endif

    /// <summary>异步保存；不抛异常，失败只记诊断（普通 UI 操作使用）。</summary>
    public async Task SaveAsync() => await SaveCoreAsync().ConfigureAwait(false);

    /// <summary>
    /// 关键流程（存入/取出）必须用这个：返回是否真的落盘。
    /// 1.1.1 之前这里静默吞异常，调用方以为「银行已保存」，接着去清箱子源格，
    /// 结果宝可梦只活在内存里，重启即消失。
    /// </summary>
    public Task<bool> TrySaveAsync() => SaveCoreAsync();

    private async Task<bool> SaveCoreAsync()
    {
        await _ioLock.WaitAsync().ConfigureAwait(false);
        try
        {
#if DEBUG
            if (SimulateSaveFailure)
                throw new IOException("调试开关：模拟 bank.json 写入失败。");
#endif
            var json = JsonSerializer.Serialize(_boxes);

            var dir = Path.GetDirectoryName(FilePath);
            if (dir is not null) Directory.CreateDirectory(dir);

            var tmp = FilePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);

            try
            {
                if (File.Exists(FilePath))
                    File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            }
            catch { /* 备份失败不阻断主流程 */ }

            File.Move(tmp, FilePath, overwrite: true);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DiagLog.Add($"bank-save-failed {ex.GetType().Name}: {ex.Message}");
            try { if (File.Exists(FilePath + ".tmp")) File.Delete(FilePath + ".tmp"); } catch { }
            return false;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>即发即忘的保存（普通 UI 操作使用）。</summary>
    public void Save() => _ = SaveAsync();

    // ── Corruption warning ────────────────────────────────────────

    /// <summary>
    /// If bank.json was corrupt on last load, returns the path of the backup file
    /// and resets the flag so the warning is only shown once.
    /// Returns null if no corruption was detected.
    /// </summary>
    public static string? TakeCorruptionWarning()
    {
        var path = _corruptBackupPath;
        _corruptBackupPath = null;
        return path;
    }

    private static string? _corruptBackupPath;

    // ── Persistence ───────────────────────────────────────────────

    private static List<BankBox> Load()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                var json = File.ReadAllText(FilePath);
                var data = JsonSerializer.Deserialize<List<BankBox>>(json);
                if (data is { Count: > 0 }) return data;
            }
            catch
            {
                // 主文件损坏：先把坏文件留档，再尝试用上一次的完整备份恢复，
                // 避免用户看到「银行被清空」。
                try
                {
                    var backupPath = FilePath + ".bak";
                    File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);

                    if (File.Exists(backupPath))
                    {
                        var bakJson = File.ReadAllText(backupPath);
                        var bakData = JsonSerializer.Deserialize<List<BankBox>>(bakJson);
                        if (bakData is { Count: > 0 })
                        {
                            _corruptBackupPath = backupPath;
                            return bakData;
                        }
                    }

                    _corruptBackupPath = FilePath + ".corrupt";
                }
                catch { /* 备份失败 — 仍回退默认箱 */ }
            }
        }

        // 主文件不存在但备份存在：直接用备份恢复
        try
        {
            var backupPath = FilePath + ".bak";
            if (!File.Exists(FilePath) && File.Exists(backupPath))
            {
                var bakJson = File.ReadAllText(backupPath);
                var bakData = JsonSerializer.Deserialize<List<BankBox>>(bakJson);
                if (bakData is { Count: > 0 })
                {
                    _corruptBackupPath = backupPath;
                    return bakData;
                }
            }
        }
        catch { }

        return DefaultBoxes();
    }

    private static List<BankBox> DefaultBoxes()
        => Enumerable.Range(0, 5).Select(i => new BankBox { Name = $"Bank {i + 1}" }).ToList();

    // ── Box data ──────────────────────────────────────────────────

    /// <summary>Deserialise one bank box into PKM? array for rendering.</summary>
    public PKM?[] GetBoxData(int box)
    {
        var result = new PKM?[SlotsPerBox];
        if (box >= _boxes.Count) return result;
        var slots = _boxes[box].Slots;
        for (int i = 0; i < Math.Min(slots.Count, SlotsPerBox); i++)
            result[i] = slots[i]?.ToPKM();
        return result;
    }

    // ── Deposit / withdraw ────────────────────────────────────────

    /// <param name="persist">false 时不自动落盘，由调用方 await <see cref="SaveAsync"/> 统一提交。</param>
    public void Deposit(int box, int slot, PKM pk, bool persist = true)
    {
        EnsureBox(box);
        EnsureSlots(_boxes[box]);
        _boxes[box].Slots[slot] = BankSlot.FromPKM(pk);
        AutoExpand();
        if (persist) Save();
    }

    public void ClearSlot(int box, int slot, bool persist = true)
    {
        if (box >= _boxes.Count) return;
        EnsureSlots(_boxes[box]);
        _boxes[box].Slots[slot] = null;
        if (persist) Save();
    }

    public BankSlot? GetSlot(int box, int slot)
    {
        if (box >= _boxes.Count) return null;
        EnsureSlots(_boxes[box]);
        return slot < _boxes[box].Slots.Count ? _boxes[box].Slots[slot] : null;
    }

    /// <summary>Returns index of the first empty slot in the box (0 if box is full).</summary>
    public int FindFirstEmpty(int box)
    {
        if (box >= _boxes.Count) return 0;
        EnsureSlots(_boxes[box]);
        var idx = _boxes[box].Slots.FindIndex(s => s == null);
        return idx < 0 ? 0 : idx;
    }

    // ── Box management ────────────────────────────────────────────

    public void CreateBox(string name)
    {
        _boxes.Add(new BankBox { Name = name });
        Save();
    }

    public void RenameBox(int box, string name)
    {
        if (box >= _boxes.Count) return;
        _boxes[box].Name = name;
        Save();
    }

    public bool IsBoxEmpty(int box)
    {
        if (box >= _boxes.Count) return true;
        EnsureSlots(_boxes[box]);
        return _boxes[box].Slots.All(s => s == null);
    }

    public void RemoveBox(int box)
    {
        if (box >= _boxes.Count || _boxes.Count <= 1) return;
        _boxes.RemoveAt(box);
        Save();
    }

    // ── Helpers ───────────────────────────────────────────────────

    private void EnsureBox(int box)
    {
        while (_boxes.Count <= box)
            _boxes.Add(new BankBox { Name = $"Bank {_boxes.Count + 1}" });
    }

    private static void EnsureSlots(BankBox box)
    {
        while (box.Slots.Count < SlotsPerBox)
            box.Slots.Add(null);
    }

    private void AutoExpand()
    {
        var last = _boxes[^1];
        EnsureSlots(last);
        if (last.Slots.All(s => s != null))
            _boxes.Add(new BankBox { Name = $"Bank {_boxes.Count + 1}" });
    }
}

// ── Data model ─────────────────────────────────────────────────────

public class BankBox
{
    public string Name { get; set; } = "";
    public List<BankSlot?> Slots { get; set; } = [];
}

public class BankSlot
{
    public byte[]  Data        { get; set; } = [];
    public string  TypeExt     { get; set; } = "pk9"; // e.g. "pk9", "pa8", "pb7"
    public int     Species     { get; set; }
    public int     Level       { get; set; }
    public bool    IsShiny     { get; set; }
    public string  Nickname    { get; set; } = "";
    /// <summary>UTC timestamp when this slot was deposited (ISO 8601). Null for legacy entries.</summary>
    public string? DepositedAt { get; set; }

    public static BankSlot FromPKM(PKM pk) => new()
    {
        Data        = pk.Data.ToArray(),
        TypeExt     = pk.GetType().Name.ToLower(),
        Species     = pk.Species,
        Level       = pk.CurrentLevel,
        IsShiny     = pk.IsShiny,
        Nickname    = pk.Nickname,
        DepositedAt = DateTime.UtcNow.ToString("o"),
    };

    public PKM? ToPKM()
    {
        try
        {
            FileUtil.TryGetPKM(Data.AsMemory(), out var pk, ("." + TypeExt).AsSpan());
            return pk;
        }
        catch { return null; }
    }
}

/// <summary>银行数据没能落盘。调用方据此走「银行侧失败」的处理分支，不要把宝可梦从箱子里清掉。</summary>
public class BankSaveFailedException : Exception
{
    public BankSaveFailedException(string? message) : base(message ?? "银行数据写入失败") { }
}
