using System.Collections.Concurrent;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 叫声统一下载与播放。
/// - 缓存目录用 <see cref="FileSystem.AppDataDirectory"/>/cries（持久，不会被系统清缓存清掉）；
///   首次访问某个 slug 时会尝试从旧的 CacheDirectory/cries 迁移，避免老缓存白丢。
/// - 下载先写 .tmp 再 Move，杜绝「写到一半被杀 → 0 字节文件被 File.Exists 当成有效文件 → 永久静默」。
/// - 同一 slug 的并发请求合并成一个任务，预取与点播不会重复下载。
/// - 播放用单例 MediaPlayer + Reset/SetDataSource/Prepare/Start，避免每次 new/Release 原生解码器；
///   音量走 USAGE_GAME，不申请焦点属已知取舍（后台音乐仍会继续）。
/// </summary>
public static class CryService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, Task<bool>> InFlight = new();

#if ANDROID
    private static Android.Media.MediaPlayer? _player;
#endif

    public static string Slug(string speciesName)
        => string.IsNullOrWhiteSpace(speciesName) ? "" : SpriteCacheService.ToShowdownSlug(speciesName);

    private static string Dir => Path.Combine(FileSystem.AppDataDirectory, "cries");

    private static string PathFor(string slug) => Path.Combine(Dir, slug + ".ogg");

    private static bool IsUsable(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch { return false; }
    }

    /// <summary>后台预取：打开详情/选中格子时调用，点播放时文件大概率已经在本地。</summary>
    public static void Prefetch(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return;
        _ = EnsureAsync(slug);
    }

    /// <summary>确保叫声文件已落地；成功返回本地路径，失败返回 null。</summary>
    public static async Task<string?> EnsureAsync(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var path = PathFor(slug);
        if (IsUsable(path)) return path;

        TryMigrateLegacy(slug, path);
        if (IsUsable(path)) return path;

        var task = InFlight.GetOrAdd(slug, s => DownloadAsync(s));
        bool ok;
        try { ok = await task.ConfigureAwait(false); }
        finally { InFlight.TryRemove(slug, out _); }

        return ok && IsUsable(path) ? path : null;
    }

    private static async Task<bool> DownloadAsync(string slug)
    {
        var path = PathFor(slug);
        var tmp  = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Dir);
            var bytes = await Http.GetByteArrayAsync(
                $"https://play.pokemonshowdown.com/audio/cries/{slug}.ogg").ConfigureAwait(false);
            if (bytes.Length == 0) return false;
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, path, true);
            return true;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    private static void TryMigrateLegacy(string slug, string path)
    {
        try
        {
            var legacy = Path.Combine(FileSystem.CacheDirectory, "cries", slug + ".ogg");
            if (!IsUsable(legacy)) return;
            Directory.CreateDirectory(Dir);
            File.Copy(legacy, path, true);
        }
        catch { /* 迁移失败就当没有，走下载 */ }
    }

    /// <summary>播放某个叫声；返回 false 表示文件拿不到或起播失败（调用方应清掉防抖标记以便重按重试）。</summary>
    public static async Task<bool> PlayAsync(string slug)
    {
        var path = await EnsureAsync(slug).ConfigureAwait(false);
        if (path is null) return false;

#if ANDROID
        return await MainThread.InvokeOnMainThreadAsync(() =>
        {
            try
            {
                _player ??= NewPlayer();
                _player.Reset();
                _player.SetDataSource(path);
                _player.Prepare();     // 本地小文件，开销很小
                _player.Start();
                return true;
            }
            catch
            {
                try { Release(); } catch { }
                return false;
            }
        });
#else
        return false;
#endif
    }

#if ANDROID
    private static Android.Media.MediaPlayer NewPlayer()
    {
        var p = new Android.Media.MediaPlayer();
        try
        {
            // Builder 的链式 setter 在绑定里是可空的，逐步兜回自身，避免解引用告警
            var builder = new Android.Media.AudioAttributes.Builder();
            var usage   = builder.SetUsage(Android.Media.AudioUsageKind.Game) ?? builder;
            var typed   = usage.SetContentType(Android.Media.AudioContentType.Sonification) ?? usage;
            var attrs   = typed.Build();
            if (attrs is not null) p.SetAudioAttributes(attrs);
        }
        catch { }
        return p;
    }

    /// <summary>停止播放（页面退出时调用，避免切页后还在响、也避免叠音）。</summary>
    public static void Stop()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { _player?.Stop(); } catch { }
            try { _player?.Reset(); } catch { }
        });
    }

    public static void Release()
    {
        try { _player?.Stop(); } catch { }
        try { _player?.Reset(); } catch { }
        try { _player?.Release(); } catch { }
        _player = null;
    }
#endif
}
