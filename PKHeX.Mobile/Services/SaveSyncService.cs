using System.Security.Cryptography;
using System.Threading.Tasks;
using PKHeX.Core;

namespace PKHeX.Mobile.Services;

public enum SaveWriteResult
{
    /// <summary>磁盘上的存档与内存基线一致，已直接写回内存版本。</summary>
    Written,

    /// <summary>磁盘上的存档已被游戏/其它程序更新，已把本次改动合并到最新副本后写回。</summary>
    MergedIntoLatest,

    /// <summary>磁盘上的存档已更新且无法解析，未做任何写入（需用户重新加载）。</summary>
    StaleUnreadable,

    /// <summary>没有可用的文件 URI。</summary>
    NoUri,
}

/// <summary>
/// 存档读写同步。核心原则：<b>绝不把内存里的旧快照整份覆盖到已被游戏更新过的文件上</b>。
///
/// 典型场景：软件加载存档 → 玩家进游戏继续玩并保存 → 回软件操作（存入/取出银行、编辑）
/// → 若直接 Write() 内存快照，玩家在游戏里的进度会被整段覆盖。
///
/// 这里的做法：写回前先读磁盘当前内容并比对指纹；
/// 若发现文件已变，则把「本次改动」重新应用到磁盘最新副本上，再写回。
/// </summary>
public static class SaveSyncService
{
    /// <summary>计算存档内容指纹（SHA-256 十六进制）。</summary>
    public static string Fingerprint(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>读取磁盘上当前的存档内容；不可读时返回 null。</summary>
    public static async Task<byte[]?> ReadDiskBytesAsync(SessionState session)
    {
        if (string.IsNullOrEmpty(session.ActiveSaveFileUri)) return null;
        try
        {
            return await new FileService().ReadBackAsync(session.ActiveSaveFileUri);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>磁盘文件是否已被外部（游戏/其它工具）改写过。</summary>
    public static async Task<bool> IsStaleAsync(SessionState session)
    {
        if (string.IsNullOrEmpty(session.ActiveSaveFingerprint)) return false;
        var disk = await ReadDiskBytesAsync(session);
        return disk is not null && Fingerprint(disk) != session.ActiveSaveFingerprint;
    }

    /// <summary>
    /// 应用一处改动并安全写回。
    /// <paramref name="applyDelta"/> 会先在内存存档上执行一次（保证界面立即可见），
    /// 若检测到磁盘已被更新，同一个操作会再应用到磁盘最新副本上，然后写回文件。
    /// </summary>
    public static async Task<SaveWriteResult> ApplyAndWriteAsync(
        SessionState session,
        Action<SaveFile> applyDelta)
    {
        var sav = session.ActiveSave;
        if (sav is null) return SaveWriteResult.NoUri;
        if (string.IsNullOrEmpty(session.ActiveSaveFileUri)) return SaveWriteResult.NoUri;

        applyDelta(sav);

        var disk = await ReadDiskBytesAsync(session);
        bool stale = disk is not null
                     && session.ActiveSaveFingerprint.Length > 0
                     && Fingerprint(disk) != session.ActiveSaveFingerprint;

        if (!stale)
        {
            var bytes = sav.Write().ToArray();
            if (disk is not null)
                await Task.Run(() => SaveBackupService.Backup(session.ActiveSaveFileUri, session.ActiveSaveFileName, disk));
            await new FileService().WriteBackAsync(bytes, session.ActiveSaveFileUri);
            session.ActiveSaveFingerprint = Fingerprint(bytes);
            return SaveWriteResult.Written;
        }

        // 磁盘已被更新：在最新副本上重放同一处改动，绝不覆盖游戏进度
        var copy = disk!.ToArray();
        if (!SaveUtil.TryGetSaveFile(copy, out var latest) || latest is null)
            return SaveWriteResult.StaleUnreadable;

        applyDelta(latest);
        var merged = latest.Write().ToArray();
        await Task.Run(() => SaveBackupService.Backup(session.ActiveSaveFileUri, session.ActiveSaveFileName, copy));
        await new FileService().WriteBackAsync(merged, session.ActiveSaveFileUri);

        session.ActiveSave           = latest;
        session.ActiveSaveFingerprint = Fingerprint(merged);
        return SaveWriteResult.MergedIntoLatest;
    }

    /// <summary>
    /// 强制把内存存档整份写回（用户明确选择覆盖时使用）。
    /// </summary>
    public static async Task ForceWriteAsync(SessionState session)
    {
        var sav = session.ActiveSave;
        if (sav is null || string.IsNullOrEmpty(session.ActiveSaveFileUri)) return;

        var bytes = sav.Write().ToArray();
        var disk = await ReadDiskBytesAsync(session);
        if (disk is not null)
            await Task.Run(() => SaveBackupService.Backup(session.ActiveSaveFileUri, session.ActiveSaveFileName, disk));
        await new FileService().WriteBackAsync(bytes, session.ActiveSaveFileUri);
        session.ActiveSaveFingerprint = Fingerprint(bytes);
    }

    /// <summary>
    /// 从磁盘重新加载存档（放弃软件内的未保存改动）。
    /// 成功返回 true，并把 <paramref name="session"/>.ActiveSave 换成最新解析结果。
    /// </summary>
    public static async Task<bool> ReloadFromDiskAsync(SessionState session)
    {
        var disk = await ReadDiskBytesAsync(session);
        if (disk is null) return false;

        var copy = disk.ToArray();
        if (!SaveUtil.TryGetSaveFile(copy, out var latest) || latest is null)
            return false;

        session.ActiveSave            = latest;
        session.ActiveSaveFingerprint = Fingerprint(disk);
        return true;
    }

    /// <summary>加载存档时调用，记录基线指纹。</summary>
    public static void CaptureBaseline(SessionState session, byte[] rawData)
    {
        session.ActiveSaveFingerprint = Fingerprint(rawData);
    }
}
