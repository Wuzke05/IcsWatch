using System.Diagnostics;
using System.Text;

namespace IcsWatch.Core;

/// <summary>
/// 隐藏窗口地运行外部程序并捕获输出（用于 netsh、以及 --legacy-repair 调 PowerShell）。
/// </summary>
public static class ProcessRunner
{
    public sealed class Result
    {
        public int ExitCode { get; init; } = -1;
        public string StdOut { get; init; } = "";
        public string StdErr { get; init; } = "";
        public bool Ok => ExitCode == 0;
        public string All => (StdOut + Environment.NewLine + StdErr).Trim();
    }

    public static Result Run(string fileName, string arguments, int timeoutMs = 120_000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return new Result { ExitCode = -1, StdErr = "进程启动失败" };

            var so = new StringBuilder();
            var se = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return new Result { ExitCode = -2, StdOut = so.ToString(), StdErr = "执行超时被终止" };
            }
            // 让异步输出收尾
            try { p.WaitForExit(2000); } catch { }

            return new Result
            {
                ExitCode = p.ExitCode,
                StdOut = so.ToString().TrimEnd(),
                StdErr = se.ToString().TrimEnd(),
            };
        }
        catch (Exception ex)
        {
            return new Result { ExitCode = -1, StdErr = AppLog.Ex(ex) };
        }
    }

    /// <summary>隐藏窗口地跑一段 PowerShell（仅在应急回退路径使用）</summary>
    public static Result RunPowerShellHidden(string scriptPathOrCommand, bool isFile, int timeoutMs = 300_000)
    {
        var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(ps)) ps = "powershell.exe";
        var args = isFile
            ? $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPathOrCommand}\""
            : $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{scriptPathOrCommand}\"";
        return Run(ps, args, timeoutMs);
    }
}
