namespace PKHeX.Mobile.Services;

/// <summary>
/// 系统返回（返回键 / 手势 + 预测性返回）的统一入口。
///
/// 目标：让「滑动返回」与手柄 B 键走完全同一套逻辑 —— 先关浮层/菜单，
/// 有未保存改动就先问，最后才真的返回上一页。
///
/// 各页面在 OnAppearing 里 <see cref="Bind"/> 自己的返回动作（返回 true 表示已消费），
/// 在 OnDisappearing 里 <see cref="Unbind"/>。没有页面注册时，MainActivity 里的
/// 全局返回回调兜底执行「返回上一页」。
/// </summary>
public static class BackCoordinator
{
    private static Func<bool>? _handler;

    public static void Bind(Func<bool> handler) => _handler = handler;

    /// <summary>只在本页仍是当前注册者时解绑，避免页面切换时误清掉新页面的处理器。</summary>
    public static void Unbind(Func<bool>? handler = null)
    {
        if (handler is null || _handler == handler)
            _handler = null;
    }

    /// <returns>true = 已被当前页面消费，调用方不要再 pop。</returns>
    public static bool TryHandle()
    {
        try { return _handler?.Invoke() ?? false; }
        catch { return false; }
    }
}
