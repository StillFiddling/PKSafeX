namespace PKHeX.Mobile.Services;

/// <summary>
/// MAUI implementation of <see cref="IFileService"/> using the platform file picker and share sheet.
/// </summary>
public sealed class FileService : IFileService
{
    public async Task<(Memory<byte> Data, string FileName)?> PickFileAsync()
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Select a Pokémon save file",
        });

        if (result is null)
            return null;

        await using var stream = await result.OpenReadAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        return (ms.ToArray(), result.FileName);
    }

    public async Task ExportFileAsync(byte[] data, string fileName)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllBytesAsync(path, data);

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = "Export save file",
            File = new ShareFile(path),
        });
    }

    /// <summary>
    /// 读取原文件当前内容（用于写回前的「外部是否已更新」比对）。
    /// 同时支持 SAF 的 content:// URI 和全自动扫描得到的普通文件路径。
    /// </summary>
    public async Task<byte[]?> ReadBackAsync(string fileUri)
    {
#if ANDROID
        if (!IsContentUri(fileUri))
        {
            try
            {
                if (!File.Exists(fileUri)) return null;
                return await File.ReadAllBytesAsync(fileUri);
            }
            catch
            {
                return null;
            }
        }

        return await Task.Run<byte[]?>(() =>
        {
            var context = Android.App.Application.Context;
            var uri = global::Android.Net.Uri.Parse(fileUri);
            if (uri is null) return null;
            using var stream = context.ContentResolver?.OpenInputStream(uri);
            if (stream is null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        });
#else
        if (!File.Exists(fileUri)) return null;
        return await File.ReadAllBytesAsync(fileUri);
#endif
    }

    public async Task WriteBackAsync(byte[] data, string fileUri)
    {
#if ANDROID
        if (!IsContentUri(fileUri))
        {
            // file:// 前缀统一降成路径，交给文件系统直写
            var path = fileUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? (global::Android.Net.Uri.Parse(fileUri)?.Path ?? fileUri)
                : fileUri;

            try
            {
                await File.WriteAllBytesAsync(path, data);
            }
            catch (UnauthorizedAccessException)
            {
                throw new UnauthorizedAccessException(
                    "系统拒绝写入这个路径。请确认已授予「所有文件访问」权限（首页顶部的授权条），或改用「添加文件」的方式加入这张存档。");
            }
            return;
        }

        var context = global::Android.App.Application.Context;
        var uri = global::Android.Net.Uri.Parse(fileUri)
            ?? throw new InvalidOperationException("Invalid file URI.");

        // 写前探测：只读授权时 OpenOutputStream 只会甩一句英文的 Permission Denial，
        // 这里提前判定并给出能照做的中文原因。
        if (!HasWritePermission(context, uri))
        {
            throw new UnauthorizedAccessException(
                "这张存档是以「只读」方式加入的，软件没有写入权限。\n"
                + "请到「文件夹管理 → 添加文件」重新选择它（选择器里允许修改），"
                + "或用「添加文件夹」把整个存档目录加进来后再写入。");
        }

        await Task.Run(() =>
        {
            using var stream = context.ContentResolver?.OpenOutputStream(uri, "wt")
                ?? throw new InvalidOperationException("Could not open output stream.");
            stream.Write(data, 0, data.Length);
            stream.Flush();
        });
#else
        await File.WriteAllBytesAsync(fileUri, data);
#endif
    }

#if ANDROID
    /// <summary>探测进程对该 URI 是否有写授权；探测本身出错时按「有」处理，交给真正写入时报错。</summary>
    private static bool HasWritePermission(global::Android.Content.Context context, global::Android.Net.Uri uri)
    {
        try
        {
            return context.CheckUriPermission(uri, global::Android.OS.Process.MyPid(), global::Android.OS.Process.MyUid(),
                       global::Android.Content.ActivityFlags.GrantWriteUriPermission)
                   == global::Android.Content.PM.Permission.Granted;
        }
        catch
        {
            return true;
        }
    }
#endif

    private static bool IsContentUri(string fileUri)
        => fileUri.StartsWith("content://", StringComparison.OrdinalIgnoreCase);
}
