namespace PKHeX.Mobile.Services;

/// <summary>
/// 精灵资源的多源地址表。
/// 每个资源给出"主源 → 镜像 → 备用 CDN"的有序列表，下载时逐个尝试，成功即停。
/// 国内网络下 raw.githubusercontent.com 基本不可达，靠 gh-proxy / jsDelivr 兜底。
/// </summary>
public static class SpriteSourceConfig
{
    /// <summary>通用镜像前缀：https://gh-proxy.com/https://raw.githubusercontent.com/...</summary>
    public static string Proxied(string url) => "https://gh-proxy.com/" + url;

    private const string HomeBase =
        "https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/other/home";

    private const string ShowdownBase = "https://play.pokemonshowdown.com/sprites";

    /// <summary>HOME 立绘候选地址（不含网格外的形态后缀判断）。</summary>
    public static string[] ForHome(string slug, bool shiny)
    {
        var path = shiny ? $"shiny/{slug}.png" : $"{slug}.png";
        var primary  = $"{HomeBase}/{path}";
        var jsDelivr = $"https://cdn.jsdelivr.net/gh/PokeAPI/sprites@master/sprites/pokemon/other/home/{path}";
        return
        [
            primary,
            Proxied(primary),
            jsDelivr,
        ];
    }

    /// <summary>动态立绘候选地址（Showdown ani / gen5ani 两套命名）。</summary>
    public static string[] ForAnim(string slug, bool shiny)
    {
        var folder     = shiny ? "ani-shiny" : "ani";
        var gen5Folder = shiny ? "gen5ani-shiny" : "gen5ani";
        var u1 = $"{ShowdownBase}/{folder}/{slug}.gif";
        var u2 = $"{ShowdownBase}/{gen5Folder}/{slug}.gif";
        return
        [
            u1,
            u2,
            Proxied(u1),
            Proxied(u2),
        ];
    }
}
