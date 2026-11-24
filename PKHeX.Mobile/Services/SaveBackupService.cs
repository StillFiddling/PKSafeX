using System;
using System.IO;
using System.Linq;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 写存档前的自动备份：把磁盘上「这次写回之前」的原始内容存一份到应用私有目录，
/// 万一写回出问题或玩家发现被改坏，还能找回上一份。
/// 位置：应用私有 files/savebackup/（玩家看不到、也不会污染模拟器存档目录），
/// 设置页「存档备份」可以看到当前保留了几份。
/// </summary>
public static class SaveBackupService
{
    private const int KeepCount = 20;
    private const string PrefLastBackup = "save_backup_last";

    public static string BackupRoot => Path.Combine(FileSystem.AppDataDirectory, "savebackup");

    /// <summary>
    /// 备份一份存档内容（调用方传入的是「尚未写入」的磁盘原始字节）。
    /// 失败不影响主流程，返回 null。
    /// </summary>
    public static string? Backup(string fileUri, string fileName, byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        try
        {
            Directory.CreateDirectory(BackupRoot);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var safeName = SanitizeName(Path.GetFileNameWithoutExtension(fileName));
            var hash = SaveSyncService.Fingerprint(bytes)[..8];
            var path = Path.Combine(BackupRoot, $"{stamp}_{safeName}_{hash}.sav");
            File.WriteAllBytes(path, bytes);

            Preferences.Default.Set(PrefLastBackup, path);
            Prune();
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>当前保留的备份数量。</summary>
    public static int Count()
    {
        try
        {
            if (!Directory.Exists(BackupRoot)) return 0;
            return Directory.EnumerateFiles(BackupRoot, "*.sav").Count();
        }
        catch { return 0; }
    }

    /// <summary>最近一次备份的路径（没有就返回空字符串）。</summary>
    public static string LastPath => Preferences.Default.Get(PrefLastBackup, "");

    public static string LastName
    {
        get
        {
            var p = LastPath;
            return string.IsNullOrEmpty(p) ? "" : Path.GetFileName(p);
        }
    }

    /// <summary>最早一次备份的时间，用来告诉玩家大概覆盖了多久。</summary>
    public static string OldestName
    {
        get
        {
            try
            {
                if (!Directory.Exists(BackupRoot)) return "";
                var files = Directory.EnumerateFiles(BackupRoot, "*.sav").ToList();
                return files.Count == 0 ? "" : Path.GetFileName(files.Min());
            }
            catch { return ""; }
        }
    }

    private static void Prune()
    {
        try
        {
            var files = Directory.EnumerateFiles(BackupRoot, "*.sav").ToList();
            if (files.Count <= KeepCount) return;
            foreach (var f in files.OrderByDescending(Path.GetFileName).Skip(KeepCount))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "save";
        var clean = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (clean.Length == 0) clean = "save";
        return clean.Length > 20 ? clean[..20] : clean;
    }
}
