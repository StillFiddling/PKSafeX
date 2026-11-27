namespace PKHeX.Mobile.Services;

/// <summary>
/// 全局功能开关（运行时只读：改一行即可整体切换，且不会让编译器折叠代码分支）。
/// </summary>
public static class AppFlags
{
    /// <summary>
    /// 双屏（Thor 类双屏掌机）支持总开关。
    /// 目前设为 false：横屏版式还在优化，双屏机器的副屏先不用，
    /// 主屏按单屏横屏跑，并提示一次「暂不支持双屏」。
    /// 横屏优化完成后把它改回 true 即恢复双屏（ThorSecondaryDisplay / SecondScreenPage 代码都还在）。
    /// （用 static readonly 而不是 const：const 会让 if 里的注册代码变成不可达，产生 CS0162。）
    /// </summary>
    public static readonly bool DualScreenEnabled = false;
}
