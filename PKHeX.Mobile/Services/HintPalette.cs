using Microsoft.Maui.Graphics;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 底部提示条键帽的强调色（全应用共用一套，改主题时只改这里）。
/// </summary>
public static class HintPalette
{
    public static readonly Color AccentConfirm = Color.FromArgb("#34D990"); // A 选中
    public static readonly Color AccentGrab    = Color.FromArgb("#6BABFF"); // Y 抓取
    public static readonly Color AccentCancel  = Color.FromArgb("#FF6B9D"); // B 返回
    public static readonly Color AccentCry     = Color.FromArgb("#60C0FF"); // R 叫声
    public static readonly Color AccentSave    = Color.FromArgb("#FFC46B"); // ≡ 保存
    public static readonly Color AccentSearch  = Color.FromArgb("#A78BFA"); // X 搜索

    public static readonly Color KeyCapBg     = Color.FromArgb("#0B1524");
    public static readonly Color HintText     = Color.FromArgb("#C8D2E8");
    public static readonly Color HintFallbackBg     = Color.FromArgb("#132038");
    public static readonly Color HintFallbackStroke = Color.FromArgb("#2A3A5A");
}
