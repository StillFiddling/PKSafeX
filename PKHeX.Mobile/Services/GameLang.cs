using PKHeX.Core;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 统一的字符串表：界面与数据默认走简体中文（PKHeX.Core 自带 zh-Hans 全量表），
/// 需要英文名的场景（叫声/立绘 slug）单独走 <see cref="English"/>。
/// </summary>
public static class GameLang
{
    public static readonly GameStrings English = GameInfo.GetStrings("en");

    public static readonly GameStrings Strings = LoadChinese();

    private static GameStrings LoadChinese()
    {
        foreach (var lang in new[] { "zh-Hans", "zh", "en" })
        {
            try
            {
                var s = GameInfo.GetStrings(lang);
                if (s is not null) return s;
            }
            catch { /* 该语言表不可用时继续尝试下一个 */ }
        }
        return English;
    }
}
