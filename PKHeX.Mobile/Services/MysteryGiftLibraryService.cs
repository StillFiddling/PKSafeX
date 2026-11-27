using PKHeX.Core;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 玩家自备的神秘礼物卡库。
///
/// 目录固定放在 app 外部 files/mgdb 下（文件管理器可取），启动或导入后并入内置配信库，
/// 之后的列表里就能搜到自己的卡。
/// </summary>
public static class MysteryGiftLibraryService
{
    private static bool _loaded;

    /// <summary>礼物库目录（外部 files/mgdb，取不到外部目录时退回 AppData）。</summary>
    public static string LibraryPath { get; } = ResolveLibraryPath();

    private static string ResolveLibraryPath()
    {
        try
        {
            var ext = Android.App.Application.Context.GetExternalFilesDir(null);
            if (ext is not null) return Path.Combine(ext.AbsolutePath, "mgdb");
        }
        catch { /* 拿不到就走 AppData */ }
        return Path.Combine(FileSystem.AppDataDirectory, "mgdb");
    }

    /// <summary>首次调用时建目录并把库里的卡并进去；重复调用无副作用。</summary>
    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Refresh();
    }

    /// <summary>重新扫描礼物库目录并重建配信库（导入后调用）。</summary>
    public static void Refresh()
    {
        try
        {
            Directory.CreateDirectory(LibraryPath);
            EncounterEvent.RefreshMGDB([LibraryPath]);
        }
        catch { /* 库坏了不该拦住页面 */ }
    }

    public static int CountFiles()
    {
        try
        {
            if (!Directory.Exists(LibraryPath)) return 0;
            return Directory.EnumerateFiles(LibraryPath, "*", SearchOption.AllDirectories).Count(IsGiftFile);
        }
        catch { return 0; }
    }

    private static readonly string[] _giftExtensions =
    [
        "wc4", "pcd", "pgt", "pgf",
        "wc5", "wc6", "wc6full", "wc7", "wc7full", "wb7", "wb7full",
        "wc8", "wb8", "wa8",
        "wc9", "wa9",
    ];

    private static bool IsGiftFile(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return _giftExtensions.Contains(ext);
    }

    /// <summary>把用户选中的卡复制进礼物库，并重建配信库。</summary>
    public static async Task<(bool ok, string message)> ImportAsync(string contentUri)
    {
        try
        {
            var context   = Android.App.Application.Context;
            var resolver  = context.ContentResolver;
            if (resolver is null) return (false, "读不到文件");

            var uri  = Android.Net.Uri.Parse(contentUri);
            if (uri is null) return (false, "文件路径识别不出来");
            var name = QueryDisplayName(resolver, uri) ?? "gift.wc7";
            if (!IsGiftFile(name)) return (false, "这不是神秘礼物卡文件（.wc7 / .pcd / .pgf 等）");

            Directory.CreateDirectory(LibraryPath);
            var dest = Path.Combine(LibraryPath, SanitizeFileName(name));

            await using (var input = resolver.OpenInputStream(uri))
            {
                if (input is null) return (false, "读不到文件内容");
                await using var output = File.Create(dest);
                await input.CopyToAsync(output);
            }

            Refresh();
            return (true, $"已导入 {Path.GetFileName(dest)}");
        }
        catch (Exception ex)
        {
            return (false, $"导入失败：{ex.Message}");
        }
    }

    private static string? QueryDisplayName(Android.Content.ContentResolver resolver, Android.Net.Uri uri)
    {
        try
        {
            using var cursor = resolver.Query(uri, null, null, null, null);
            if (cursor is null || !cursor.MoveToFirst()) return null;

            int idx = cursor.GetColumnIndex(Android.Provider.IOpenableColumns.DisplayName);
            if (idx < 0) return null;
            return cursor.GetString(idx);
        }
        catch { return null; }
    }

    private static string SanitizeFileName(string name)
    {
        var clean = Path.GetFileName(name);
        foreach (var ch in Path.GetInvalidFileNameChars())
            clean = clean.Replace(ch, '_');
        return clean.Length == 0 ? "gift.wc7" : clean;
    }
}
