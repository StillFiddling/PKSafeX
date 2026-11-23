namespace PKHeX.Mobile.Services;

public record SaveEntry(
    string FileUri,
    string FileName,
    string DirectoryUri,
    PKHeX.Core.GameVersion Version,
    int Generation,
    string TrainerName,
    string TrainerID,
    string PlayTime,
    int BoxCount,
    int SlotCount,
    byte[] RawData,
    DateTimeOffset LastModified);

public class SaveDirectoryService
{
    /// <summary>
    /// SAV4HGSS.Version always returns HGSS. Scan all party + all boxes and use
    /// a majority vote to determine whether the save is HeartGold or SoulSilver.
    /// A first-match probe is unreliable when traded Pokémon from the other version
    /// appear earlier in the party than native ones.
    /// </summary>
    private static PKHeX.Core.GameVersion ResolveVersion(PKHeX.Core.SaveFile sav)
    {
        if (sav.Version != PKHeX.Core.GameVersion.HGSS) return sav.Version;
        return ResolveHGSS(sav);
    }

    internal static PKHeX.Core.GameVersion ResolveHGSS(PKHeX.Core.SaveFile sav)
    {
        int hg = 0, ss = 0;
        foreach (var pk in sav.PartyData)
        {
            if (pk.Species == 0) continue;
            if (pk.Version == PKHeX.Core.GameVersion.HG) hg++;
            else if (pk.Version == PKHeX.Core.GameVersion.SS) ss++;
        }
        for (int box = 0; box < sav.BoxCount; box++)
        {
            foreach (var pk in sav.GetBoxData(box))
            {
                if (pk.Species == 0) continue;
                if (pk.Version == PKHeX.Core.GameVersion.HG) hg++;
                else if (pk.Version == PKHeX.Core.GameVersion.SS) ss++;
            }
        }
        if (hg == 0 && ss == 0) return PKHeX.Core.GameVersion.HGSS; // empty save, can't determine
        return ss > hg ? PKHeX.Core.GameVersion.SS : PKHeX.Core.GameVersion.HG;
    }

    private const string PrefKey      = "watched_dirs";
    private const string FilePrefKey  = "watched_files";
    private const string EdenRootKey  = "eden_roots";
    private const char   Sep          = '|';

    /// <summary>这些扩展名才可能是存档（含无扩展名的 3DS main）。</summary>
    private static readonly HashSet<string> SaveExtensions =
        [".sav", ".dsv", ".srm", ".bin", ".dat", ".gci", ".bak", ".main", ".dsv.bak", ""];

    /// <summary>递归扫描的最大目录深度（Citra/Eden 的存档藏在 title/…/data 下）。</summary>
    private const int MaxDepth = 12;

    /// <summary>直读扫描的保险上限，避免在模拟器大目录里卡死。</summary>
    private const int MaxPathCandidates = 20000;
    private const long MinSaveBytes = 1 * 1024;
    private const long MaxSaveBytes = 64L * 1024 * 1024;

    private static readonly string[] SkipDirNames =
        ["cache", "caches", "shadercache", "textures", "texcache", "roms", "rom", "logs", "log", "crash", "tmp"];

    /// <summary>
    /// 候选判定：有扩展名的按白名单；无扩展名的只认 3DS 的 <c>main</c>。
    /// 3DS/NDS 模拟器目录里无扩展名文件成千上万，全当候选会把刷新拖到几分钟。
    /// </summary>
    internal static bool IsCandidateName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext.Length == 0) return name.Equals("main", StringComparison.OrdinalIgnoreCase);
        return SaveExtensions.Contains(ext);
    }

    private static bool ShouldSkipDir(string name)
    {
        if (name.Length == 0 || name.StartsWith('.')) return name.Length > 0;
        var lower = name.ToLowerInvariant();
        return SkipDirNames.Contains(lower);
    }


    public List<string> GetWatchedDirectories()
    {
        var raw = Preferences.Default.Get(PrefKey, "");
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return [.. raw.Split(Sep, StringSplitOptions.RemoveEmptyEntries)];
    }

    public void AddDirectory(string uri)
    {
        var dirs = GetWatchedDirectories();
        if (dirs.Contains(uri)) return;
        dirs.Add(uri);
        Preferences.Default.Set(PrefKey, string.Join(Sep, dirs));
    }

    public void RemoveDirectory(string uri)
    {
        var dirs = GetWatchedDirectories();
        dirs.Remove(uri);
        Preferences.Default.Set(PrefKey, string.Join(Sep, dirs));
    }

    public List<string> GetWatchedFiles()
    {
        var raw = Preferences.Default.Get(FilePrefKey, "");
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return [.. raw.Split(Sep, StringSplitOptions.RemoveEmptyEntries)];
    }

    public void AddFile(string uri)
    {
        var files = GetWatchedFiles();
        if (files.Contains(uri)) return;
        files.Add(uri);
        Preferences.Default.Set(FilePrefKey, string.Join(Sep, files));
    }

    public void RemoveFile(string uri)
    {
        var files = GetWatchedFiles();
        files.Remove(uri);
        Preferences.Default.Set(FilePrefKey, string.Join(Sep, files));
    }

    public List<string> GetEdenRoots()
    {
        var raw = Preferences.Default.Get(EdenRootKey, "");
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return [.. raw.Split(Sep, StringSplitOptions.RemoveEmptyEntries)];
    }

    public void AddEdenRoot(string uri)
    {
        var roots = GetEdenRoots();
        if (roots.Contains(uri)) return;
        roots.Add(uri);
        Preferences.Default.Set(EdenRootKey, string.Join(Sep, roots));
    }

    public void RemoveEdenRoot(string uri)
    {
        var roots = GetEdenRoots();
        roots.Remove(uri);
        Preferences.Default.Set(EdenRootKey, string.Join(Sep, roots));
    }

    public async Task<List<SaveEntry>> ScanAllAsync()
    {
        ScanReport.Reset();
        var result = new List<SaveEntry>();

        var dirs = GetWatchedDirectories();
        ScanReport.Add($"监视目录：{dirs.Count} 个");
        foreach (var dir in dirs)
            result.AddRange(await ScanDirectoryOrPathAsync(dir));

        var files = GetWatchedFiles();
        if (files.Count > 0)
        {
            ScanReport.Add($"单独添加的文件：{files.Count} 个");
            foreach (var file in files)
            {
                var entry = await ScanFileAsync(file);
                if (entry != null) result.Add(entry);
            }
        }

        var roots = GetEdenRoots();
        if (roots.Count > 0)
            ScanReport.Add($"模拟器根目录：{roots.Count} 个");
        foreach (var root in roots)
            result.AddRange(await ScanEdenRootAsync(root));

        // 全自动扫描：默认关闭，只有设置里打开开关且已授权才执行
        if (Microsoft.Maui.Storage.Preferences.Default.Get("auto_scan_all", false))
        {
            try
            {
                var autoCandidates = await EmulatorAutoScanService.FindCandidatesAsync();
                foreach (var path in autoCandidates)
                {
                    var entry = await ScanLocalPathAsync(path);
                    if (entry != null) result.Add(entry);
                }
            }
            catch (Exception ex)
            {
                ScanReport.Add($"自动扫描失败：{ex.GetType().Name} {ex.Message}");
            }
        }

        // 1) 内容完全相同的文件只留一份（同一存档的多份拷贝）
        var seenHash = new HashSet<string>();
        result.RemoveAll(e => !seenHash.Add(Fingerprint(e.RawData)));

        // 2) 同一目录下「同一存档 + 它的备份」只留最新那份。
        //    不同目录里的同名文件（例如两个模拟器各自一个 main）都保留；
        //    同一训练家的多份不同进度存档也全都保留（旧版会把它们误删，只剩 1 个）。
        var byPath = new Dictionary<(string, string), SaveEntry>();
        foreach (var e in result)
        {
            var key = (NormalizeDir(e.DirectoryUri), StemOf(e.FileName));
            if (!byPath.TryGetValue(key, out var existing) || e.LastModified > existing.LastModified)
                byPath[key] = e;
        }
        result = [.. byPath.Values];

        ScanReport.Add($"识别出 {result.Count} 个存档");
        return result;
    }

    private static string Fingerprint(byte[] data)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

    /// <summary>SAF 子文件的 docId 里去掉最后一段，得到它所在目录（相对根）。</summary>
    private static string? ParentDocIdOf(string? childDocId)
    {
        if (string.IsNullOrEmpty(childDocId)) return null;
        var idx = childDocId.LastIndexOf('/');
        return idx > 0 ? childDocId[..idx] : null;
    }

    private static string NormalizeDir(string dirUri) => (dirUri ?? string.Empty).TrimEnd('/', '|');

    /// <summary>去掉扩展名与备份标记，用于判断两个文件是不是「同一存档 + 备份」。</summary>
    private static string StemOf(string fileName)
    {
        var name = (fileName ?? string.Empty).ToLowerInvariant().Replace(".bak", string.Empty);
        var ext = Path.GetExtension(name);
        if (ext is ".sav" or ".dsv" or ".srm" or ".bin" or ".dat" or ".gci" or ".main")
            name = name[..^ext.Length];
        // main (1) / main_1 这类副本命名，也按同一份处理
        while (name.EndsWith(")") && name.Contains('('))
        {
            int i = name.LastIndexOf('(');
            var mid = name[(i + 1)..^1];
            if (mid.Length > 0 && mid.All(char.IsDigit)) name = name[..i].Trim();
            else break;
        }
        return name.Trim();
    }

    public async Task<List<SaveEntry>> ScanEdenRootAsync(string rootUri)
    {
        var entries = new List<SaveEntry>();
#if ANDROID
        var found = await EmulatorSaveFinderService.ScanEdenAsync(rootUri);
        foreach (var (fileUri, _) in found)
        {
            var entry = await ScanFileAsync(fileUri);
            if (entry != null) entries.Add(entry);
        }
#endif
        return entries;
    }

    public async Task<SaveEntry?> ScanFileAsync(string fileUri)
    {
#if ANDROID
        return await Task.Run(() =>
        {
            try
            {
                var context = Android.App.Application.Context;
                var uri = global::Android.Net.Uri.Parse(fileUri);
                if (uri == null) return null;

                using var stream = context.ContentResolver?.OpenInputStream(uri);
                if (stream == null) return null;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var data = ms.ToArray();
                if (data.Length == 0) return null;

                // Copy before TryGetSaveFile: Switch (Gen 8/9) saves are decrypted
                // in-place by SwishCrypto, which would corrupt RawData for re-parsing.
                var rawData = data.ToArray();
                if (!PKHeX.Core.SaveUtil.TryGetSaveFile(data, out var sav)) return null;

                // Query display name and last-modified timestamp
                string[] proj = [
                    global::Android.Provider.IOpenableColumns.DisplayName,
                    global::Android.Provider.DocumentsContract.Document.ColumnLastModified,
                ];
                var name = "";
                var lastMod = DateTimeOffset.UtcNow;
                using var cursor = context.ContentResolver?.Query(uri, proj, null, null, null);
                if (cursor != null && cursor.MoveToFirst())
                {
                    name = cursor.GetString(0) ?? "";
                    var modMs = cursor.GetLong(1);
                    if (modMs > 0) lastMod = DateTimeOffset.FromUnixTimeMilliseconds(modMs);
                }

                ScanReport.CountRecognized();
                return new SaveEntry(
                    FileUri: fileUri,
                    FileName: name,
                    DirectoryUri: fileUri,
                    Version: ResolveVersion(sav),
                    Generation: sav.Generation,
                    TrainerName: sav.OT,
                    TrainerID: $"{sav.TID16}/{sav.SID16}",
                    PlayTime: sav.PlayTimeString,
                    BoxCount: sav.BoxCount,
                    SlotCount: sav.SlotCount,
                    RawData: rawData,
                    LastModified: lastMod);
            }
            catch (Exception ex)
            {
                ScanReport.Add($"读取失败 {fileUri}：{ex.GetType().Name}");
                return null;
            }
        });
#else
        return await Task.Run(() =>
        {
            try
            {
                var data = File.ReadAllBytes(fileUri);
                var rawData = data.ToArray();
                if (!PKHeX.Core.SaveUtil.TryGetSaveFile(data, out var sav)) return null;
                ScanReport.CountRecognized();
                return new SaveEntry(
                    FileUri: fileUri,
                    FileName: Path.GetFileName(fileUri),
                    DirectoryUri: Path.GetDirectoryName(fileUri) ?? fileUri,
                    Version: ResolveVersion(sav),
                    Generation: sav.Generation,
                    TrainerName: sav.OT,
                    TrainerID: $"{sav.TID16}/{sav.SID16}",
                    PlayTime: sav.PlayTimeString,
                    BoxCount: sav.BoxCount,
                    SlotCount: sav.SlotCount,
                    RawData: rawData,
                    LastModified: new DateTimeOffset(File.GetLastWriteTimeUtc(fileUri), TimeSpan.Zero));
            }
            catch { return null; }
        });
#endif
    }

    /// <summary>解析全自动扫描得到的普通文件路径（直接文件系统访问）。</summary>
    public async Task<SaveEntry?> ScanLocalPathAsync(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0) return null;

                var data = File.ReadAllBytes(path);
                var rawData = data.ToArray();
                if (!PKHeX.Core.SaveUtil.TryGetSaveFile(data, out var sav))
                {
                    ScanReport.Add($"自动跳过 {info.Name}：{FormatSize(data.Length)}，不是支持的存档格式");
                    return null;
                }

                ScanReport.CountRecognized();
                return new SaveEntry(
                    FileUri: path,
                    FileName: info.Name,
                    DirectoryUri: info.DirectoryName ?? path,
                    Version: ResolveVersion(sav),
                    Generation: sav.Generation,
                    TrainerName: sav.OT,
                    TrainerID: $"{sav.TID16}/{sav.SID16}",
                    PlayTime: sav.PlayTimeString,
                    BoxCount: sav.BoxCount,
                    SlotCount: sav.SlotCount,
                    RawData: rawData,
                    LastModified: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            }
            catch (Exception ex)
            {
                ScanReport.Add($"自动读取失败 {Path.GetFileName(path)}：{ex.GetType().Name}");
                return null;
            }
        });
    }

    public async Task<List<SaveEntry>> ScanDirectoryAsync(string dirUri)
    {
        var entries = new List<SaveEntry>();
#if ANDROID
        await Task.Run(() =>
        {
            try
            {
                var context = Android.App.Application.Context;
                var resolver = context.ContentResolver;
                if (resolver == null) { ScanReport.Add($"目录跳过 {ShortUri(dirUri)}：无法访问存储服务"); return; }

                var treeUri = global::Android.Net.Uri.Parse(dirUri);
                if (treeUri == null) { ScanReport.Add($"目录跳过 {ShortUri(dirUri)}：URI 无效"); return; }

                var treeDocId = global::Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri);
                if (treeDocId == null) { ScanReport.Add($"目录跳过 {ShortUri(dirUri)}：不是可扫描的文件夹"); return; }

                string[] projection =
                [
                    global::Android.Provider.DocumentsContract.Document.ColumnDocumentId,
                    global::Android.Provider.DocumentsContract.Document.ColumnDisplayName,
                    global::Android.Provider.DocumentsContract.Document.ColumnMimeType,
                    global::Android.Provider.DocumentsContract.Document.ColumnLastModified,
                ];

                // 递归遍历：Citra / Eden / DeSmuME 的存档都藏在多层子目录里
                var queue = new Queue<(string DocId, int Depth)>();
                queue.Enqueue((treeDocId, 0));
                var visitedDirs = new HashSet<string>();
                int scannedDirs = 0, seenFiles = 0, localFound = 0;

                while (queue.Count > 0)
                {
                    var (parentId, depth) = queue.Dequeue();
                    if (!visitedDirs.Add(parentId)) continue;
                    if (++scannedDirs > 500) break;

                    global::Android.Net.Uri? childrenUri = null;
                    try
                    {
                        childrenUri = global::Android.Provider.DocumentsContract
                            .BuildChildDocumentsUriUsingTree(treeUri, parentId);
                    }
                    catch { continue; }
                    if (childrenUri == null) continue;

                    try
                    {
                        using var cursor = resolver.Query(childrenUri, projection, null, null, null);
                        if (cursor == null) continue;

                        while (cursor.MoveToNext())
                        {
                            var childDocId = cursor.GetString(0);
                            var name      = cursor.GetString(1) ?? "";
                            var mimeType  = cursor.GetString(2) ?? "";
                            var lastModMs = cursor.GetLong(3);
                            if (childDocId == null) continue;

                            if (mimeType == global::Android.Provider.DocumentsContract.Document.MimeTypeDir)
                            {
                                if (depth < MaxDepth && !ShouldSkipDir(name))
                                    queue.Enqueue((childDocId, depth + 1));
                                continue;
                            }

                            var ext = Path.GetExtension(name).ToLowerInvariant();
                            if (!IsCandidateName(name))
                            {
                                // 只记录"像个存档"的，避免把图片/文本刷满
                                if (name.Length <= 48 && name.Contains("sav", StringComparison.OrdinalIgnoreCase))
                                    ScanReport.Add($"跳过 {name}：扩展名 {ext} 不在支持列表");
                                continue;
                            }

                            seenFiles++;
                            ScanReport.CountCandidate();

                            var fileUri = global::Android.Provider.DocumentsContract
                                .BuildDocumentUriUsingTree(treeUri, childDocId);
                            if (fileUri == null) continue;

                            try
                            {
                                using var stream = resolver.OpenInputStream(fileUri);
                                if (stream == null)
                                {
                                    ScanReport.Add($"读不到 {name}：没有读取权限（可能是 Android/data 下的目录）");
                                    continue;
                                }
                                using var ms = new MemoryStream();
                                stream.CopyTo(ms);
                                var data = ms.ToArray();
                                if (data.Length == 0) { ScanReport.Add($"跳过 {name}：空文件"); continue; }

                                var rawData = data.ToArray();
                                if (!PKHeX.Core.SaveUtil.TryGetSaveFile(data, out var sav))
                                {
                                    ScanReport.Add($"跳过 {name}：{FormatSize(data.Length)}，不是支持的存档格式");
                                    continue;
                                }

                                var lastMod = lastModMs > 0
                                    ? DateTimeOffset.FromUnixTimeMilliseconds(lastModMs)
                                    : DateTimeOffset.UtcNow;

                                ScanReport.CountRecognized();
                                localFound++;
                                entries.Add(new SaveEntry(
                                    FileUri: fileUri.ToString()!,
                                    FileName: name,
                                    // 记「文件所在真实子目录」，否则同一根目录下所有 main
                                    // 会被去重逻辑误判成同一份存档的备份，只剩一个
                                    DirectoryUri: ParentDocIdOf(childDocId) ?? treeDocId,
                                    Version: ResolveVersion(sav),
                                    Generation: sav.Generation,
                                    TrainerName: sav.OT,
                                    TrainerID: $"{sav.TID16}/{sav.SID16}",
                                    PlayTime: sav.PlayTimeString,
                                    BoxCount: sav.BoxCount,
                                    SlotCount: sav.SlotCount,
                                    RawData: rawData,
                                    LastModified: lastMod));
                            }
                            catch (Exception ex)
                            {
                                ScanReport.Add($"读取失败 {name}：{ex.GetType().Name}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ScanReport.Add($"子目录读取失败（深度 {depth}）：{ex.GetType().Name}");
                    }
                }

                ScanReport.Add($"目录 {ShortUri(dirUri)}：扫描 {scannedDirs} 个文件夹、{seenFiles} 个候选文件，识别 {localFound} 个");

                // SAF 一个存档都没读到（Android/data 这类受控目录常见）时，
                // 若已授权「所有文件访问」，就换成文件系统直读兜底。
                if (localFound == 0)
                {
                    var realPath = TryMapTreeUriToPath(dirUri);
                    if (realPath == null)
                    {
                        ScanReport.Add($"SAF 读不到，且无法把 {ShortUri(dirUri)} 换算成真实路径");
                    }
                    else if (!AllFilesAccess.IsGranted())
                    {
                        ScanReport.Add($"SAF 读不到，直读需要「所有文件访问」权限（未授权）：{realPath}");
                    }
                    else
                    {
                        var viaFs = ScanMainStructureByPath(realPath);
                        if (viaFs.Count == 0) viaFs = ScanDirectoryByPath(realPath);
                        if (viaFs.Count == 0)
                            ScanReport.Add($"直读兜底也没读到：{realPath}");
                        else
                        {
                            ScanReport.Add($"SAF 读不到，改用直读 {realPath}：{viaFs.Count} 个");
                            entries.AddRange(viaFs);
                            localFound = viaFs.Count;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ScanReport.Add($"目录扫描异常 {ShortUri(dirUri)}：{ex.GetType().Name} {ex.Message}");
            }
        });
#else
        await Task.Run(() =>
        {
            if (!Directory.Exists(dirUri)) { ScanReport.Add($"目录不存在：{dirUri}"); return; }
            int seen = 0, found = 0;
            try
            {
                foreach (var filePath in Directory.EnumerateFiles(dirUri, "*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(filePath).ToLowerInvariant();
                    if (!SaveExtensions.Contains(ext)) continue;
                    seen++;
                    ScanReport.CountCandidate();
                    try
                    {
                        var data = File.ReadAllBytes(filePath);
                        var rawData = data.ToArray();
                        if (!PKHeX.Core.SaveUtil.TryGetSaveFile(data, out var sav))
                        {
                            ScanReport.Add($"跳过 {Path.GetFileName(filePath)}：不是支持的存档格式");
                            continue;
                        }
                        ScanReport.CountRecognized();
                        found++;
                        entries.Add(new SaveEntry(
                            FileUri: filePath,
                            FileName: Path.GetFileName(filePath),
                            DirectoryUri: dirUri,
                            Version: ResolveVersion(sav),
                            Generation: sav.Generation,
                            TrainerName: sav.OT,
                            TrainerID: $"{sav.TID16}/{sav.SID16}",
                            PlayTime: sav.PlayTimeString,
                            BoxCount: sav.BoxCount,
                            SlotCount: sav.SlotCount,
                            RawData: rawData,
                            LastModified: new DateTimeOffset(File.GetLastWriteTimeUtc(filePath), TimeSpan.Zero)));
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                ScanReport.Add($"目录扫描异常：{ex.GetType().Name}");
            }
            ScanReport.Add($"目录 {dirUri}：候选 {seen} 个，识别 {found} 个");
        });
#endif
        return entries;
    }

    /// <summary>直接走文件系统递归扫描（Android 上需已授权「所有文件访问」）。</summary>
    internal static List<SaveEntry> ScanDirectoryByPath(string dirPath, int maxDepth = MaxDepth)
    {
        var entries = new List<SaveEntry>();
        if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath)) return entries;
        try
        {
            bool truncated;
            foreach (var filePath in EnumerateFilesLimited(dirPath, maxDepth, out truncated))
            {
                if (!IsCandidateName(Path.GetFileName(filePath))) continue;
                try
                {
                    var info = new FileInfo(filePath);
                    if (!info.Exists || info.Length < MinSaveBytes || info.Length > MaxSaveBytes) continue;
                    var data = File.ReadAllBytes(filePath);
                    var rawData = data.ToArray();
                    if (!PKHeX.Core.SaveUtil.TryGetSaveFile(data, out var sav)) continue;

                    ScanReport.CountCandidate();
                    ScanReport.CountRecognized();
                    entries.Add(new SaveEntry(
                        FileUri: filePath,
                        FileName: info.Name,
                        DirectoryUri: info.DirectoryName ?? dirPath,
                        Version: ResolveVersion(sav),
                        Generation: sav.Generation,
                        TrainerName: sav.OT,
                        TrainerID: $"{sav.TID16}/{sav.SID16}",
                        PlayTime: sav.PlayTimeString,
                        BoxCount: sav.BoxCount,
                        SlotCount: sav.SlotCount,
                        RawData: rawData,
                        LastModified: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            ScanReport.Add($"直读目录异常 {dirPath}：{ex.GetType().Name}");
        }
        return entries;
    }

    /// <summary>限深度、限数量、跳过缓存类目录的文件枚举（手写栈，避免全树冲进去）。</summary>
    private static List<string> EnumerateFilesLimited(string root, int maxDepth, out bool truncated)
    {
        var found = new List<string>();
        int budget = MaxPathCandidates;
        truncated = false;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir); } catch { continue; }

            foreach (var f in files)
            {
                if (budget-- <= 0) { truncated = true; return found; }
                found.Add(f);
            }

            if (depth >= maxDepth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { continue; }
            foreach (var sub in subs)
            {
                if (ShouldSkipDir(Path.GetFileName(sub))) continue;
                stack.Push((sub, depth + 1));
            }
        }
        return found;
    }

    /// <summary>
    /// 「main」定向快扫：3DS 与 NS（Eden/Suyu）的存档都是无扩展名的 <c>main</c>，
    /// 只找这些文件，比全树遍历快一到两个数量级。
    /// </summary>
    internal static List<SaveEntry> ScanMainStructureByPath(string dirPath)
    {
        var entries = new List<SaveEntry>();
        if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath)) return entries;
        try
        {
            bool truncated;
            foreach (var filePath in EnumerateFilesLimited(dirPath, MaxDepth, out truncated))
            {
                var name = Path.GetFileName(filePath);
                if (!name.Equals("main", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var info = new FileInfo(filePath);
                    if (!info.Exists || info.Length < MinSaveBytes || info.Length > MaxSaveBytes) continue;
                    var data = File.ReadAllBytes(filePath);
                    var rawData = data.ToArray();
                    if (!PKHeX.Core.SaveUtil.TryGetSaveFile(data, out var sav)) continue;

                    ScanReport.CountCandidate();
                    ScanReport.CountRecognized();
                    entries.Add(new SaveEntry(
                        FileUri: filePath,
                        FileName: info.Name,
                        DirectoryUri: info.DirectoryName ?? dirPath,
                        Version: ResolveVersion(sav),
                        Generation: sav.Generation,
                        TrainerName: sav.OT,
                        TrainerID: $"{sav.TID16}/{sav.SID16}",
                        PlayTime: sav.PlayTimeString,
                        BoxCount: sav.BoxCount,
                        SlotCount: sav.SlotCount,
                        RawData: rawData,
                        LastModified: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            ScanReport.Add($"3DS 快扫异常 {dirPath}：{ex.GetType().Name}");
        }
        return entries;
    }

    /// <summary>校验缓存里的条目：文件没变就直接沿用，变了才重新读。</summary>
    public async Task<List<SaveEntry>> VerifyCachedAsync(List<SaveEntry> cached)
    {
        var result = new List<SaveEntry>();
        foreach (var e in cached)
        {
            try
            {
                var meta = await GetMetaAsync(e.FileUri);
                if (meta is null) continue; // 文件已被删除/移走
                var (lastMod, size) = meta.Value;
                bool sameTime = Math.Abs((lastMod - e.LastModified).TotalSeconds) < 2;
                bool sameSize = size == e.RawData.Length;
                if (sameTime && sameSize) { result.Add(e); continue; }

                var fresh = await ScanFileAsync(e.FileUri);
                if (fresh != null) result.Add(fresh);
            }
            catch { result.Add(e); }
        }
        ScanReport.Add($"缓存校验：{cached.Count} 个 → 有效 {result.Count} 个");
        return result;
    }

    /// <summary>取文件的修改时间与体积（SAF 与裸路径都支持）；取不到返回 null。</summary>
    private async Task<(DateTimeOffset LastModified, long Size)?> GetMetaAsync(string fileUri)
    {
#if ANDROID
        return await Task.Run<(DateTimeOffset, long)?>(() =>
        {
            try
            {
                var context = Android.App.Application.Context;
                if (fileUri.StartsWith("content://"))
                {
                    var uri = global::Android.Net.Uri.Parse(fileUri);
                    if (uri == null) return null;
                    string[] proj = [
                        global::Android.Provider.DocumentsContract.Document.ColumnSize,
                        global::Android.Provider.DocumentsContract.Document.ColumnLastModified,
                    ];
                    using var cursor = context.ContentResolver?.Query(uri, proj, null, null, null);
                    if (cursor == null || !cursor.MoveToFirst()) return null;
                    var size = cursor.GetLong(0);
                    var modMs = cursor.GetLong(1);
                    return (DateTimeOffset.FromUnixTimeMilliseconds(modMs), size);
                }
                var info = new FileInfo(fileUri);
                if (!info.Exists) return null;
                return (new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length);
            }
            catch { return null; }
        });
#else
        await Task.Yield();
        var fi = new FileInfo(fileUri);
        if (!fi.Exists) return null;
        return (new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero), fi.Length);
#endif
    }

    /// <summary>把 SAF 的 tree URI 换成真实路径；只处理主存储 primary: 与非主存储卷。</summary>
    private static string? TryMapTreeUriToPath(string dirUri)
    {
        try
        {
            var uri = global::Android.Net.Uri.Parse(dirUri);
            if (uri == null) return null;
            var docId = global::Android.Provider.DocumentsContract.GetTreeDocumentId(uri);
            if (string.IsNullOrEmpty(docId)) return null;

            var sep = docId.IndexOf(':');
            if (sep < 0) return null;
            var volume = docId[..sep];
            var rel = docId[(sep + 1)..];

            string root;
            if (volume.Equals("primary", StringComparison.OrdinalIgnoreCase))
                root = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0";
            else
                root = "/storage/" + volume;

            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            return Directory.Exists(full) ? full : null;
        }
        catch { return null; }
    }

    /// <summary>只读一个路径（文件或文件夹），用于「新增存档 / 添加文件夹」后的增量加入。</summary>
    public async Task<List<SaveEntry>> ScanSingleAsync(string uri, bool isFile)
    {
        if (isFile)
        {
            var one = await ScanFileAsync(uri);
            return one is null ? [] : [one];
        }
        return await ScanDirectoryOrPathAsync(uri);
    }

    /// <summary>
    /// 目录扫描统一入口：SAF 的 content:// 走系统查询；裸路径走文件系统，
    /// 并且先做「main 定向」快扫（3DS / NS），没结果再回落到普通递归。
    /// </summary>
    public async Task<List<SaveEntry>> ScanDirectoryOrPathAsync(string target)
    {
        if (!string.IsNullOrWhiteSpace(target) && !target.StartsWith("content://"))
        {
            return await Task.Run(() =>
            {
                if (!Directory.Exists(target)) return [];
                var fast = ScanMainStructureByPath(target);
                return fast.Count > 0 ? fast : ScanDirectoryByPath(target);
            });
        }
        return await ScanDirectoryAsync(target);
    }

    private static string FormatSize(int bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB"
           : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
           : $"{bytes} B";

    /// <summary>URI 太长，只留末尾一段便于辨认。</summary>
    private static string ShortUri(string uri)
    {
        try
        {
            var decoded = global::Android.Net.Uri.Decode(uri) ?? uri;
            const int max = 60;
            return decoded.Length <= max ? decoded : "…" + decoded[^max..];
        }
        catch { return uri; }
    }
}
