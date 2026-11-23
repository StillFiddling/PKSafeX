using System.IO.Compression;
using System.Text.Json;
using PKHeX.Core;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 把上次扫描到的存档列表（含已解析的存档内容）缓存到应用私有目录，
/// 启动时先秒开列表，再在后台校验「有变化的文件」，避免每次重进都全量重扫。
/// </summary>
public static class SaveIndexCache
{
    private const string FileName    = "save_index.bin";
    private const int    Schema      = 2;
    private const long   MaxTotalRaw = 48L * 1024 * 1024;   // 超过就不缓存，避免吃满空间

    private sealed record Row(
        string Uri, string Name, string Dir,
        string Version, int Gen, string OT, string TID, string Play,
        int Boxes, int Slots, long ModUnixMs, byte[] Data);

    private static string Path2 =>
        Path.Combine(FileSystem.AppDataDirectory, FileName);

    public static void Save(List<SaveEntry> entries)
    {
        try
        {
            long total = entries.Sum(e => (long)e.RawData.Length);
            if (entries.Count == 0 || total > MaxTotalRaw)
            {
                Clear();
                return;
            }

            var rows = entries.Select(e => new Row(
                e.FileUri, e.FileName, e.DirectoryUri,
                e.Version.ToString(), e.Generation, e.TrainerName, e.TrainerID, e.PlayTime,
                e.BoxCount, e.SlotCount, e.LastModified.ToUnixTimeMilliseconds(),
                e.RawData)).ToList();

            var payload = JsonSerializer.SerializeToUtf8Bytes(new { schema = Schema, rows });

            using var fs = File.Create(Path2);
            using var gz = new GZipStream(fs, CompressionLevel.Fastest);
            gz.Write(payload);
        }
        catch (Exception ex)
        {
            CrashLog.Write("SaveIndexCache.Save", ex);
        }
    }

    public static List<SaveEntry>? Load()
    {
        try
        {
            if (!File.Exists(Path2)) return null;

            using var fs = File.OpenRead(Path2);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);

            using var doc = JsonDocument.Parse(ms.ToArray());
            var root = doc.RootElement;
            if (root.GetProperty("schema").GetInt32() != Schema) return null;

            var list = new List<SaveEntry>();
            foreach (var r in root.GetProperty("rows").EnumerateArray())
            {
                var version = Enum.TryParse<GameVersion>(r.GetProperty("Version").GetString(), out var v)
                    ? v : GameVersion.GP;
                list.Add(new SaveEntry(
                    FileUri:      r.GetProperty("Uri").GetString() ?? "",
                    FileName:     r.GetProperty("Name").GetString() ?? "",
                    DirectoryUri: r.GetProperty("Dir").GetString() ?? "",
                    Version:      version,
                    Generation:   r.GetProperty("Gen").GetInt32(),
                    TrainerName:  r.GetProperty("OT").GetString() ?? "",
                    TrainerID:    r.GetProperty("TID").GetString() ?? "",
                    PlayTime:     r.GetProperty("Play").GetString() ?? "",
                    BoxCount:     r.GetProperty("Boxes").GetInt32(),
                    SlotCount:    r.GetProperty("Slots").GetInt32(),
                    RawData:      Convert.FromBase64String(r.GetProperty("Data").GetString() ?? ""),
                    LastModified: DateTimeOffset.FromUnixTimeMilliseconds(r.GetProperty("ModUnixMs").GetInt64())));
            }
            return list.Count > 0 ? list : null;
        }
        catch (Exception ex)
        {
            CrashLog.Write("SaveIndexCache.Load", ex);
            return null;
        }
    }

    public static void Clear()
    {
        try { if (File.Exists(Path2)) File.Delete(Path2); } catch { }
    }
}
