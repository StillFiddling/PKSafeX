using Android.App;
using Android.Hardware.Display;

namespace PKHeX.Mobile.Platforms.Android;

/// <summary>
/// 硬件层面的「有没有第二块屏」探测，独立于 <see cref="Services.ISecondaryDisplay"/>。
/// 双屏支持被关掉（<see cref="Services.AppFlags.DualScreenEnabled"/> = false）时，
/// 服务里查不到副屏了，但还需要知道对方是不是双屏机——用来给一次性提示 + 走横屏。
/// 判定规则与 ThorSecondaryDisplay.ResolveDisplay 一致：Presentation 类目优先，其次 GetDisplays()[1]。
/// </summary>
public static class DualDisplayProbe
{
    private static bool? _hasSecondary;

    public static bool HasSecondaryDisplay()
    {
        if (_hasSecondary is not null)
            return _hasSecondary.Value;

        try
        {
            var context = global::Android.App.Application.Context;
            var dm = context.GetSystemService(global::Android.Content.Context.DisplayService) as DisplayManager;
            if (dm is null)
            {
                _hasSecondary = false;
                return false;
            }

            var pres = dm.GetDisplays(DisplayManager.DisplayCategoryPresentation);
            if (pres?.Length > 0)
            {
                _hasSecondary = true;
                return true;
            }

            var all = dm.GetDisplays();
            _hasSecondary = all is { Length: > 1 };
            return _hasSecondary.Value;
        }
        catch (Exception ex)
        {
            Services.CrashLog.Write("DualDisplayProbe", ex);
            _hasSecondary = false;
            return false;
        }
    }
}
