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
            await File.WriteAllBytesAsync(fileUri, data);
            return;
        }

        await Task.Run(() =>
        {
            var context = Android.App.Application.Context;
            var uri = global::Android.Net.Uri.Parse(fileUri)
                ?? throw new InvalidOperationException("Invalid file URI.");
            using var stream = context.ContentResolver?.OpenOutputStream(uri, "wt")
                ?? throw new InvalidOperationException("Could not open output stream.");
            stream.Write(data, 0, data.Length);
            stream.Flush();
        });
#else
        await File.WriteAllBytesAsync(fileUri, data);
#endif
    }

    private static bool IsContentUri(string fileUri)
        => fileUri.StartsWith("content://", StringComparison.OrdinalIgnoreCase);
}
