using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 临时诊断日志（布局行高排查用）。内存缓冲 + 导出文本，正式发布前可整段删除。
/// </summary>
public static class DiagLog
{
    private static readonly List<string> _lines = new();
    private const int MaxLines = 300;
    private static bool _watchdogStarted;

    public static int Count => _lines.Count;

    public static void Add(string line)
    {
        var entry = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        _lines.Add(entry);
        if (_lines.Count > MaxLines) _lines.RemoveAt(0);
        Console.WriteLine("[LAYOUT] " + entry);
    }

    /// <summary>
    /// 主线程卡顿探针：后台线程每 250ms 往主线程投一个空回调，
    /// 量出这次回调实际等了多久。>220ms 说明主线程当时被同步工作占住。
    /// </summary>
    public static void StartWatchdog()
    {
        if (_watchdogStarted) return;
        _watchdogStarted = true;
        var sw = Stopwatch.StartNew();
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await Task.Delay(250);
                    long t0 = sw.ElapsedMilliseconds;
                    await MainThread.InvokeOnMainThreadAsync(() => { });
                    long lag = sw.ElapsedMilliseconds - t0;
                    if (lag > 220)
                        Add($"[LAG] 主线程阻塞 {lag}ms（进入后 +{t0 / 1000.0:F1}s）");
                }
                catch { await Task.Delay(500); }
            }
        });
    }

    /// <summary>把缓冲拼成整段文本（含设备信息抬头），供复制到剪贴板或写成文件。</summary>
    public static string Text()
    {
        string header;
        try
        {
            var d = DeviceDisplay.Current.MainDisplayInfo;
            header = $"PKSafeX 布局诊断 {DateTime.Now:yyyy-MM-dd HH:mm:ss}  屏幕={d.Width:0}x{d.Height:0}px @{d.Density:0.##}  平台={DeviceInfo.Platform}";
        }
        catch
        {
            header = $"PKSafeX 布局诊断 {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
        return header + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, _lines);
    }

    /// <summary>把缓冲写成文本文件，返回文件路径（文件管理里可直接取走）。</summary>
    public static string Dump()
    {
        var name = $"pksafe-layout-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        var text = Text();

        // 优先写到应用外部目录（/Android/data/<pkg>/files/），文件管理里能看到、能直接拷走；
        // 失败再退回 Cache 目录，两者都写，保证至少有一份。
        string? primary = null;
#if ANDROID
        try
        {
            var dir = Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                primary = Path.Combine(dir, name);
                File.WriteAllText(primary, text);
            }
        }
        catch { primary = null; }
#endif
        var cache = Path.Combine(FileSystem.CacheDirectory, name);
        try { File.WriteAllText(cache, text); } catch { }
        return primary ?? cache;
    }
}
