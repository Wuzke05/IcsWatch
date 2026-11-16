using System.Runtime.InteropServices;
using System.Text;
using IcsWatch.Config;

namespace IcsWatch.Core;

/// <summary>
/// 命令行模式（给计划任务和手工排查用）。
/// 因为本程序是 WinExe（无控制台），输出前必须 AttachConsole 到父进程控制台。
/// </summary>
public static class CliRunner
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int ATTACH_PARENT_PROCESS = -1;

    private static void TryAttachConsole()
    {
        try
        {
            if (AttachConsole(ATTACH_PARENT_PROCESS))
            {
                var so = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(so);
                var se = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetError(se);
            }
        }
        catch (Exception __ex) { AppLog.Swallow("CliRunner.cs:30", __ex); }
    }

    /// <summary>--game-on / --game-off：游戏模式（只保留手机 USB 共享出网）</summary>
    public static bool RunGame(AppConfig cfg, bool on)
    {
        TryAttachConsole();
        AppLog.Init(cfg.LogPath, cfg.LogKeepDays);
        Console.WriteLine();
        Console.WriteLine(on ? "===== 进入游戏模式 =====" : "===== 退出游戏模式 =====");
        var r = on ? GameMode.Enter(cfg, s => Console.WriteLine("  " + s))
                   : GameMode.Exit(cfg, s => Console.WriteLine("  " + s));
        Console.WriteLine();
        Console.WriteLine(r.msg);
        return r.ok;
    }

    public static void RunCheck(AppConfig cfg)
    {
        TryAttachConsole();
        AppLog.Init(cfg.LogPath, cfg.LogKeepDays);
        Console.WriteLine();
        Console.WriteLine("========== IcsWatch 体检 ==========");
        Console.WriteLine($"私有端(卡1)={cfg.PrivateAdapter}   公网端(卡2)={cfg.PublicAdapter}");
        Console.WriteLine();

        var snap = Checker.Run(cfg);
        var lastGroup = "";
        foreach (var it in snap.Items)
        {
            if (it.Group != lastGroup)
            {
                lastGroup = it.Group;
            }
            var tag = it.Status switch
            {
                CheckStatus.Pass => "[PASS]",
                CheckStatus.Warn => "[WARN]",
                CheckStatus.Fail => "[FAIL]",
                _ => "[INFO]",
            };
            Console.WriteLine($"{tag} {it.Name,-24} {it.Detail}");
            if (!string.IsNullOrEmpty(it.Hint) && it.Status != CheckStatus.Pass)
                Console.WriteLine($"       └─ {it.Hint}");
        }

        Console.WriteLine();
        Console.WriteLine($"========== 汇总: PASS={snap.Count(CheckStatus.Pass)}  " +
                          $"WARN={snap.Count(CheckStatus.Warn)}  FAIL={snap.Count(CheckStatus.Fail)} ==========");
        Console.WriteLine($"整体状态: {Checker.StateText(snap.State)}");
        Console.WriteLine();
    }

    public static void RunBootFix(AppConfig cfg)
    {
        TryAttachConsole();
        AppLog.Init(cfg.LogPath, cfg.LogKeepDays);
        AppLog.Info("===== [CLI] --boot-fix 全量重建 =====");
        Console.WriteLine();
        Console.WriteLine("========== IcsWatch 全量重建 ICS ==========");

        var rec = Repairer.RunFull(cfg, "CLI --boot-fix", s => Console.WriteLine("  " + s));
        foreach (var s in rec.Steps) { /* 已实时打印 */ }

        Console.WriteLine();
        Console.WriteLine(rec.Summary);
        if (!string.IsNullOrEmpty(rec.Detail)) Console.WriteLine("详情: " + rec.Detail);
        Console.WriteLine();
        Console.WriteLine("复检结果:");
        var snap = Checker.Run(cfg);
        Console.WriteLine($"  {snap.Summary}   整体: {Checker.StateText(snap.State)}");
        foreach (var it in snap.Items.Where(i => i.Status != CheckStatus.Pass))
            Console.WriteLine($"  [{it.Status.ToString().ToUpper()}] {it.Name}: {it.Detail}");
        Console.WriteLine();
    }

    public static void RunRepair(AppConfig cfg, bool full)
    {
        TryAttachConsole();
        AppLog.Init(cfg.LogPath, cfg.LogKeepDays);
        var rec = full
            ? Repairer.RunFull(cfg, "CLI --repair", s => Console.WriteLine("  " + s))
            : Repairer.RunLight(cfg, "CLI --repair-light", s => Console.WriteLine("  " + s));
        Console.WriteLine();
        Console.WriteLine(rec.Summary);
    }

    public static void RunLegacyRepair(AppConfig cfg)
    {
        TryAttachConsole();
        AppLog.Init(cfg.LogPath, cfg.LogKeepDays);
        var rec = Repairer.RunLegacy(cfg, "CLI --legacy-repair", s => Console.WriteLine("  " + s));
        Console.WriteLine();
        Console.WriteLine(rec.Summary);
    }

    /// <summary>
    /// 把私有网卡的网络类别设为 Private —— 供主进程以**子进程**方式调用。
    /// 这样即使 NLM 的 COM 互操作触发 AccessViolation（无法用 try/catch 拦），
    /// 崩溃也局限在子进程，主守护进程可以检测退出码并降级到 PowerShell 兜底。
    /// </summary>
    public static int RunSetCatPrivate(AppConfig cfg)
    {
        TryAttachConsole();
        AppLog.Init(cfg.LogPath, cfg.LogKeepDays);
        try
        {
            var ad = NetProbe.ReadAdapter(cfg.PrivateAdapter);
            if (ad.Category == "Private") { Console.WriteLine("OK 已是 Private"); return 0; }

            var ok = NetworkCategoryApi.SetPrivate(cfg.PrivateAdapter, out var err);
            Console.WriteLine(ok ? "OK 网络类别已设为 Private" : "FAIL " + err);
            var after = NetworkCategoryApi.GetCategory(cfg.PrivateAdapter);
            Console.WriteLine($"回读: {NetworkCategoryApi.CategoryText(after)}");
            return ok && after == NetworkCategoryApi.Private ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL " + AppLog.Ex(ex));
            return 3;
        }
    }
}
