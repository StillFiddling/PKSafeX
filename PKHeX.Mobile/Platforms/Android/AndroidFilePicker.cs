using Android.App;
using Android.Content;
using PKHeX.Mobile.Services;

namespace PKHeX.Mobile.Platforms.Android;

public class AndroidFilePicker : ISaveFilePicker
{
    /// <summary>
    /// 最近一次挑选到的文件是否拿到了写权限。
    /// 部分 provider 只支持只读，这里拿不到写授权时要让上层提示「这张卡只能看不能存」。
    /// </summary>
    public static bool LastPickWriteGranted { get; private set; } = true;

    public async Task<string?> PickFileAsync()
    {
        var activity = Platform.CurrentActivity;
        if (activity == null) return null;

        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        // 必须申请写权限：只申请读的话，这个文件的 URI 永远不能写回存档
        intent.AddFlags(ActivityFlags.GrantReadUriPermission
                      | ActivityFlags.GrantWriteUriPermission
                      | ActivityFlags.GrantPersistableUriPermission);

        var tcs = new TaskCompletionSource<string?>();

        void Handler(global::Android.Net.Uri? uri)
        {
            if (uri != null)
            {
                var resolver = activity.ContentResolver;
                bool write = false;

                // 先要读写；provider 不支持写时降级为只读，不要因为 SecurityException 崩掉
                try
                {
                    resolver?.TakePersistableUriPermission(uri,
                        ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                    write = true;
                }
                catch
                {
                    try
                    {
                        resolver?.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
                    }
                    catch { }
                }

                LastPickWriteGranted = write && HasWritePermission(uri);
                tcs.TrySetResult(uri.ToString());
            }
            else
            {
                LastPickWriteGranted = true;
                tcs.TrySetResult(null);
            }
            MainActivity.FilePickResult -= Handler;
        }

        MainActivity.FilePickResult += Handler;
        activity.StartActivityForResult(intent, MainActivity.RequestPickFile);

        return await tcs.Task;
    }

    /// <summary>探测当前进程对该 URI 是否有写授权；探测本身失败时按「有」处理，交给真正写入时报错。</summary>
    internal static bool HasWritePermission(global::Android.Net.Uri uri)
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            return ctx.CheckUriPermission(uri, global::Android.OS.Process.MyPid(), global::Android.OS.Process.MyUid(),
                       ActivityFlags.GrantWriteUriPermission)
                   == global::Android.Content.PM.Permission.Granted;
        }
        catch
        {
            return true;
        }
    }
}

