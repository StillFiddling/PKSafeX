#if ANDROID
using AndroidX.Activity;
using Microsoft.Maui.ApplicationModel;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 拦截 Android 系统返回（返回键 + 手势/预测性返回）。
///
/// MAUI 的 Page.OnBackButtonPressed 在 Android 13+ 的预测性返回手势下不一定被调用，
/// 手势会直接走 Activity 的 OnBackPressedDispatcher 把页面 pop 掉。
/// 这里在 dispatcher 上挂一个回调，Enabled 由页面按需开关：
/// 道具页/编辑页「有未保存改动」时打开，正常状态关掉，不影响其它页面的返回。
/// </summary>
public sealed class SystemBackGate : OnBackPressedCallback
{
    private readonly Func<bool> _handler;

    private SystemBackGate(Func<bool> handler) : base(false) => _handler = handler;

    /// <summary>在当前 Activity 上注册回调（默认关闭）；没有 Activity 时返回 null。</summary>
    public static SystemBackGate? Attach(Func<bool> handler)
    {
        var activity = Platform.CurrentActivity;
        if (activity is not ComponentActivity component) return null;
        if (component is not AndroidX.Lifecycle.ILifecycleOwner owner) return null;

        var gate = new SystemBackGate(handler);
        try { component.OnBackPressedDispatcher.AddCallback(owner, gate); }
        catch { return null; }
        return gate;
    }

    public static void Detach(SystemBackGate? gate)
    {
        if (gate is null) return;
        gate.Enabled = false;
        try { gate.Remove(); } catch { }
    }

    public override void HandleOnBackPressed()
        => _handler();
}
#endif
