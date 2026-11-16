using System.Text;

namespace IcsWatch.Core;

/// <summary>
/// 按天滚动的日志 + 内存环形缓冲（供 UI 的日志视图实时显示）。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Ring = new();
    private const int RingMax = 500;

    private static string _dir = Path.Combine(AppContext.BaseDirectory, "logs");
    private static int _keepDays = 30;

    /// <summary>每写入一行就触发（UI 订阅后需自行切回 UI 线程）。</summary>
    public static event Action<string>? LineWritten;

    public static void Init(string dir, int keepDays)
    {
        _dir = dir;
        _keepDays = keepDays;
        try { Directory.CreateDirectory(_dir); } catch { }
        CleanupOldLogs();
    }

    public static string CurrentFile =>
        Path.Combine(_dir, $"icswatch-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string msg)  => Write("INFO ", msg);
    public static void Warn(string msg)  => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Debug(string msg) => Write("DEBUG", msg);

    /// <summary>
    /// "吞掉异常但留痕"：探测类代码（读网卡/注册表/WMI/文件）失败不影响主流程，
    /// 但完全不记就无从排查。只写 DEBUG 级别，正常时不干扰阅读。
    /// </summary>
    public static void Swallow(string where, Exception ex)
        => Write("DEBUG", $"[忽略] {where}: {ex.GetType().Name} {ex.Message}");

    /// <summary>把异常展开成可读文本（含内部异常）。</summary>
    public static string Ex(Exception ex)
    {
        var sb = new StringBuilder();
        var e = ex;
        var depth = 0;
        while (e != null && depth < 5)
        {
            sb.Append(depth == 0 ? "" : " <- ").Append(e.GetType().Name).Append(": ").Append(e.Message);
            e = e.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
        lock (Gate)
        {
            Ring.Enqueue(line);
            while (Ring.Count > RingMax) Ring.Dequeue();
            try { File.AppendAllText(CurrentFile, line + Environment.NewLine, new UTF8Encoding(true)); }
            catch { }
        }
        try { LineWritten?.Invoke(line); } catch { }
    }

    /// <summary>取最近 n 行（新的在最后）。</summary>
    public static string[] Tail(int n)
    {
        lock (Gate) return Ring.TakeLast(Math.Max(1, n)).ToArray();
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_keepDays);
            foreach (var f in Directory.GetFiles(_dir, "icswatch-*.log"))
            {
                try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}
