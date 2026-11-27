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

    /// <summary>
    /// 内容指纹：把存档对象重新序列化（含重算校验）后取哈希。
    /// test11 问题7：比「文件原始字节」更稳——不受容器包装（.dsv 等）或 provider
    /// 写入方式影响，语义就是「存档内容有没有被改过」。
    /// </summary>
    public static string ContentFingerprint(SaveFile sav)
        => Fingerprint(sav.Write().ToArray());

    /// <summary>
    /// 磁盘内容是否仍等于基线。优先按内容比对（解析磁盘字节后重新序列化），
    /// 磁盘解析失败时才退回原始字节比对（此时后续 merge 也会判 StaleUnreadable）。
    /// </summary>
    public static bool MatchesBaseline(byte[] disk, string baseline)
    {
        var copy = disk.ToArray();   // TryGetSaveFile 可能原地解密，必须拷贝
        if (SaveUtil.TryGetSaveFile(copy, out var diskSav) && diskSav is not null)
            return ContentFingerprint(diskSav) == baseline;

        return Fingerprint(disk) == baseline;
    }

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
        return disk is not null && !MatchesBaseline(disk, session.ActiveSaveFingerprint);
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

        // 事务快照：applyDelta 会先把内存存档改掉（界面立即生效），
        // 之后任何一步失败都必须把内存还原，否则界面与磁盘长期不一致，
        // 而且下一次成功写回会把这份「脏」结果整份落盘，造成真实丢档。
        var baselineFingerprint = session.ActiveSaveFingerprint;
        var snapshot = sav.Write().ToArray();

        try
        {
            applyDelta(sav);

            var disk = await ReadDiskBytesAsync(session);
            bool stale = disk is not null
                         && session.ActiveSaveFingerprint.Length > 0
                         && !MatchesBaseline(disk, session.ActiveSaveFingerprint);

            // P4 诊断：把判定依据的前 8 位记下来，再遇到疑难时可以直接对照
            try
            {
                DiagLog.Add($"save-write uri={session.ActiveSaveFileName} size={(disk?.Length ?? -1)} " +
                            $"disk={(disk is null ? "null" : Fingerprint(disk)[..8])} " +
                            $"base={(session.ActiveSaveFingerprint.Length > 0 ? session.ActiveSaveFingerprint[..8] : "empty")} " +
                            $"stale={stale} at={DateTime.Now:HH:mm:ss}");
            }
            catch { /* 诊断失败不影响写入 */ }

            if (!stale)
            {
                var bytes = sav.Write().ToArray();
                if (disk is not null)
                    await Task.Run(() => SaveBackupService.Backup(session.ActiveSaveFileUri, session.ActiveSaveFileName, disk));
                await new FileService().WriteBackAsync(bytes, session.ActiveSaveFileUri);
                session.ActiveSaveFingerprint = ContentFingerprint(sav);
                return SaveWriteResult.Written;
            }

            // 磁盘已被更新：在最新副本上重放同一处改动，绝不覆盖游戏进度
            var copy = disk!.ToArray();
            if (!SaveUtil.TryGetSaveFile(copy, out var latest) || latest is null)
            {
                // 这条是「正常返回」而不是异常，但内存已经被 applyDelta 改脏了，
                // 必须同样还原，否则回到页面看到的是没落盘的改动。
                RestoreFromSnapshot(session, snapshot, baselineFingerprint);
                return SaveWriteResult.StaleUnreadable;
            }

            applyDelta(latest);
            var merged = latest.Write().ToArray();
            await Task.Run(() => SaveBackupService.Backup(session.ActiveSaveFileUri, session.ActiveSaveFileName, copy));
            await new FileService().WriteBackAsync(merged, session.ActiveSaveFileUri);

            session.ActiveSave           = latest;
            session.ActiveSaveFingerprint = ContentFingerprint(latest);
            return SaveWriteResult.MergedIntoLatest;
        }
        catch
        {
            RestoreFromSnapshot(session, snapshot, baselineFingerprint);
            throw;
        }
    }

    /// <summary>
    /// 把内存存档还原到事务开始前的状态（失败时用）。
    /// 快照本身就是 <see cref="SaveFile.Write"/> 的输出，必然可重新解析；
    /// 万一解析失败，至少把指纹复位回基线，避免下一轮比对把脏内存当成有效基线。
    /// </summary>
    private static void RestoreFromSnapshot(SessionState session, byte[] snapshot, string baselineFingerprint)
    {
        try
        {
            var copy = snapshot.ToArray();
            if (SaveUtil.TryGetSaveFile(copy, out var restored) && restored is not null)
                session.ActiveSave = restored;
        }
        catch { /* 解析失败就保持原对象，仅复位指纹 */ }

        session.ActiveSaveFingerprint = baselineFingerprint;
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
        session.ActiveSaveFingerprint = ContentFingerprint(sav);
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
        session.ActiveSaveFingerprint = ContentFingerprint(latest);
        return true;
    }

    /// <summary>
    /// 加载存档时调用，记录基线指纹。
    /// test11 问题7：基线改用「内容指纹」，且必须传入<b>真正用于这次载入</b>的那个 sav 对象
    /// （即内存里正在编辑的那一份），否则又会退化成「拿旧字节当基线」的老问题。
    /// </summary>
    public static void CaptureBaseline(SessionState session, SaveFile sav)
    {
        session.ActiveSaveFingerprint = ContentFingerprint(sav);
    }
}

