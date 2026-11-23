using System.IO.Compression;
using System.Text.RegularExpressions;

namespace PKHeX.Mobile.Services;

public sealed class SpriteImportResult
{
    public int Home;
    public int HomeShiny;
    public int Anim;
    public int AnimShiny;
    public int Skipped;
    public int Invalid;
    public int Failed;
    public int Total;
    public readonly List<string> Errors = [];

    public int Imported => Home + HomeShiny + Anim + AnimShiny;

    public string Summary()
        => $"导入完成：HOME {Home} + 闪光 {HomeShiny}，动图 {Anim} + 闪光 {AnimShiny}；" +
           $"跳过 {Skipped}，无效 {Invalid}，失败 {Failed}";
}

/// <summary>
/// 精灵包导入器：把用户自己下载的 ZIP（PokeAPI sprites / Showdown sprites）流式解压，
/// 按命名规则归类进 HOME 立绘与动态立绘两个缓存目录。
///
/// 识别规则（宽容匹配）：
///   *.png —— 路径含 home/（含 shiny/ 则为闪光），文件名须是 数字(-后缀) 形式
///   *.gif —— 路径含 ani-shiny/（闪光）或 ani/，文件名须是小写英文名(-后缀) 形式
/// 全程流式写盘，不整包读入内存；已有且合格的文件跳过（可重复导入）。
/// </summary>
public static class SpritePackInstaller
{
    private static readonly Regex HomeNamePattern = new(
        @"^\d+(-[a-z0-9]+(-[a-z0-9]+)*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnimNamePattern = new(
        @"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const int MinPngBytes = 8000;  // 小于此值基本是坏图/占位图
    private const int MinGifBytes = 2000;

    /// <summary>
    /// 导入一个精灵包 ZIP。<paramref name="progress"/> 报告 (已处理, 总数)。
    /// </summary>
    public static async Task<SpriteImportResult> ImportAsync(
        Stream zipStream,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var result = new SpriteImportResult();

        await HomeSpriteCacheService.EnsureMigratedAsync().ConfigureAwait(false);

        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        int total = 0;
        foreach (var e in zip.Entries)
            if (!IsDirectory(e)) total++;
        result.Total = total;

        int processed = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (IsDirectory(entry)) continue;

            processed++;
            progress?.Report((processed, total));

            try
            {
                var full  = entry.FullName.Replace('\\', '/');
                var lower = full.ToLowerInvariant();
                var ext   = Path.GetExtension(lower);
                var name  = Path.GetFileNameWithoutExtension(lower);

                if (ext == ".png")
                {
                    if (!lower.Contains("/home/") && !lower.StartsWith("home/")) continue; // 非 HOME 目录，忽略

                    if (!HomeNamePattern.IsMatch(name))
                    {
                        result.Invalid++;
                        continue;
                    }

                    bool shiny = lower.Contains("/shiny/") || lower.Contains("home/shiny");
                    var dest = Path.Combine(
                        HomeSpriteCacheService.CacheRootPath,
                        shiny ? "shiny" : "",
                        name + ".png");

                    if (IsAlreadyGood(dest, MinPngBytes))
                    {
                        result.Skipped++;
                        continue;
                    }

                    if (await CopyValidatedAsync(entry, dest, png: true, ct).ConfigureAwait(false))
                    {
                        if (shiny) result.HomeShiny++; else result.Home++;
                    }
                    else
                    {
                        result.Failed++;
                        if (result.Errors.Count < 20) result.Errors.Add("坏图：" + entry.FullName);
                    }
                }
                else if (ext == ".gif")
                {
                    if (!lower.Contains("/ani/") && !lower.StartsWith("ani/")
                        && !lower.Contains("/ani-shiny/") && !lower.StartsWith("ani-shiny/"))
                        continue;

                    if (!AnimNamePattern.IsMatch(name))
                    {
                        result.Invalid++;
                        continue;
                    }

                    bool shiny = lower.Contains("ani-shiny");
                    var dest = Path.Combine(
                        SpriteCacheService.CacheRootPath,
                        shiny ? "ani-shiny" : "ani",
                        name + ".gif");

                    if (IsAlreadyGood(dest, MinGifBytes))
                    {
                        result.Skipped++;
                        continue;
                    }

                    if (await CopyValidatedAsync(entry, dest, png: false, ct).ConfigureAwait(false))
                    {
                        if (shiny) result.AnimShiny++; else result.Anim++;
                    }
                    else
                    {
                        result.Failed++;
                        if (result.Errors.Count < 20) result.Errors.Add("坏图：" + entry.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Failed++;
                if (result.Errors.Count < 20) result.Errors.Add($"{entry.FullName}: {ex.Message}");
            }
        }

        return result;
    }

    private static bool IsDirectory(ZipArchiveEntry e)
        => e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');

    private static bool IsAlreadyGood(string path, int minBytes)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length >= minBytes;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 流式写盘 + 文件头魔数校验 + 体积下限校验，先写 .part 再改名，避免半截文件被当成缓存。
    /// </summary>
    private static async Task<bool> CopyValidatedAsync(
        ZipArchiveEntry entry, string dest, bool png, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(dest);
        if (dir is not null) Directory.CreateDirectory(dir);

        var tmp = dest + ".part";
        bool ok = false;
        try
        {
            await using (var src = entry.Open())
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                                     FileShare.None, 81920, useAsync: true))
            {
                var head = new byte[8];
                int read = await src.ReadAsync(head.AsMemory(), ct).ConfigureAwait(false);
                if (read < 4 || !HasMagic(head, read, png))
                    return false;

                await dst.WriteAsync(head.AsMemory(0, read), ct).ConfigureAwait(false);
                await src.CopyToAsync(dst, 65536, ct).ConfigureAwait(false);
            }

            var length = new FileInfo(tmp).Length;
            if (length < (png ? MinPngBytes : MinGifBytes))
                return false;

            File.Move(tmp, dest, overwrite: true);
            ok = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (!ok)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }

    private static bool HasMagic(byte[] head, int read, bool png)
    {
        if (png)
            return read >= 4 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47;

        return read >= 4 && head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'8';
    }
}
