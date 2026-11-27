using Android.App;
using Android.Content;
using PKHeX.Mobile.Services;

namespace PKHeX.Mobile.Platforms.Android;

public class AndroidDirectoryPicker : IDirectoryPicker
{
    /// <summary>最近一次挑选的目录是否拿到了写权限（拿不到时该目录下的存档只能读）。</summary>
    public static bool LastTreeWriteGranted { get; private set; } = true;

    public async Task<string?> PickDirectoryAsync()
    {
        var activity = Platform.CurrentActivity;
        if (activity == null) return null;

        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);

        var tcs = new TaskCompletionSource<string?>();

        void Handler(global::Android.Net.Uri? uri)
        {
            if (uri != null)
            {
                var resolver = activity.ContentResolver;

                // 先要读写；provider 不支持持久化/写入时降级，别让 SecurityException 直接冒到 UI
                try
                {
                    resolver?.TakePersistableUriPermission(uri,
                        ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                }
                catch
                {
                    try
                    {
                        resolver?.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
                    }
                    catch { }
                }

                LastTreeWriteGranted = AndroidFilePicker.HasWritePermission(uri);
                tcs.TrySetResult(uri.ToString());
            }
            else
            {
                LastTreeWriteGranted = true;
                tcs.TrySetResult(null);
            }
            MainActivity.DirectoryPickResult -= Handler;
        }

        MainActivity.DirectoryPickResult += Handler;
        activity.StartActivityForResult(intent, MainActivity.RequestPickDirectory);

        return await tcs.Task;
    }
}

