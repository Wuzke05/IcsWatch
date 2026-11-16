using System.Drawing;
using System.Windows.Forms;
using IcsWatch.Config;
using IcsWatch.Core;
using IcsWatch.Monitor;

namespace IcsWatch.Ui;

/// <summary>托盘 → 界面的动作分发（窗口可能在隐藏状态）</summary>
public sealed class MainFormDispatcher
{
    private readonly MainForm _f;
    public MainFormDispatcher(MainForm f) => _f = f;
    public void ToggleMainWindow() => _f.ToggleFromTray();
    public void RequestCheck() => _f.CheckNow();
    public void RequestRepair() => _f.RepairNow(false);
    public void RequestExit() => _f.ExitApp();
}

public sealed partial class MainForm : Form
{
    private readonly AppConfig _cfg;
    private readonly Engine _engine;
    private TrayIcon? _tray;
    private FloatingBall? _ball;
    private Func<FloatingBall>? _ballFactory;
    private bool _exiting;

    // ── 总览 ────────────────────────────────────────────────────
    private StatusLight _light = null!;
    private Label _lblEngine = null!;
    private readonly Dictionary<string, Label> _kv = new();
    private Button _btnCheck = null!, _btnRepair = null!, _btnHide = null!, _btnFull = null!;
    private DataGridView _gridRepairs = null!;
    private int _lastRepairShown = -1;

    private static readonly Color PageBg = Theme.PageBg;
    private static readonly Color CardBg = Color.White;

    // ── 流量 ────────────────────────────────────────────────────
    private TrafficChart _chart = null!;
    private Label? _lblTrafficCap;
    private DataGridView _gridDev = null!;
    private readonly List<Button> _winBtns = new();

    // ── 检查项 ──────────────────────────────────────────────────
    private DataGridView _gridChecks = null!;
    private Label _lblCheckSum = null!;

    // ── 自动化 ──────────────────────────────────────────────────
    private DataGridView _gridTasks = null!, _gridScripts = null!;
    private TextBox _txtLog = null!;

    // ── 设置 ────────────────────────────────────────────────────
    private CheckBox _chkTray = null!, _chkStartMin = null!, _chkCloseTray = null!, _chkLeftClick = null!;
    private CheckBox _chkBall = null!, _chkSplash = null!;
    // ── 游戏模式 ────────────────────────────────────────────────
    private Label _lblGameState = null!, _lblGameDetail = null!, _lblGameMsg = null!;
    private ComboBox _cmbUsb = null!;
    private CheckBox? _chkExitOnBoot;
    private CheckBox _chkAuto = null!;                       // 自动切换（检测到 USB 就切）
    private NumericUpDown _numAutoEnter = null!, _numAutoExit = null!;   // 进入/退出防抖（秒）
    private CheckBox _chkLite = null!;                       // 游戏模式省电（轻量化）
    private NumericUpDown _numGameProbe = null!;             // 游戏模式体检周期（秒）
    private FlowLayoutPanel _flowPause = null!;
    private readonly Dictionary<string, CheckBox> _chkPause = new();
    private Button _btnGameOn = null!, _btnGameOff = null!;
    private CheckBox _chkBootRebuild = null!;
    private ComboBox _cmbPriv = null!, _cmbPub = null!;
    private NumericUpDown _numBootDelay = null!;
    private ComboBox _cboRepair = null!;
    private NumericUpDown _numProbe = null!, _numTraffic = null!, _numCooldown = null!, _numMaxRepair = null!;
    private Label _lblCfgPath = null!;

    private System.Windows.Forms.Timer _uiTimer = null!;
    private CheckSnapshot? _lastSnap;
    private DateTime _engineStart = DateTime.Now;

    public MainFormDispatcher Dispatcher { get; }

    public MainForm()
    {
        _cfg = Program.Config;
        _engine = new Engine(_cfg);
        Dispatcher = new MainFormDispatcher(this);

        Text = "IcsWatch";
        MinimumSize = new Size(960, 660);
        Size = new Size(1060, 790);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Theme.PageBg;
        Icon = AppIcon.Window;

        BuildUi();
        WireEngine();

        // 强制创建窗口句柄：这样即使"启动后只留托盘、窗口从不显示"，
        // 后台引擎事件也能通过 BeginInvoke 正常送到界面。
        _ = Handle;
    }


    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _engine.Start();
        RefreshAll();
        AppendLog("IcsWatch 已启动，后台守护中。");
    }

    // ════════════════════════════════════════════════════════════
    //  界面构建
    // ════════════════════════════════════════════════════════════
    private TabControl _tabs = null!;


    private void BuildUi()
    {
        _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(16, 6) };
        _tabs.TabPages.Add(BuildOverviewTab());
        _tabs.TabPages.Add(BuildGameTab());
        _tabs.TabPages.Add(BuildTrafficTab());
        _tabs.TabPages.Add(BuildStatsTab());
        _tabs.TabPages.Add(BuildChecksTab());
        _tabs.TabPages.Add(BuildAutomationTab());
        _tabs.TabPages.Add(BuildSettingsTab());
        Controls.Add(_tabs);

        // 切到"自动化"时重读一遍计划任务/脚本状态。
        // 【为什么】这两张表原来只在启动和手动刷新时更新，于是会出现
        // "检查项页显示某任务 LastResult=0，自动化页却还挂着启动时的 -1" —— 同一事实两种说法。
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            try
            {
                if (_tabs.SelectedTab == _tabs.TabPages[5]) { RefreshTasks(); RefreshScripts(); }
            }
            catch (Exception ex) { AppLog.Swallow("MainForm.BuildUi(tabSwitch)", ex); }
        };

        _uiTimer = new System.Windows.Forms.Timer { Interval = 700 };
        _uiTimer.Tick += (_, _) => TickUi();
        _uiTimer.Start();
    }

    // 控件工厂全部委托给 Theme —— 全程序的颜色/字体/表格样式只在一个地方定义
    private static Label MkLabel(string text, float size = 9F, bool bold = false, Color? color = null)
        => Theme.MkLabel(text, size, bold, color);

    private static readonly (string key, string caption)[] KvRows =
    {
        ("k1",  "卡1"),
        ("k2",  "卡2"),
        ("ics", "ICS 共享"),
        ("svc", "ICS 服务"),
        ("nat", "NAT 出口"),
        ("dev", "下游设备"),
    };


    private static Button MkButton(string text, int width, Color back, int height = Theme.BtnH)
        => Theme.MkButton(text, width, back, height);


    private static DataGridView MkGrid() => Theme.MkGrid();

    // ════════════════════════════════════════════════════════════
    //  引擎事件
    // ════════════════════════════════════════════════════════════

    private void WireEngine()
    {
        _engine.SnapshotReady += s => Ui(() => ApplySnapshot(s));
        _engine.TrafficReady += s => Ui(() => _chart?.Push(s));
        _engine.StateChanged += (st, msg) => Ui(() => ApplyState(st, msg));
        _engine.RepairStarted += () => Ui(() => { _btnRepair.Enabled = false; _btnCheck.Enabled = false; });
        _engine.RepairProgress += m => Ui(() => AppendLog("  " + m));
        _engine.RepairFinished += rec => Ui(() =>
        {
            _btnRepair.Enabled = true; _btnCheck.Enabled = true;
            RefreshRepairs();
            AppendLog("维修完成: " + rec.Summary);
        });
        AppLog.LineWritten += line => Ui(() => AppendLog(line));
    }


    private void Ui(Action a)
    {
        try
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }


    private void AppendLog(string line)
    {
        try
        {
            if (_txtLog.TextLength > 200_000) _txtLog.Text = _txtLog.Text[^100_000..];
            _txtLog.AppendText(line + Environment.NewLine);
        }
        catch (Exception __ex) { AppLog.Swallow("MainForm.cs:237", __ex); }
    }


    private static void OpenPath(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return;
            if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        }
        catch (Exception __ex) { AppLog.Swallow("MainForm.cs:249", __ex); }
    }

    // ════════════════════════════════════════════════════════════
    //  托盘 / 生命周期
    // ════════════════════════════════════════════════════════════
    public void AttachTray(TrayIcon tray) => _tray = tray;


    /// <summary>接上桌面悬浮球（状态由 TickUi 每 700ms 推一次）</summary>
    public void AttachBall(FloatingBall ball) => _ball = ball;

    /// <summary>悬浮球工厂：运行时从托盘/设置页打开开关时，用它按需创建</summary>
    public void AttachBallFactory(Func<FloatingBall> factory) => _ballFactory = factory;


    /// <summary>设置页开关悬浮球时用</summary>
    public void ApplyBallVisible(bool on)
    {
        if (_ball == null) return;
        if (on) _ball.ShowBall(); else _ball.HideBall();
    }


    /// <summary>
    /// 统一入口：设置页勾选框、托盘菜单勾选项、悬浮球右键「隐藏」都走这里。
    /// 三处 UI 必须永远表示同一个状态，所以配置、显隐、另外两处的勾选一次改完。
    /// </summary>
    public void SetBallVisible(bool on)
    {
        if (_cfg.FloatingBall != on)
        {
            _cfg.FloatingBall = on;
            try { _cfg.Save(); } catch (Exception ex) { AppLog.Swallow("MainForm.SetBallVisible(save)", ex); }
            AppLog.Info(on ? "桌面悬浮球已开启" : "桌面悬浮球已关闭");
        }
        if (_ball != null)
        {
            if (on) { if (!_ball.Visible) _ball.ShowBall(); }
            else _ball.HideBall();
        }
        else if (on && _ballFactory != null)
        {
            _ball = _ballFactory();
            _ball.ShowBall();
        }
        if (_chkBall != null && _chkBall.Checked != on) _chkBall.Checked = on;
        _tray?.SetBallChecked(on);
    }


    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && _cfg.CloseToTray && _tray != null)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        // 真正关掉了 = 用户要它退出 → 同样不让看门狗拉起。
        // 注意 CloseReason：✕ / Alt+F4 = UserClosing，任务管理器结束/CloseMainWindow = TaskManagerClosing，
        // 都要算；关机(WindowsShutDown) 和 Application.Exit(ApplicationExitCall) 不算。
        if (!e.Cancel &&
            e.CloseReason != CloseReason.WindowsShutDown &&
            e.CloseReason != CloseReason.ApplicationExitCall)
            UserRequestedExit = true;
        base.OnFormClosing(e);
    }


    protected override void WndProc(ref Message m)
    {
        // 第二次启动时的"唤出已有实例"广播
        if (m.Msg == NativeMethods.WM_ICSWATCH_SHOWMAIN)
        {
            Ui(ShowFromTray);
            return;
        }
        base.WndProc(ref m);
    }
}
