using System.Text;
using Microsoft.Maui.Storage;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 崩溃记录。任何未捕获异常都会写入应用私有目录下的 crash.log，
/// 下次启动时可在首页提示中看到，便于把真实原因反馈出来（无需连电脑抓 logcat）。
/// </summary>
public static class CrashLog
{
    private static bool _installed;
    private static readonly object _lock = new();

    public static string FilePath =>
        Path.Combine(FileSystem.AppDataDirectory, "crash.log");

    /// <summary>挂上全局异常钩子，只做一次。</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) Write("Unhandled", ex);
                else Write("Unhandled", e.ExceptionObject?.ToString() ?? "unknown");
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Write("UnobservedTask", e.Exception);
                e.SetObserved();
            };
        }
        catch
        {
            // 钩子挂不上也不能影响启动
        }
    }

    public static void Write(string tag, Exception ex) => Write(tag, ex.ToString());

    public static void Write(string tag, string? detail)
    {
        try
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ").AppendLine(tag);
                sb.AppendLine(detail ?? "(null)");
                sb.AppendLine(new string('-', 48));

                // 只保留最近若干条，避免无限增长
                var existing = File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;
                var merged = sb.ToString() + existing;
                if (merged.Length > 64 * 1024)
                    merged = merged[..(64 * 1024)];

                File.WriteAllText(FilePath, merged, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 写日志失败不要再抛
        }
    }

    public static bool HasLog => SafeExists();

    // ── 启动健康标记：上次启动是否走到了首页初始化完成 ──────────────────────

    private static string BootFlagPath =>
        Path.Combine(FileSystem.AppDataDirectory, "boot.flag");

    /// <summary>启动开始时打标记；若下次启动还看到它，说明上次启动中途死了。</summary>
    public static void MarkBootStarted()
    {
        try { File.WriteAllText(BootFlagPath, DateTime.Now.ToString("O"), new UTF8Encoding(false)); }
        catch { }
    }

    /// <summary>启动走到可用状态后清除标记。</summary>
    public static void MarkBootHealthy()
    {
        try { if (File.Exists(BootFlagPath)) File.Delete(BootFlagPath); }
        catch { }
    }

    public static bool LastBootUnhealthy()
    {
        try { return File.Exists(BootFlagPath); }
        catch { return false; }
    }

    public static string ReadLast(int maxChars = 900)
    {
        try
        {
            if (!File.Exists(FilePath)) return string.Empty;
            var text = File.ReadAllText(FilePath);
            return text.Length <= maxChars ? text : text[..maxChars] + "\n…";
        }
        catch
        {
            return string.Empty;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch { }
    }

    private static bool SafeExists()
    {
        try { return File.Exists(FilePath); }
        catch { return false; }
    }
}
