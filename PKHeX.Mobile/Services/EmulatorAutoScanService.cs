using System.Text.RegularExpressions;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 全自动模拟器存档发现。
///
/// 不依赖写死某几个模拟器的路径（那样永远会漏），而是：
/// 1. 拿到「所有文件访问权限」后，直接走文件系统；
/// 2. 覆盖 <c>Android/data</c>（Citra / Azahar / AzaharPlus / Eden / melonDS / Pizza Boy / mGBA / DraStic / My Boy … 全都在这下面）；
/// 3. 覆盖常见顶层目录（DraStic / melonDS / RetroArch / myboy / Documents / Download / 存档 …）；
/// 4. 只按「扩展名 + 文件名 + 体积」筛候选，最终由 SaveUtil 用**内容**判断是不是存档，
///    所以新增/没见过的模拟器也能被识别出来。
/// </summary>
public static class EmulatorAutoScanService
{
    private static readonly HashSet<string> SaveExts =
    [
        ".sav", ".dsv", ".ds1", ".ds2", ".srm", ".sra", ".rtc", ".fla",
        ".bin", ".dat", ".gci", ".mpk", ".xps", ".psu", ".cpt", ".bkp",
        ".bak", ".state", ".ss0", ".ss1", ".ss2", ".ss3",
    ];

    /// <summary>顶层/子目录名里出现这些词，就算"像模拟器目录"，会往里多走几层。</summary>
    private static readonly string[] Keywords =
    [
        "dra", "melon", "desmume", "nds", "ds", "gba", "gbc", "mgba", "myboy", "pizza",
        "retro", "emu", "citra", "azahar", "lime", "eden", "skyline", "yuzu", "dolphin",
        "save", "sav", "backup", "存档", "模拟", "游戏", "rom", "nds4droid", "drastic",
    ];

    private static readonly Regex NumberedSav = new(@"^\d{1,4}\.sav$", RegexOptions.IgnoreCase);

    private const int MaxVisited = 60000;
    private const long MinSize = 1 * 1024;
    private const long MaxSize = 40L * 1024 * 1024;

    private static int _visited;

    /// <summary>返回候选存档文件的绝对路径列表（已按修改时间倒序，新的在前）。</summary>
    public static async Task<List<string>> FindCandidatesAsync(CancellationToken ct = default)
    {
        var result = new List<(string Path, DateTime Modified)>();
        if (!AllFilesAccess.IsGranted())
        {
            ScanReport.Add("未授予「所有文件访问权限」，已跳过全自动模拟器扫描");
            return [];
        }

        await Task.Run(() =>
        {
            try
            {
                var root = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath
                           ?? "/storage/emulated/0";

                var targets = new List<(string Dir, int Depth)>();

                // 所有模拟器的主战场
                targets.Add((Path.Combine(root, "Android", "data"), 12));
                targets.Add((Path.Combine(root, "Android", "media"), 8));

                // 常见顶层目录（DraStic/melonDS/RetroArch/myboy/Documents/Download…）
                foreach (var dir in SafeEnumerateDirectories(root))
                {
                    var name = Path.GetFileName(dir).ToLowerInvariant();
                    if (IsLikelyEmulatorDir(name))
                        targets.Add((dir, 6));
                }

                foreach (var extra in new[] { "Documents", "Download", "Downloads", "存档", "saves" })
                {
                    var dir = Path.Combine(root, extra);
                    if (SafeExists(dir)) targets.Add((dir, 5));
                }

                _visited = 0;
                foreach (var (dir, depth) in targets)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!SafeExists(dir)) continue;
                    Walk(dir, depth, result, ct);
                    if (_visited >= MaxVisited) break;
                }

                ScanReport.Add($"自动扫描：遍历 {_visited} 个条目，命中候选 {result.Count} 个文件");
            }
            catch (Exception ex)
            {
                ScanReport.Add($"自动扫描异常：{ex.GetType().Name} {ex.Message}");
            }
        }, ct);

        return [.. result.OrderByDescending(x => x.Modified).Select(x => x.Path)];
    }

    private static void Walk(string dir, int depth, List<(string, DateTime)> files, CancellationToken ct)
    {
        if (depth <= 0 || ct.IsCancellationRequested) return;
        if (_visited >= MaxVisited) return;

        List<string> children;
        try { children = [.. Directory.EnumerateFileSystemEntries(dir)]; }
        catch { return; }

        foreach (var child in children)
        {
            if (ct.IsCancellationRequested) return;

            _visited++;
            var name = Path.GetFileName(child);

            bool isDir;
            try { isDir = File.GetAttributes(child).HasFlag(FileAttributes.Directory); }
            catch { continue; }

            if (isDir)
            {
                if (ShouldSkipDir(name)) continue;
                Walk(child, depth - 1, files, ct);
                continue;
            }

            if (!IsCandidateName(name)) continue;

            long size;
            DateTime modified;
            try
            {
                var info = new FileInfo(child);
                size = info.Length;
                modified = info.LastWriteTime;
            }
            catch { continue; }

            if (size < MinSize || size > MaxSize) continue;
            files.Add((child, modified));
        }
    }

    private static bool ShouldSkipDir(string name)
    {
        var n = name.ToLowerInvariant();
        if (n is "cache" or "code_cache" or "no_backup" or "obb" or "tmp" or "temp") return true;
        if (n.StartsWith('.')) return true;
        // 明显的媒体/缓存目录，没必要进去
        if (n is "dcim" or "pictures" or "movies" or "music" or "thumbnails" or "logs") return true;
        return false;
    }

    private static bool IsCandidateName(string name)
    {
        if (name.Equals("main", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("00000001.main", StringComparison.OrdinalIgnoreCase)) return true;
        if (NumberedSav.IsMatch(name)) return true;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return SaveExts.Contains(ext);
    }

    private static bool IsLikelyEmulatorDir(string lowerName)
    {
        foreach (var k in Keywords)
            if (lowerName.Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return []; }
    }

    private static bool SafeExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }
}
