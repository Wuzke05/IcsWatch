namespace IcsWatch.Core;

/// <summary>
/// 极小的通用文本工具。
/// 原来 Repairer 和 GameMode 各自定义了一份 Trunc —— 合并到这里，避免两处实现漂移。
/// </summary>
public static class TextUtil
{
    /// <summary>截断到 n 个字符（超出加 …），用于日志里避免打印超长的命令输出。</summary>
    public static string Trunc(string? s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
