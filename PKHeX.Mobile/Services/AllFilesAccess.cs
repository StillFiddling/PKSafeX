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
        try
        {
            var context = global::Android.App.Application.Context;
            var uri = global::Android.Net.Uri.Parse("package:" + context.PackageName);

            Intent intent;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission, uri);
            else
                intent = new Intent(Settings.ActionManageAllFilesAccessPermission);

            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch
        {
            try
            {
                var context = global::Android.App.Application.Context;
                var intent = new Intent(Settings.ActionManageAllFilesAccessPermission);
                intent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(intent);
            }
            catch { }
        }
#endif
    }

    public static bool AlreadyAskedOnce()
        => Preferences.Default.Get(PrefAsked, false);

    public static void MarkAsked()
        => Preferences.Default.Set(PrefAsked, true);
}
