using PKHeX.Core;
using PKHeX.Drawing.Mobile.Sprites;
using SkiaSharp;

namespace PKHeX.Mobile.Services;

/// <summary>
/// Loads Pokémon sprites from MAUI raw assets bundled in the APK.
/// Sprites are accessed via FileSystem.OpenAppPackageFileAsync("sprites/{name}.png").
/// </summary>
public sealed class FileSystemSpriteRenderer : PKHeX.Drawing.Mobile.Sprites.ISpriteRenderer
{
    private readonly Dictionary<string, SKBitmap> _cache = new();
    private SKBitmap? _empty;

    public int Width => 68;
    public int Height => 56;

    /// <summary>
    /// Pre-loads sprites for all non-empty slots in a box so painting is cache-hit only.
    /// HOME sprites are downloaded in parallel first; bundled sprites fill any gaps.
    /// Call this before InvalidateSurface.
    /// </summary>
    public async Task PreloadBoxAsync(PKM[] box)
        => await PreloadAsync(box, allowNetwork: true, CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// 离线优先预加载：只读磁盘已有 HOME 图 + APK 内置图，绝不发起网络请求。
    /// 图鉴/首页首帧用这个，避免在境外源不可达时进页面卡住。
    /// </summary>
    public Task PreloadLocalAsync(PKM[] box, CancellationToken ct = default)
        => PreloadAsync(box, allowNetwork: false, ct);

    /// <summary>是否为空白占位图（用于画"缺图"提示）。</summary>
    public bool IsBlank(SKBitmap bmp) => _empty is not null && ReferenceEquals(bmp, _empty);

    private async Task PreloadAsync(PKM[] box, bool allowNetwork, CancellationToken ct)
    {
        // 有网模式下先并行取 HOME（网络或磁盘），离线模式只读磁盘
        if (allowNetwork)
        {
            var homeSlots = box
                .Where(pk => pk.Species != 0)
                .Select(pk => ((ushort)pk.Species, pk.Form, pk.IsShiny));
            await HomeSpriteCacheService.PreloadAsync(homeSlots).ConfigureAwait(false);
        }

        // Fall back: load bundled sprites for anything HOME couldn't provide
        foreach (var pk in box)
        {
            ct.ThrowIfCancellationRequested();
            if (pk.Species == 0)
                continue;

            // Skip bundled load if HOME already has this one (磁盘缓存即可，不联网)
            if (HomeSpriteCacheService.TryGetFromDisk((ushort)pk.Species, pk.Form, pk.IsShiny) is not null)
                continue;

            var key = BuildKey(pk);
            if (!_cache.ContainsKey(key))
            {
                var bmp = await TryLoadAsync(key);
                // Form sprite missing — alias to form 0 so slot isn't blank
                if (bmp is null && pk.Form != 0)
                {
                    var baseKey = BuildBaseKey(pk);
                    if (!_cache.ContainsKey(baseKey))
                        await TryLoadAsync(baseKey);
                    if (_cache.TryGetValue(baseKey, out var baseBmp))
                        _cache[key] = baseBmp;
                }
            }
        }
    }

    public SKBitmap GetSprite(PKM pk)
    {
        // Prefer high-quality HOME sprite for all forms
        var home = HomeSpriteCacheService.GetCached((ushort)pk.Species, pk.Form, pk.IsShiny);
        if (home is not null) return home;

        // Fall back to bundled 2D sprite
        var key = BuildKey(pk);
        if (_cache.TryGetValue(key, out var bmp)) return bmp;
        if (pk.Form != 0 && _cache.TryGetValue(BuildBaseKey(pk), out var baseBmp))
            return baseBmp;
        return GetEmptySprite();
    }

    public SKBitmap GetSprite(ushort species, byte form, byte gender, uint formArg, bool shiny, EntityContext context)
    {
        // 图鉴等只给物种信息的调用：先看磁盘 HOME（同步、不联网）
        var home = HomeSpriteCacheService.TryGetFromDisk(species, form, shiny);
        if (home is not null) return home;

        var key = "b" + SpriteName.GetResourceStringSprite(species, form, gender, formArg, context, shiny);
        return _cache.GetValueOrDefault(key) ?? GetEmptySprite();
    }

    public SKBitmap GetBallSprite(byte ball)
    {
        var key = SpriteName.GetResourceStringBall(ball);
        return _cache.GetValueOrDefault(key) ?? GetEmptySprite();
    }

    public SKBitmap GetEmptySprite()
    {
        if (_empty is not null)
            return _empty;
        var bmp = new SKBitmap(Width, Height);
        bmp.Erase(SKColors.Transparent);
        return _empty = bmp;
    }

    private static string BuildKey(PKM pk)
    {
        uint formArg = pk is IFormArgument fa ? fa.FormArgument : 0u;
        return "b" + SpriteName.GetResourceStringSprite(pk.Species, pk.Form, pk.Gender, formArg, pk.Context, pk.IsShiny);
    }

    // Fallback key: form 0, no formArg — used when a form-specific sprite file is absent
    private static string BuildBaseKey(PKM pk)
        => "b" + SpriteName.GetResourceStringSprite(pk.Species, 0, pk.Gender, 0u, pk.Context, pk.IsShiny);

    private async Task<SKBitmap?> TryLoadAsync(string name)
    {
        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync($"sprites/{name}.png");
            var bmp = SKBitmap.Decode(stream);
            if (bmp is not null)
                _cache[name] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
