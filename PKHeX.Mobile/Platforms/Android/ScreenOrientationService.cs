using Android.Content.PM;

namespace PKHeX.Mobile.Platforms.Android;

/// <summary>
/// 屏幕方向三态开关：跟随系统 / 强制竖屏 / 强制横屏。
///
/// 以前是在清单里写死 android:screenOrientation，等于替玩家做决定；
/// 现在改为运行时 RequestedOrientation，玩家自己在设置里选，改完即时生效，不用重启。
/// 默认值是「强制竖屏」——多数人在手机上就是这么用的，
/// 也顺手解决「每次装完包，系统又把旋转开关抢回去」那个老问题。
/// </summary>
public static class ScreenOrientationService
{
    public const string KeyOrientation = "screen_orientation";

    public const string ModeAuto = "auto";
    public const string ModePortrait = "portrait";
    public const string ModeLandscape = "landscape";

    /// <summary>默认强制竖屏。</summary>
    public const string DefaultMode = ModePortrait;

    public static string Current =>
        Microsoft.Maui.Storage.Preferences.Default.Get(KeyOrientation, DefaultMode);

    /// <summary>启动时按偏好应用一次。</summary>
    public static void ApplySaved() => Apply(Current);

    /// <summary>写入偏好并立刻应用。</summary>
    public static void Set(string mode)
    {
        Microsoft.Maui.Storage.Preferences.Default.Set(KeyOrientation, mode);
        Apply(mode);
    }

    /// <summary>应用指定方向；窗口 Activity 还没就绪时静默跳过（下次启动会补上）。</summary>
    public static void Apply(string mode)
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is not global::Android.App.Activity act) return;

        act.RunOnUiThread(() =>
        {
            try
            {
                act.RequestedOrientation = mode switch
                {
                    ModePortrait => ScreenOrientation.Portrait,
                    ModeLandscape => ScreenOrientation.Landscape,
                    _ => ScreenOrientation.Unspecified,   // 跟随系统，尊重玩家自己的旋转锁
                };
            }
            catch (Exception ex)
            {
                PKHeX.Mobile.Services.CrashLog.Write("ScreenOrientationService.Apply", ex);
            }
        });
    }

    /// <summary>中文显示名，给设置页用。</summary>
    public static string DisplayName(string mode) => mode switch
    {
        ModePortrait => "强制竖屏",
        ModeLandscape => "强制横屏",
        _ => "跟随系统",
    };
}
