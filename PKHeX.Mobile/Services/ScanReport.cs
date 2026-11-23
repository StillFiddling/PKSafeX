using System.Text;

namespace PKHeX.Mobile.Services;

/// <summary>
/// 存档扫描的现场记录。用于在手机上直接看出「为什么这个文件夹读不到存档」：
/// 哪个目录扫了、见到哪些文件、每个文件被跳过的原因。
/// 首页「N 个存档」小字双击可查看。
/// </summary>
public static class ScanReport
{
    private static readonly List<string> _lines = new();
    private static readonly object _lock = new();
    private const int MaxLines = 600;

    public static int CandidateFiles { get; private set; }
    public static int RecognizedFiles { get; private set; }

    public static void Reset()
    {
        lock (_lock)
        {
            _lines.Clear();
            CandidateFiles = 0;
            RecognizedFiles = 0;
        }
    }

    public static void Add(string line)
    {
        lock (_lock)
        {
            if (_lines.Count >= MaxLines) return;
            _lines.Add(line);
        }
    }

    public static void CountCandidate() => CandidateFiles++;

    public static void CountRecognized() => RecognizedFiles++;

    public static string Text
    {
        get
        {
            lock (_lock)
            {
                var head = $"候选文件 {CandidateFiles} 个，成功识别 {RecognizedFiles} 个\n" +
                           new string('-', 40) + "\n";
                return _lines.Count == 0
                    ? head + "（没有记录：可能还没扫描，或监视目录为空）"
                    : head + string.Join("\n", _lines);
            }
        }
    }

    /// <summary>把记录写一份到应用私有目录，便于导出。</summary>
    public static string DumpToFile()
    {
        try
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "scan-report.txt");
            File.WriteAllText(path, Text, new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return string.Empty;
        }
    }
}
