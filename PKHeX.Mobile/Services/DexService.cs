using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PKHeX.Core;

namespace PKHeX.Mobile.Services;

/// <summary>图鉴单元状态。</summary>
public enum DexState
{
    /// <summary>超出本作图鉴范围（如剑盾 400、LGPE 151），不计入分母。</summary>
    OutOfScope = 0,
    /// <summary>未见到：黑剪影。</summary>
    Unseen = 1,
    /// <summary>见到但未捕获：灰阶。</summary>
    Seen = 2,
    /// <summary>已捕获：彩色。</summary>
    Caught = 3,
}

/// <summary>单个存档的图鉴进度。</summary>
public sealed class DexProgress
{
    public string Uri { get; set; } = "";
    public string TrainerName { get; set; } = "";
    public GameVersion Version { get; set; } = GameVersion.Any;
    public int Generation { get; set; }

    /// <summary>本作图鉴上限（= SaveFile.MaxSpeciesID）。</summary>
    public int MaxSpecies { get; set; } = DexService.MaxSpecies;

    /// <summary>true = 数据读自存档自己的图鉴块；false = 走箱子扫描兜底。</summary>
    public bool HasSaveDex { get; set; }

    /// <summary>叠加了历史全局记录（旧版 dex_unlocked.json）。</summary>
    public bool HasLegacyOverlay { get; set; }

    /// <summary>兜底/异常说明，非 null 时页面顶部要给提示。</summary>
    public string? Note { get; set; }

    public HashSet<ushort> Caught { get; } = [];
    public HashSet<ushort> Seen   { get; } = [];

    public int CaughtCount => Caught.Count;
    public int SeenCount   => Caught.Count + Seen.Count;
}

/// <summary>
/// 图鉴进度：按存档（存档 URI）计算，读存档自己的图鉴位；读不到时用队伍+箱子兜底。
/// 旧版的全局 dex_unlocked.json 作为只读叠加层保留，可开关。
/// </summary>
public class DexService
{
    public const int MaxSpecies = 1025;

    private const string LegacyFileName = "dex_unlocked.json";
    private const string SnapshotDir    = "dex";
    private const string OverlayPref    = "dex_legacy_overlay";

    public static readonly (ushort Start, ushort End, string Name)[] Generations =
    [
        (  1, 151, "Kanto"),
        (152, 251, "Johto"),
        (252, 386, "Hoenn"),
        (387, 493, "Sinnoh"),
        (494, 649, "Unova"),
        (650, 721, "Kalos"),
        (722, 809, "Alola"),
        (810, 905, "Galar"),
        (906, 1025, "Paldea"),
    ];

    private static string LegacyPath => Path.Combine(FileSystem.AppDataDirectory, LegacyFileName);
    private static string SnapshotDirPath => Path.Combine(FileSystem.AppDataDirectory, SnapshotDir);

    private readonly HashSet<ushort> _legacy;

    public DexService()
    {
        _legacy = LoadLegacy();
        OverlayEnabled = Preferences.Default.Get(OverlayPref, true);
    }

    /// <summary>历史全局记录（跨存档累加、只有"有/没有"）。</summary>
    public bool OverlayEnabled { get; set; }

    public int LegacyCount => _legacy.Count;
    public bool HasLegacy  => _legacy.Count > 0;

    public DexProgress? Current { get; private set; }

    public void SetOverlay(bool enabled)
    {
        OverlayEnabled = enabled;
        Preferences.Default.Set(OverlayPref, enabled);
    }

    // ── 构建 ─────────────────────────────────────────────────────────────────

    /// <summary>解析一个存档的图鉴进度。</summary>
    public DexProgress Build(SaveFile sav, string uri)
    {
        var p = new DexProgress
        {
            Uri          = uri,
            TrainerName  = sav.OT,
            Version      = sav.Version,
            Generation   = sav.Generation,
            MaxSpecies   = Math.Clamp((int)sav.MaxSpeciesID, 1, MaxSpecies),
        };

        bool hasDex = sav.HasPokeDex;
        if (hasDex)
        {
            for (ushort s = 1; s <= p.MaxSpecies; s++)
            {
                if (sav.GetCaught(s)) p.Caught.Add(s);
                else if (sav.GetSeen(s)) p.Seen.Add(s);
            }

            // 一致性自检：队伍里明明有的物种，图鉴却说自己没抓到 → 图鉴块未初始化/不可靠。
            foreach (var pk in sav.PartyData)
            {
                if (pk.Species <= 0 || pk.Species > p.MaxSpecies) continue;
                if (!p.Caught.Contains((ushort)pk.Species)) { hasDex = false; break; }
            }
        }

        p.HasSaveDex = hasDex;

        if (!hasDex)
        {
            p.Caught.Clear();
            SeekFallbackScan(p, sav);
        }
        ApplyOverlay(p);
        p.Seen.ExceptWith(p.Caught);

        Current = p;
        SaveSnapshot(p);
        return p;
    }

    /// <summary>「扫描存档」兜底：把队伍与全部箱子里出现的物种标为已捕获，返回新增数量。</summary>
    public int Rescan(DexProgress p, SaveFile sav)
    {
        int before = p.CaughtCount;
        SeekFallbackScan(p, sav);
        if (p.HasSaveDex) p.Note = null;
        ApplyOverlay(p);
        p.Seen.ExceptWith(p.Caught);
        SaveSnapshot(p);
        return p.CaughtCount - before;
    }

    private static void SeekFallbackScan(DexProgress p, SaveFile sav)
    {
        foreach (var pk in sav.PartyData)
            AddCaught(p, pk.Species);

        for (int b = 0; b < sav.BoxCount; b++)
            foreach (var pk in sav.GetBoxData(b))
                AddCaught(p, pk.Species);

        p.Note = BuildFallbackNote(sav);
    }

    private static string BuildFallbackNote(SaveFile sav) => sav.HasPokeDex
        ? "该存档的图鉴数据与队伍对不上，已按箱子记录显示"
        : "该存档没有可读的图鉴数据，已按箱子记录显示";

    private static void AddCaught(DexProgress p, int species)
    {
        if (species <= 0 || species > p.MaxSpecies) return;
        p.Caught.Add((ushort)species);
        p.Seen.Remove((ushort)species);
    }

    private void ApplyOverlay(DexProgress p)
    {
        if (!OverlayEnabled || _legacy.Count == 0) return;

        foreach (var s in _legacy)
        {
            if (s > p.MaxSpecies) continue;
            if (p.Caught.Add(s))
            {
                p.Seen.Remove(s);
                p.HasLegacyOverlay = true;
            }
        }
    }

    // ── 查询 ─────────────────────────────────────────────────────────────────

    public static DexState StateOf(DexProgress? p, ushort species)
    {
        if (p is null) return DexState.Unseen;
        if (species > p.MaxSpecies) return DexState.OutOfScope;
        if (p.Caught.Contains(species)) return DexState.Caught;
        if (p.Seen.Contains(species))   return DexState.Seen;
        return DexState.Unseen;
    }

    public (int caught, int total)[] GetStatsByGen(DexProgress? p)
    {
        var result = new (int, int)[Generations.Length];
        for (int g = 0; g < Generations.Length; g++)
        {
            var (start, end, _) = Generations[g];
            int caught = 0, total = 0;
            int max = p?.MaxSpecies ?? MaxSpecies;
            for (ushort s = start; s <= end; s++)
            {
                if (s > max) break;
                total++;
                if (p is not null && p.Caught.Contains(s)) caught++;
            }
            result[g] = (caught, total);
        }
        return result;
    }

    // ── 存储 ─────────────────────────────────────────────────────────────────

    private static string SnapshotPath(string uri)
    {
        // 存档 URI 可能带中文/特殊字符，用短哈希做文件名。
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri)))[..12].ToLowerInvariant();
        return Path.Combine(SnapshotDirPath, $"{hash}.json");
    }

    private static void SaveSnapshot(DexProgress p)
    {
        if (string.IsNullOrEmpty(p.Uri)) return;
        try
        {
            Directory.CreateDirectory(SnapshotDirPath);
            var dto = new DexSnapshotDto
            {
                Version    = (int)p.Version,
                Generation = p.Generation,
                MaxSpecies = p.MaxSpecies,
                Trainer    = p.TrainerName,
                HasSaveDex = p.HasSaveDex,
                Caught     = p.Caught.OrderBy(x => x).ToList(),
                Seen       = p.Seen.OrderBy(x => x).ToList(),
            };
            File.WriteAllText(SnapshotPath(p.Uri), JsonSerializer.Serialize(dto));
        }
        catch { /* 快照只是辅助，失败不影响页面 */ }
    }

    private static HashSet<ushort> LoadLegacy()
    {
        try
        {
            if (!File.Exists(LegacyPath)) return [];
            var json = File.ReadAllText(LegacyPath);
            var list = JsonSerializer.Deserialize<List<ushort>>(json);
            if (list is { Count: > 0 }) return new HashSet<ushort>(list);
        }
        catch { }
        return [];
    }

    private sealed class DexSnapshotDto
    {
        public int Version { get; set; }
        public int Generation { get; set; }
        public int MaxSpecies { get; set; }
        public string Trainer { get; set; } = "";
        public bool HasSaveDex { get; set; }
        public List<ushort> Caught { get; set; } = [];
        public List<ushort> Seen { get; set; } = [];
    }
}
