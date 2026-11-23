using System;
using System.Collections.Generic;
using System.IO;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 临时诊断日志（布局行高排查用）。内存缓冲 + 导出文本，正式发布前可整段删除。
/// </summary>
public static class DiagLog
{
    private static readonly List<string> _lines = new();
    private const int MaxLines = 300;

    public static int Count => _lines.Count;

    public static void Add(string line)
    {
        var entry = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        _lines.Add(entry);
        if (_lines.Count > MaxLines) _lines.RemoveAt(0);
        Console.WriteLine("[LAYOUT] " + entry);
    }

    /// <summary>把缓冲写成文本文件，返回文件路径（供系统分享直接发送）。</summary>
    public static string Dump()
    {
        var path = Path.Combine(FileSystem.CacheDirectory, $"pksafe-layout-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var header = $"PKSafeX 布局诊断 {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{Environment.NewLine}";
        File.WriteAllText(path, header + string.Join(Environment.NewLine, _lines));
        return path;
    }
}
