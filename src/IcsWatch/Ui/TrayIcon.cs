using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using IcsWatch.Core;

namespace IcsWatch.Ui;

/// <summary>
/// 系统托盘图标（QQ / 微信 那种常驻通知区域的小图标）。
///  - 左键单击 → 唤出/收起主窗口
///  - 右键      → 打开主界面 / 立即体检 / 一键维修 / 打开日志文件夹 / 退出
///  - 悬停      → Tooltip 显示状态摘要
///  - 图标颜色随健康状态实时变化（GDI+ 动态生成，不依赖外部 .ico 文件）
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _ni;
    private readonly ContextMenuStrip _menu;
    private Icon? _icon;
    private HealthState _state = HealthState.Unknown;

    public event Action? OpenMainRequested;
    public event Action? CheckNowRequested;
    public event Action? RepairRequested;
    public event Action? ExitRequested;

    /// <summary>托盘菜单里勾选/取消「桌面悬浮球」时触发（参数=是否显示）</summary>
    public event Action<bool>? BallToggled;

    private ToolStripMenuItem? _miBall;

    /// <summary>从设置页改动时，把托盘菜单的勾选状态跟上（不触发 BallToggled）</summary>
    public void SetBallChecked(bool on)
    {
        if (_miBall == null || _miBall.Checked == on) return;
        _ballSyncing = true;
        _miBall.Checked = on;
        _ballSyncing = false;
    }

    private bool _ballSyncing;

    public bool LeftClickOpens { get; set; } = true;

    public TrayIcon()
    {
        _menu = new ContextMenuStrip();

        var miOpen = new ToolStripMenuItem("打开主界面(&O)") { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        miOpen.Click += (_, __) => OpenMainRequested?.Invoke();
        _menu.Items.Add(miOpen);

        _menu.Items.Add(new ToolStripSeparator());

        var miCheck = new ToolStripMenuItem("立即体检(&C)");
        miCheck.Click += (_, __) => CheckNowRequested?.Invoke();
        _menu.Items.Add(miCheck);

        var miRepair = new ToolStripMenuItem("一键维修(&R)");
        miRepair.Click += (_, __) => RepairRequested?.Invoke();
        _menu.Items.Add(miRepair);

        _menu.Items.Add(new ToolStripSeparator());

        var miLog = new ToolStripMenuItem("打开日志文件夹(&L)");
        miLog.Click += (_, __) => OpenFolder(AppLog.CurrentFile);
        _menu.Items.Add(miLog);

        var miCfg = new ToolStripMenuItem("打开配置文件(&F)");
        miCfg.Click += (_, __) => OpenFolder(Program.Config.ConfigPath);
        _menu.Items.Add(miCfg);

        _menu.Items.Add(new ToolStripSeparator());

        // 桌面悬浮球：勾选=显示。CheckOnClick 自动切换勾选态，点击后把新状态通知出去，
        // 由 MainForm 统一落配置 + 显示/隐藏，保证与「设置」页那个勾选框始终一致。
        _miBall = new ToolStripMenuItem("桌面悬浮球(&B)") { CheckOnClick = true };
        _miBall.CheckedChanged += (_, __) =>
        {
            if (_ballSyncing) return;
            BallToggled?.Invoke(_miBall.Checked);
        };
        _menu.Items.Add(_miBall);

        _menu.Items.Add(new ToolStripSeparator());

        var miExit = new ToolStripMenuItem("退出(&X)");
        miExit.Click += (_, __) => ExitRequested?.Invoke();
        _menu.Items.Add(miExit);

        _ni = new NotifyIcon
        {
            Visible = false,
            ContextMenuStrip = _menu,
            Text = "IcsWatch",
        };

        _ni.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && LeftClickOpens) OpenMainRequested?.Invoke();
        };
        // 双击总是在唤出（兼容"单击只弹菜单"的习惯）
        _ni.DoubleClick += (_, __) => OpenMainRequested?.Invoke();
    }

    public void Show(bool visible)
    {
        _ni.Visible = visible;
        if (visible) { ApplyState(); _applied = true; }
    }

    private string _lastTooltip = "正在启动…";
    private bool _applied;

    /// <summary>更新状态色与 Tooltip（Tooltip 上限 63 字符，请注意截断）</summary>
    public void SetState(HealthState state, string tooltip)
    {
        // TickUi 每 700ms 调一次。状态和文案都没变时直接返回，
        // 否则会反复 SetIcon + 重设 Tooltip，让外壳不停重绘（托盘闪烁/吃 CPU）。
        if (_applied && state == _state && tooltip == _lastTooltip) return;
        _state = state;
        _lastTooltip = tooltip;
        if (!_ni.Visible) return;
        ApplyState();
        _applied = true;
    }

    /// <summary>把当前状态真正画到托盘上（图标 + Tooltip）</summary>
    private void ApplyState()
    {
        // 托盘图标 = 用户线稿 + 右下角状态色圆点
        // （颜色语义保留：绿=正常 黄=有警告 红=故障 蓝=维修中）
        SetIcon(AppIcon.Tray(_state, _state == HealthState.Repairing));
        _ni.Text = ClampTooltip(_lastTooltip);
    }

    /// <summary>
    /// 托盘提示文本兜底裁剪。
    /// ⚠️ NotifyIcon.Text 有长度上限 —— .NET Framework 是 63 字符，.NET Core 3.0+ 提到 127。
    /// 超长会抛 ArgumentException（会被 UI 异常兜底误判成崩溃而重启程序），所以必须主动裁。
    /// 策略：**按整行**丢弃末尾行（而不是把一行砍成半句），并补省略号。
    /// </summary>
    private const int MaxTooltipChars = 127;

    private static string ClampTooltip(string? tooltip)
    {
        var t = (tooltip ?? "IcsWatch").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (t.Length == 0) t = "IcsWatch";
        if (t.Length <= MaxTooltipChars) return t.Replace("\n", "\r\n");

        var lines = t.Split('\n');
        var kept = new List<string>();
        var len = 0;
        foreach (var ln in lines)
        {
            var add = ln.Length + (kept.Count > 0 ? 2 : 0);   // \r\n 占 2 字符
            if (len + add + 1 > MaxTooltipChars) break;       // 留 1 位给省略号
            kept.Add(ln);
            len += add;
        }
        if (kept.Count == 0) return t[..(MaxTooltipChars - 1)] + "…";
        return string.Join("\r\n", kept) + "…";
    }

    /// <summary>气泡提示（告警用）</summary>
    public void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Warning)
    {
        try { _ni.ShowBalloonTip(6000, title, text, icon); } catch { }
    }

    private void SetIcon(Icon ic)
    {
        var old = _icon;
        _icon = ic;
        _ni.Icon = ic;
        old?.Dispose();
    }

    private static void OpenFolder(string? path)
    {
        try
        {
            var p = path;
            if (!string.IsNullOrEmpty(p))
            {
                if (File.Exists(p)) { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{p}\""); return; }
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) { System.Diagnostics.Process.Start("explorer.exe", dir); return; }
            }
            System.Diagnostics.Process.Start("explorer.exe", AppContext.BaseDirectory);
        }
        catch { }
    }

    public void Dispose()
    {
        try { _ni.Visible = false; _ni.Dispose(); } catch { }
        try { _menu.Dispose(); } catch { }
        try { _icon?.Dispose(); } catch { }
    }
}
