using Android.Content;
using Android.Net;
using Android.Provider;
using Microsoft.Maui.ApplicationModel;
using Environment = Android.OS.Environment;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 「所有文件访问权限」（MANAGE_EXTERNAL_STORAGE）。
/// 只有拿到它才能直接读 <c>/storage/emulated/0/Android/data/…</c>，
/// 而 Citra / Azahar / melonDS / Pizza Boy 等模拟器的存档基本都在那里面；
/// 系统的「选文件夹」在 Android 11+ 是禁止访问 Android/data 的。
/// </summary>
public static class AllFilesAccess
{
    private const string PrefAsked = "all_files_asked";

    public static bool IsGranted()
    {
#if ANDROID
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                return Environment.IsExternalStorageManager;
            return true; // Android 10 及以下：沙盒外可直接读写
        }
        catch
        {
            return false;
        }
#else
        return true;
#endif
    }

    /// <summary>跳到系统的授权页（用户需要手动打开开关）。</summary>
    public static void OpenSettings()
    {
#if ANDROID
        // 这两个「所有文件访问」系统页都是 API 30+ 才有：用 SDKInt 判断（CA1416 能识别），
        // 低版本退到应用详情页，别让老机器走到不存在的 Activity。
        try
        {
            var context = global::Android.App.Application.Context;
            var pkgUri  = global::Android.Net.Uri.Parse("package:" + context.PackageName);

            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.R)
            {
                // 上面已用 SDKInt 做了运行时版本判断，低版本不会走到这里；
                // 但 CA1416 认不出 Build.VERSION.SdkInt 这种写法，就地抑制。
#pragma warning disable CA1416
                try
                {
                    var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission, pkgUri);
                    intent.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(intent);
                    return;
                }
                catch { }

                try
                {
                    var intent = new Intent(Settings.ActionManageAllFilesAccessPermission, pkgUri);
                    intent.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(intent);
                    return;
                }
                catch { }
#pragma warning restore CA1416
            }

            var details = new Intent(Settings.ActionApplicationDetailsSettings, pkgUri);
            details.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(details);
        }
        catch { }
#endif
    }

    public static bool AlreadyAskedOnce()
        => Preferences.Default.Get(PrefAsked, false);

    public static void MarkAsked()
        => Preferences.Default.Set(PrefAsked, true);
}
