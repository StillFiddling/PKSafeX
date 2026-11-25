namespace PKHeX.Mobile.Services;

/// <summary>
/// Scans for Pokémon save files stored by Android emulators using SAF (Storage Access Framework).
/// Supports Eden (Switch), MelonDS (DS), and Azahar (3DS export folders).
/// </summary>
public static class EmulatorSaveFinderService
{
    // Known Pokémon Switch title IDs — used only for display names.
    private static readonly Dictionary<string, string> PokemonSwitchGames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "010003F003A34000", "Let's Go Pikachu" },
            { "0100187003A36000", "Let's Go Eevee"   },
            { "0100ABF008968000", "Sword"             },
            { "01008DB008C2C000", "Shield"            },
            { "0100000011D90000", "Brilliant Diamond" },
            { "010018E011D92000", "Shining Pearl"     },
            { "01001F5010B28000", "Legends: Arceus"   },
            { "0100A3D008C5C000", "Scarlet"           },
            { "01008F6008C5E000", "Violet"            },
            { "0100F43008C44000", "Legends: Z-A"      },
        };

    /// <summary>
    /// Scans an Eden emulator "files" root folder for Pokémon Switch saves.
    /// Navigates to nand/user/save/ then recursively searches for files named
    /// "main", trying SaveUtil on each one. No assumptions about uid/title layout.
    /// </summary>
    public static async Task<List<(string FileUri, string GameName)>> ScanEdenAsync(string rootTreeUri)
    {
        return await Task.Run(() =>
        {
            var results = new List<(string, string)>();
#if ANDROID
            try
            {
                var context  = global::Android.App.Application.Context;
                var resolver = context.ContentResolver;
                if (resolver == null) return results;

                var treeUri = global::Android.Net.Uri.Parse(rootTreeUri);
                if (treeUri == null) return results;

                var rootDocId = global::Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri);
                if (rootDocId == null) return results;

                // Navigate to the save directory. Try several path variants because the
                // user may have picked the 'files/' root, the 'nand/' folder, or 'user/'.
                string[][] candidates =
                [
                    ["nand", "user", "save"],   // root = eden files/
                    ["user", "save"],            // root = eden files/nand/
                    ["save"],                    // root = eden files/nand/user/
                ];
                string? saveDocId = null;
                foreach (var path in candidates)
                {
                    saveDocId = NavigatePath(resolver, treeUri, rootDocId, path);
                    if (saveDocId != null) break;
                }
                if (saveDocId == null) return results;

                // Collect every "main" or "*.bin" file found up to 8 directories deep.
                // BDSP/BD saves may appear as .bin rather than the standard extensionless "main".
                var mainDocIds = new List<string>();
                FindMatchingFiles(resolver, treeUri, saveDocId,
                    name => name == "main" || name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase),
                    8, mainDocIds);

                // Try SaveUtil on each candidate
                foreach (var mainDocId in mainDocIds)
                {
                    var mainUri = global::Android.Provider.DocumentsContract
                        .BuildDocumentUriUsingTree(treeUri, mainDocId);
                    if (mainUri == null) continue;

                    try
                    {
                        using var stream = resolver.OpenInputStream(mainUri);
                        if (stream == null) continue;
                        using var ms = new System.IO.MemoryStream();
                        stream.CopyTo(ms);
                        var data = ms.ToArray();
                        if (data.Length == 0 || !PKHeX.Core.SaveUtil.TryGetSaveFile(data, out _))
                            continue;

                        // Try to extract a friendly name from the parent directory name
                        // (the title ID folder), falling back to "Switch save"
                        var gameName = GuessEdenGameName(mainDocId);
                        results.Add((mainUri.ToString()!, gameName));
                    }
                    catch { }
                }
            }
            catch { }
#endif
            return results;
        });
    }

    /// <summary>
    /// Scans an Azahar (Citra) emulator root folder for 3DS Pokémon save files.
    /// Tree: root/sdmc/Nintendo 3DS/&lt;ID0&gt;/&lt;ID1&gt;/title/00040000/&lt;game_id&gt;/data/00000001/main
    /// </summary>
    public static async Task<List<(string FileUri, string GameName)>> ScanAzaharAsync(string rootTreeUri)
    {
        return await Task.Run(() =>
        {
            var results = new List<(string, string)>();
#if ANDROID
            try
            {
                var context  = global::Android.App.Application.Context;
                var resolver = context.ContentResolver;
                if (resolver == null) return results;

                var treeUri = global::Android.Net.Uri.Parse(rootTreeUri);
                if (treeUri == null) return results;

                var rootDocId = global::Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri);
                if (rootDocId == null) return results;

                var n3dsDocId = NavigatePath(resolver, treeUri, rootDocId, ["sdmc", "Nintendo 3DS"]);
                if (n3dsDocId == null) return results;

                foreach (var (id0DocId, _) in ListChildren(resolver, treeUri, n3dsDocId))
                foreach (var (id1DocId, _) in ListChildren(resolver, treeUri, id0DocId))
                {
                    var retailDocId = NavigatePath(resolver, treeUri, id1DocId, ["title", "00040000"]);
                    if (retailDocId == null) continue;

                    foreach (var (gameDocId, _) in ListChildren(resolver, treeUri, retailDocId))
                    {
                        var dataDocId = NavigatePath(resolver, treeUri, gameDocId, ["data", "00000001"]);
                        if (dataDocId == null) continue;

                        var saveDocId = FindChildDocId(resolver, treeUri, dataDocId, "main");
                        if (saveDocId == null) continue;

                        var saveUri = global::Android.Provider.DocumentsContract
                            .BuildDocumentUriUsingTree(treeUri, saveDocId);
                        if (saveUri == null) continue;

                        try
                        {
                            using var stream = resolver.OpenInputStream(saveUri);
                            if (stream == null) continue;
                            using var ms = new System.IO.MemoryStream();
                            stream.CopyTo(ms);
                            var data = ms.ToArray();
                            if (data.Length > 0 && PKHeX.Core.SaveUtil.TryGetSaveFile(data, out _))
                                results.Add((saveUri.ToString()!, "Azahar save"));
                        }
                        catch { }
                    }
                }
            }
            catch { }
#endif
            return results;
        });
    }

    // ── 模板化定向扫描（GBA / NDS / 3DS / NS）────────────────────────────────
    //
    // 思路：先花极低成本判定「用户加的这根目录是哪个模拟器的」，命中就按它的
    // 固定目录模板精确取档；判定不出来（或模板没取到东西）才回落通用递归。
    // 这样做的好处：
    //   ① 3DS/NS 的 main 藏在 title/data 四五层下，模板只走固定几层，
    //      不会撞通用遍历的 500 目录 / 20000 文件上限；
    //   ② SAF 版产出 content://，写回与缓存校验都能重读（裸路径那条缺陷绕开了）。

    public sealed record TemplateScanResult(string Flavor, List<string> Uris);

    public static async Task<TemplateScanResult> ScanTemplateAsync(string root)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (root.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
                    return ScanTemplateSaf(root);
                return ScanTemplatePath(root);
            }
            catch (Exception ex)
            {
                ScanReport.Add($"画像扫描异常：{ex.GetType().Name}");
                return new TemplateScanResult("", []);
            }
        });
    }

    // ── 路径（直读）实现 ─────────────────────────────────────────────────

    private static TemplateScanResult ScanTemplatePath(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new TemplateScanResult("", []);

        var flavor = DetectPathFlavor(root);
        var uris = new List<string>();

        switch (flavor)
        {
            case "Azahar(3DS)":
            {
                var n3ds = FindFirstDir(root, ["Nintendo 3DS", "sdmc/Nintendo 3DS"], 2);
                if (n3ds != null) Collect3DsByPath(n3ds, uris);
                break;
            }
            case "Eden(NS)":
            {
                var saveDir = FindFirstDir(root, ["nand/user/save", "user/save", "save"], 2) ?? root;
                foreach (var f in EnumerateMain(saveDir, 5)) uris.Add(f);
                break;
            }
            case "NDS":
            {
                foreach (var dir in new[]
                         {
                             Path.Combine(root, "DraStic", "backup"),
                             Path.Combine(root, "Backup"),
                             Path.Combine(root, "melonDS", "saves"),
                             root,
                         })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir))
                        if (IsNdsSaveName(Path.GetFileName(f))) uris.Add(f);
                }
                break;
            }
            case "GBA":
            {
                foreach (var dir in new[]
                         {
                             Path.Combine(root, "RetroArch", "saves"),
                             Path.Combine(root, "MyBoy", "save"),
                             root,
                         })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir))
                        if (IsGbaSaveName(Path.GetFileName(f))) uris.Add(f);
                }
                break;
            }
        }

        if (flavor.Length == 0) return new TemplateScanResult("", []);
        return new TemplateScanResult(flavor, uris);
    }

    private static List<string> EnumerateMain(string dir, int maxDepth)
    {
        var found = new List<string>();
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((dir, 0));
        int budget = 4000;
        while (stack.Count > 0 && budget > 0)
        {
            var (d, depth) = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(d); } catch { continue; }
            foreach (var f in files)
            {
                budget--;
                if (Path.GetFileName(f).Equals("main", StringComparison.OrdinalIgnoreCase))
                    found.Add(f);
            }
            if (depth >= maxDepth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(d); } catch { continue; }
            foreach (var s in subs) stack.Push((s, depth + 1));
        }
        return found;
    }

    private static void Collect3DsByPath(string n3dsRoot, List<string> uris)
    {
        foreach (var id0 in SafeDirs(n3dsRoot))
        foreach (var id1 in SafeDirs(id0))
        {
            var retail = Path.Combine(id1, "title", "00040000");
            if (!Directory.Exists(retail)) continue;
            foreach (var game in SafeDirs(retail))
            {
                foreach (var dataName in new[] { "00000001", "00000002" })
                {
                    var main = Path.Combine(game, "data", dataName, "main");
                    if (File.Exists(main)) uris.Add(main);
                }
            }
        }
    }

    private static string[] SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return []; }
    }

    private static string? FindFirstDir(string root, string[] relatives, int maxDepth)
    {
        foreach (var rel in relatives)
        {
            var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    private static bool IsNdsSaveName(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".dsv" or ".sav" or ".srm";
    }

    private static bool IsGbaSaveName(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".sav" or ".srm";
    }

    /// <summary>按目录名判断这根目录属于哪个模拟器（不猜内容，只看结构特征）。</summary>
    /// <summary>
    /// 按目录名判断这根目录属于哪个模拟器（不猜内容，只看结构特征）。
    /// 命不中时再让 <see cref="ProbeSafFlavor"/> 看根下 1 层目录。
    /// </summary>
#if ANDROID
    private static string ProbeSafFlavor(
        global::Android.Content.ContentResolver resolver,
        global::Android.Net.Uri treeUri,
        string rootDocId)
    {
        var kids = ListChildrenTyped(resolver, treeUri, rootDocId);
        bool has(string n) => kids.Any(k => k.IsDir
            && string.Equals(ChildNameOf(k.DocId), n, StringComparison.OrdinalIgnoreCase));

        if (has("Nintendo 3DS") || has("sdmc")) return "Azahar(3DS)";
        if (has("nand")) return "Eden(NS)";
        if (has("DraStic") || has("melonDS")) return "NDS";
        if (has("RetroArch") || has("MyBoy")) return "GBA";

        // 根下是 sdmc 以外的中间层（例如 3DS/sdmc 直接作为根）：再看一层
        foreach (var k in kids)
        {
            if (!k.IsDir) continue;
            var grand = ListChildrenTyped(resolver, treeUri, k.DocId);
            bool g(string n) => grand.Any(x => x.IsDir
                && string.Equals(ChildNameOf(x.DocId), n, StringComparison.OrdinalIgnoreCase));
            if (g("Nintendo 3DS")) return "Azahar(3DS)";
            if (g("save")) return "Eden(NS)";
        }
        return "";
    }
#endif

    private static string DetectPathFlavor(string root)
    {
        var name = Path.GetFileName(root).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        var parent = Path.GetFileName(Path.GetDirectoryName(root) ?? "").ToLowerInvariant();

        bool Has(string rel) => Directory.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

        if (name is "nintendo 3ds" || name == "sdmc" || Has("Nintendo 3DS") || Has("sdmc/Nintendo 3DS"))
            return "Azahar(3DS)";
        if (name == "save" && Has("nand") || Has("nand/user/save") || Has("user/save"))
            return "Eden(NS)";
        if (Has("DraStic") || Has("melonDS") || ((name is "backup" or "saves" or "dsv") && parent is "drastic" or "melonds"))
            return "NDS";
        if (Has("RetroArch") || Has("MyBoy") || (name == "saves" && parent == "retroarch") || (name == "save" && parent == "myboy"))
            return "GBA";
        return "";
    }

    // ── SAF 实现 ─────────────────────────────────────────────────────────

#if ANDROID
    private static TemplateScanResult ScanTemplateSaf(string root)
    {
        var context  = global::Android.App.Application.Context;
        var resolver = context.ContentResolver;
        if (resolver == null) return new TemplateScanResult("", []);

        var treeUri = global::Android.Net.Uri.Parse(root);
        if (treeUri == null) return new TemplateScanResult("", []);

        var rootDocId = global::Android.Provider.DocumentsContract.GetTreeDocumentId(treeUri);
        if (rootDocId == null) return new TemplateScanResult("", []);

        var flavor = DetectSafFlavor(root, rootDocId);
        // docId 字符串认不出来（例如用户选的是 primary:3DS，里面才是 sdmc/Nintendo 3DS）
        // 时，花一次 query 看根下 1 层目录名。否则模板对这类根目录完全不生效。
        if (flavor.Length == 0) flavor = ProbeSafFlavor(resolver, treeUri, rootDocId);
        var uris = new List<string>();
        if (flavor.Length == 0) return new TemplateScanResult("", []);

        string DocUri(string docId) => global::Android.Provider.DocumentsContract
            .BuildDocumentUriUsingTree(treeUri, docId)?.ToString() ?? "";

        switch (flavor)
        {
            case "Azahar(3DS)":
            {
                var n3ds = FindDirByName(resolver, treeUri, rootDocId, "Nintendo 3DS", 3);
                if (n3ds == null) break;
                foreach (var (id0, isDir0) in ListChildrenTyped(resolver, treeUri, n3ds))
                {
                    if (!isDir0) continue;
                    foreach (var (id1, isDir1) in ListChildrenTyped(resolver, treeUri, id0))
                    {
                        if (!isDir1) continue;
                        var retail = NavigatePath(resolver, treeUri, id1, ["title", "00040000"]);
                        if (retail == null) continue;
                        foreach (var (game, isGameDir) in ListChildrenTyped(resolver, treeUri, retail))
                        {
                            if (!isGameDir) continue;
                            foreach (var dataName in new[] { "00000001", "00000002" })
                            {
                                var dataDoc = NavigatePath(resolver, treeUri, game, ["data", dataName]);
                                if (dataDoc == null) continue;
                                var mainDoc = FindChildDocId(resolver, treeUri, dataDoc, "main");
                                if (mainDoc == null) continue;
                                var u = DocUri(mainDoc);
                                if (u.Length > 0) uris.Add(u);
                            }
                        }
                    }
                }
                break;
            }
            case "Eden(NS)":
            {
                var saveDoc = FindDirByName(resolver, treeUri, rootDocId, "save", 3);
                if (saveDoc == null) break;
                var mains = new List<string>();
                FindMatchingFiles(resolver, treeUri, saveDoc,
                    n => n.Equals("main", StringComparison.OrdinalIgnoreCase), 5, mains);
                foreach (var doc in mains)
                {
                    var u = DocUri(doc);
                    if (u.Length > 0) uris.Add(u);
                }
                break;
            }
            case "NDS":
            {
                CollectFilesSaf(resolver, treeUri, rootDocId, root, IsNdsSaveName, 2, uris);
                break;
            }
            case "GBA":
            {
                CollectFilesSaf(resolver, treeUri, rootDocId, root, IsGbaSaveName, 2, uris);
                break;
            }
        }

        return new TemplateScanResult(flavor, uris);
    }

    private static void CollectFilesSaf(
        global::Android.Content.ContentResolver resolver,
        global::Android.Net.Uri treeUri,
        string rootDocId,
        string rootUri,
        Func<string, bool> match,
        int maxDepth,
        List<string> uris)
    {
        // 直接在选中的根、以及它下面 2 层里按扩展名取；比逐层导航省事，
        // 且 3DS/NS 那种深结构不会被误伤（它们按名字/结构已先判出去）。
        var docIds = new List<string>();
        FindMatchingFiles(resolver, treeUri, rootDocId, match, maxDepth, docIds);
        foreach (var doc in docIds)
        {
            var u = global::Android.Provider.DocumentsContract.BuildDocumentUriUsingTree(treeUri, doc);
            if (u != null) uris.Add(u.ToString()!);
        }
    }

    private static string DetectSafFlavor(string rootUri, string rootDocId)
    {
        // docId 形如 primary:3DS/sdmc/… —— 先看路径字符串，再让调用方去探测子目录
        var doc = rootDocId.Replace('\\', '/').ToLowerInvariant();
        if (doc.Contains("nintendo 3ds") || doc.EndsWith("/sdmc") || doc.EndsWith(":sdmc"))
            return "Azahar(3DS)";
        if (doc.EndsWith("/nand") || doc.Contains("nand/user/save"))
            return "Eden(NS)";
        if (doc.Contains("drastic") || doc.Contains("melonds"))
            return "NDS";
        if (doc.Contains("retroarch") || doc.Contains("myboy"))
            return "GBA";
        return "";
    }

    private static string? FindDirByName(
        global::Android.Content.ContentResolver resolver,
        global::Android.Net.Uri treeUri,
        string startDocId,
        string name,
        int maxDepth)
    {
        var queue = new Queue<(string DocId, int Depth)>();
        queue.Enqueue((startDocId, 0));
        var visited = new HashSet<string>();
        while (queue.Count > 0)
        {
            var (docId, depth) = queue.Dequeue();
            if (!visited.Add(docId)) continue;
            foreach (var (childId, isDir) in ListChildrenTyped(resolver, treeUri, docId))
            {
                if (!isDir) continue;
                var childName = ChildNameOf(childId);
                if (string.Equals(childName, name, StringComparison.OrdinalIgnoreCase)) return childId;
                if (depth + 1 < maxDepth) queue.Enqueue((childId, depth + 1));
            }
        }
        return null;
    }

    /// <summary>docId 最后一段即显示名（外部存储的 docId 是路径式的）。</summary>
    private static string ChildNameOf(string docId)
    {
        var idx = docId.LastIndexOf('/');
        return idx >= 0 ? docId[(idx + 1)..] : docId;
    }

    private static List<(string DocId, bool IsDir)> ListChildrenTyped(
        global::Android.Content.ContentResolver resolver,
        global::Android.Net.Uri treeUri,
        string parentDocId)
    {
        var result = new List<(string, bool)>();
        var childrenUri = global::Android.Provider.DocumentsContract
            .BuildChildDocumentsUriUsingTree(treeUri, parentDocId);
        if (childrenUri == null) return result;

        string[] projection =
        [
            global::Android.Provider.DocumentsContract.Document.ColumnDocumentId,
            global::Android.Provider.DocumentsContract.Document.ColumnMimeType,
        ];

        using var cursor = resolver.Query(childrenUri, projection, null, null, null);
        if (cursor == null) return result;

        while (cursor.MoveToNext())
        {
            var docId = cursor.GetString(0);
            var mime  = cursor.GetString(1) ?? "";
            if (docId == null) continue;
            var isDir = mime == global::Android.Provider.DocumentsContract.Document.MimeTypeDir;
            result.Add((docId, isDir));
        }
        return result;
    }
#endif

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to derive a game name from the document ID of a "main" file.
    /// The document ID for external storage is path-based, so the parent segment
    /// is likely the title ID directory name.
    /// </summary>
    private static string GuessEdenGameName(string mainDocId)
    {
        // docId looks like "primary:Android/data/.../nand/user/save/0/uid/uid/0100ABF008968000/main"
        // Walk backwards to find the first segment that matches a known title ID.
        var parts = mainDocId.Replace('\\', '/').Split('/');
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            if (PokemonSwitchGames.TryGetValue(parts[i], out var name))
                return name;
        }
        return "Switch save";
    }

    // ── Android SAF helpers ───────────────────────────────────────────────────

#if ANDROID
    /// <summary>
    /// Recursively finds all files matching <paramref name="match"/> under a document tree node,
    /// up to <paramref name="maxDepth"/> directory levels deep.
    /// </summary>
    private static void FindMatchingFiles(
        global::Android.Content.ContentResolver resolver,
        global::Android.Net.Uri treeUri,
        string parentDocId,
        Func<string, bool> match,
        int maxDepth,
        List<string> results)
    {
        if (maxDepth <= 0) return;

        var childrenUri = global::Android.Provider.DocumentsContract
            .BuildChildDocumentsUriUsingTree(treeUri, parentDocId);
        if (childrenUri == null) return;

        string[] projection =
        [
            global::Android.Provider.DocumentsContract.Document.ColumnDocumentId,
            global::Android.Provider.DocumentsContract.Document.ColumnDisplayName,
            global::Android.Provider.DocumentsContract.Document.ColumnMimeType,
        ];

        using var cursor = resolver.Query(childrenUri, projection, null, null, null);
        if (cursor == null) return;

        while (cursor.MoveToNext())
        {
            var docId    = cursor.GetString(0);
            var name     = cursor.GetString(1);
            var mimeType = cursor.GetString(2);
            if (docId == null || name == null) continue;

            bool isDir = mimeType == global::Android.Provider.DocumentsContract.Document.MimeTypeDir;

            if (!isDir && match(name))
                results.Add(docId);
            else if (isDir)
                FindMatchingFiles(resolver, treeUri, docId, match, maxDepth - 1, results);
        }
    }

    private static string? NavigatePath(
        global::Android.Content.ContentResolver resolver,
        global::Android.Net.Uri treeUri,
        string rootDocId,
        string[] path)
    {
        var current = rootDocId;
        foreach (var segment in path)
        {
            current = FindChildDocId(resolver, treeUri, current, segment)!;
            if (current == null) return null;
        }
        return current;
    }

    private static string? FindChildDocId(
        global::Android.Content.ContentResolver resolver,
        global::Android.Net.Uri treeUri,
        string parentDocId,
        string childName)
    {
        var childrenUri = global::Android.Provider.DocumentsContract
            .BuildChildDocumentsUriUsingTree(treeUri, parentDocId);
        if (childrenUri == null) return null;

        string[] projection =
        [
            global::Android.Provider.DocumentsContract.Document.ColumnDocumentId,
            global::Android.Provider.DocumentsContract.Document.ColumnDisplayName,
        ];

        using var cursor = resolver.Query(childrenUri, projection, null, null, null);
        if (cursor == null) return null;

        while (cursor.MoveToNext())
        {
            if (cursor.GetString(1) == childName)
                return cursor.GetString(0);
        }
        return null;
    }

    private static List<(string DocId, string Name)> ListChildren(
        global::Android.Content.ContentResolver resolver,
        global::Android.Net.Uri treeUri,
        string parentDocId)
    {
        var result = new List<(string, string)>();
        var childrenUri = global::Android.Provider.DocumentsContract
            .BuildChildDocumentsUriUsingTree(treeUri, parentDocId);
        if (childrenUri == null) return result;

        string[] projection =
        [
            global::Android.Provider.DocumentsContract.Document.ColumnDocumentId,
            global::Android.Provider.DocumentsContract.Document.ColumnDisplayName,
        ];

        using var cursor = resolver.Query(childrenUri, projection, null, null, null);
        if (cursor == null) return result;

        while (cursor.MoveToNext())
        {
            var docId = cursor.GetString(0);
            var name  = cursor.GetString(1);
            if (docId != null && name != null)
                result.Add((docId, name));
        }
        return result;
    }
#endif
}
