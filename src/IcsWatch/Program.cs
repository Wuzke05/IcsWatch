using IcsWatch.Config;
using IcsWatch.Core;
using IcsWatch.Ui;

namespace IcsWatch;

internal static class Program
{
    /// <summary>全局配置（各处通过 Program.Config 访问）</summary>
    public static AppConfig Config { get; private set; } = new();

    private const string MutexName = @"Global\IcsWatch.SingleInstance.v1";
    private static Mutex? _mutex;
    private static TrayIcon? _tray;
    private static MainForm? _form;
    private static FloatingBall? _ball;

    [STAThread]
    private static void Main(string[] args)
    {
        // --watchdog：看门狗模式。已被计划任务每 2 分钟拉起一次，
        // 若主实例已在运行则**静默退出**（不能弹窗打扰用户）。
        bool watchdog = args.Any(a => a.Equals("--watchdog", StringComparison.OrdinalIgnoreCase));
        bool byWatchdog = args.Any(a => a.Equals("--by-watchdog", StringComparison.OrdinalIgnoreCase));

        Config = AppConfig.Load();

        // ── 先处理纯 CLI 命令 ────────────────────────────────────
        // 【关键】必须放在单实例互斥之前。否则 GUI 正在运行时，
        // 命令行调用（--check / --repair-light …）会被当成"重复启动"而静默退出，
        // 表现为"GUI 一开着，所有 CLI 命令就全部失效"。
        if (!watchdog && args.Length > 0 && IsGuiLessCommand(args[0]))
        {
            TryRunCli(args);
            return;
        }

        // ── 看门狗模式：只判断"主进程在不在"，不在就拉起一份新实例，然后立刻退出 ──
        // 【为什么绝不能在这里跑 GUI】看门狗由计划任务每 2 分钟拉起，而计划任务设了
        // 执行时限（ExecutionTimeLimit）。若在它进程里跑 GUI，会被到点硬杀掉、再被
        // 下一次触发拉起 → 表现为"每 6 分钟自己重启一次，而且每次重启都断一次网"。
        // 另外这样还有个副作用：任务实例一直处于"运行中"，配合 IgnoreNew 会
        // 让每隔一次的触发被跳过，看门狗实际变成 4~6 分钟才检查一次。
        if (watchdog)
        {
            // 用户主动退出过 → 尊重它，不拉起（重新登录/手动启动会清掉标记）
            if (!File.Exists(ExitFlagPath) && !IsAnotherRunning()) StartGuiDetached();
            return;
        }

        // ── 单实例互斥：防止重复启动把自己跑成两份 ────────────────
        _mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            NativeMethods.BroadcastShowMain();
            return;
        }

        AppLog.Init(Config.LogPath, Config.LogKeepDays);
        AppLog.Info($"===== IcsWatch 启动 v{typeof(Program).Assembly.GetName().Version} =====");
        AppLog.Info($"配置: 卡1={Config.PrivateAdapter} 卡2={Config.PublicAdapter} 修复模式={Config.RepairMode} 静默={!Config.TrayIcon}");

        // 手动/开机启动（不是看门狗拉的）= 用户希望它跑 → 清掉"主动退出"标记
        if (!byWatchdog && File.Exists(ExitFlagPath))
        {
            try { File.Delete(ExitFlagPath); AppLog.Info("已清除主动退出标记"); } catch { }
        }

        // ── 全局异常兜底：绝不让进程静默死掉 ─────────────────────
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppLog.Error("UI 线程未处理异常: " + e.Exception);
            SafeRestart();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) AppLog.Error("未处理异常: " + AppLog.Ex(ex));
            else AppLog.Error("未处理异常(非Exception)");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("后台任务未观察异常: " + AppLog.Ex(e.Exception));
            e.SetObserved();
        };

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ── 启动动画 ───────────────────────────────────────────
        // 只给"人手动启动"看。看门狗每 2 分钟拉一次，弹动画等于每 2 分钟闪一下屏幕。
        SplashForm? splash = null;
        if (Config.ShowSplash)
        {
            splash = new SplashForm();
            splash.ShowBoot();
            splash.Step("正在准备运行环境…", 0.35f);
        }

        // ── 提取内嵌的 PowerShell 脚本（对照 + 应急回退用）────────
        EmbeddedScripts.EnsureExtracted(Config.ScriptPath);
        splash?.Step("正在构建界面…", 0.62f);

        // ── 托盘 ───────────────────────────────────────────────
        _form = new MainForm();
        splash?.Step("正在连接网卡…", 0.82f);

        if (Config.TrayIcon)
        {
            _tray = new TrayIcon { LeftClickOpens = Config.LeftClickTrayOpens };
            _tray.OpenMainRequested += () => _form.Dispatcher.ToggleMainWindow();
            _tray.CheckNowRequested += () => _form.Dispatcher.RequestCheck();
            _tray.RepairRequested += () => _form.Dispatcher.RequestRepair();
            _tray.ExitRequested += () => _form.Dispatcher.RequestExit();
            // 托盘里勾「桌面悬浮球」= 和设置页那个勾选框同一件事，统一走 MainForm 落配置
            _tray.BallToggled += on => _form.SetBallVisible(on);
            _form.AttachTray(_tray);
            _tray.Show(true);
            _tray.SetBallChecked(Config.FloatingBall);
            _tray.SetState(HealthState.Unknown, "IcsWatch · 正在启动…");
        }

        // ── 桌面悬浮球 ─────────────────────────────────────────
        // 不放托盘里也能用：它是"随时能看到状态"的那一层，和托盘是两种入口。
        // 工厂挂给主窗体：托盘/设置页在运行时打开开关时，由这里按需创建。
        _form.AttachBallFactory(EnsureBall);
        if (Config.FloatingBall) EnsureBall().ShowBall();
        splash?.Step("正在启动守护…", 0.94f);

        // ── 主窗口：按配置决定是否最小化启动 ─────────────────────
        if (!Config.StartMinimized || !Config.TrayIcon) _form.Show();
        else { /* 保持隐藏，仅托盘常驻 */ }

        // 动画收尾放在 Run 之前：它自己会 pump 消息，不需要消息循环
        splash?.Finish("已就绪", 1f);
        splash = null;

        Application.Run(_form);

        // 用户主动退出 → 留标记，看门狗不再自动拉起
        if (_form.UserRequestedExit) MarkUserExit();
        AppLog.Info("===== IcsWatch 退出 =====");
        _tray?.Dispose();
        try { _ball?.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
    }

    /// <summary>悬浮球右键菜单里的"隐藏"：写进配置，重启后不再出现（能在设置里重新打开）</summary>
    /// <summary>按需创建（只建一次），供托盘 / 设置页在运行时打开悬浮球时调用</summary>
    private static FloatingBall EnsureBall()
    {
        if (_ball != null) return _ball;
        _ball = new FloatingBall(Config, _form!.Dispatcher, HideBallFromMenu);
        _form.AttachBall(_ball);
        return _ball;
    }

    private static void HideBallFromMenu()
    {
        try
        {
            _form?.SetBallVisible(false);
            AppLog.Info("悬浮球已隐藏（设置页或托盘菜单可重新打开）");
        }
        catch (Exception ex) { AppLog.Swallow("Program.HideBallFromMenu", ex); }
    }

    /// <summary>不需要界面、也不受单实例限制的命令行命令</summary>
    private static bool IsGuiLessCommand(string arg)
    {
        var c = arg.TrimStart('-').ToLowerInvariant();
        return c is "check" or "boot-fix" or "repair-light" or "legacy-repair"
                   or "set-cat-private" or "game-on" or "game-off"
                   or "help" or "h" or "?";
    }

    /// <summary>CLI 模式：--check / --boot-fix / --repair-light / --legacy-repair / --silent</summary>
    private static bool TryRunCli(string[] args)
    {
        var cmd = args[0].TrimStart('-').ToLowerInvariant();
        switch (cmd)
        {
            case "check":
                CliRunner.RunCheck(Config);
                return true;
            case "boot-fix":
                CliRunner.RunBootFix(Config);
                return true;
            case "repair-light":
                CliRunner.RunRepair(Config, full: false);
                return true;
            case "legacy-repair":
                CliRunner.RunLegacyRepair(Config);
                return true;
            case "set-cat-private":
                // 子进程隔离用：NLM 互操作若访问违纪崩溃，也只影响本进程
                Environment.ExitCode = CliRunner.RunSetCatPrivate(Config);
                return true;
            case "game-on":
                Environment.ExitCode = CliRunner.RunGame(Config, true) ? 0 : 1;
                return true;
            case "game-off":
                Environment.ExitCode = CliRunner.RunGame(Config, false) ? 0 : 1;
                return true;
            case "help":
            case "h":
            case "?":
                Console.WriteLine("""
                    IcsWatch —— ICS 双网卡守护

                    用法:
                      IcsWatch.exe                 启动托盘常驻（默认）
                      IcsWatch.exe --check         等价 ics-healthcheck.ps1，打印 PASS/WARN/FAIL
                      IcsWatch.exe --repair-light  只修轻量项（类别/度量/EnableDHCP/共享）
                      IcsWatch.exe --boot-fix      全量重建 ICS（开机任务用）
                      IcsWatch.exe --legacy-repair 强制走内嵌的 fix-ics-final.ps1（应急回退）
                      IcsWatch.exe --help          显示本帮助
                    """);
                return true;
            default:
                // 未知参数：按正常启动处理（例如被任务计划程序带上路径参数）
                return false;
        }
    }

    // ── 用户主动退出 / 看门狗 ─────────────────────────────────────

    /// <summary>用户主动退出的标记文件：存在 = 用户明确要它关着，看门狗不再自动拉起</summary>
    private static string ExitFlagPath => Path.Combine(Config.BaseDir, "user-exit.flag");

    /// <summary>是否已有实例在跑（按进程名判断，排除自己）</summary>
    private static bool IsAnotherRunning()
    {
        try
        {
            int me = Environment.ProcessId;
            return System.Diagnostics.Process.GetProcessesByName("IcsWatch")
                       .Any(p => p.Id != me);
        }
        catch { return true; }   // 探测失败就当有人在跑，宁可不动作
    }

    /// <summary>拉起一份正常的 GUI 实例（用独立进程，本进程立刻返回）</summary>
    private static void StartGuiDetached()
    {
        try
        {
            AppLog.Init(Config.LogPath, Config.LogKeepDays);
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "IcsWatch.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add("--by-watchdog");
            System.Diagnostics.Process.Start(psi);
            AppLog.Info("看门狗: 主进程不在，已拉起一份新实例");
        }
        catch (Exception ex)
        {
            try { AppLog.Error("看门狗拉起失败: " + AppLog.Ex(ex)); } catch { }
        }
    }

    /// <summary>记录"用户主动退出"，让看门狗别把它又拉起来</summary>
    private static void MarkUserExit()
    {
        try
        {
            File.WriteAllText(ExitFlagPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            AppLog.Info("用户主动退出 → 已写标记，看门狗不再自动拉起");
        }
        catch { }
    }

    private static void SafeRestart()
    {
        try
        {
            AppLog.Warn("尝试自动重启进程…");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { AppLog.Error("自动重启失败: " + AppLog.Ex(ex)); }
        try { Application.Exit(); } catch { }
    }
}
