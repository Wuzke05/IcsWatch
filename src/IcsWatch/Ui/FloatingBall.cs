using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using IcsWatch.Config;
using IcsWatch.Core;

namespace IcsWatch.Ui;

/// <summary>
/// 桌面悬浮球：一个常驻的小圆球，颜色直接表示健康状态（绿=正常 黄=降级 红=故障 蓝=维修中）。
///
/// 【为什么不就是一个托盘图标】托盘在任务栏右下角的折叠区里，平时看不见；
/// 悬浮球始终在桌面上，一眼就能读到"现在到底正不正常"，并且点一下就能开主界面。
///
/// 【交互】
///   单击      → 唤出/收起主窗口
///   按住拖动  → 移到任意位置（位置记进配置，重启后回到原处）
///   右键      → 打开主界面 / 立即体检 / 一键维修 / 隐藏 / 退出
///   悬停      → 轻微放大 + 悬停提示（状态摘要）
///
/// 【不抢焦点】窗口带 WS_EX_NOACTIVATE：拖它、点它都不会把你正在用的程序切走。
/// </summary>
public sealed class FloatingBall : Form
{
    private readonly AppConfig _cfg;
    private readonly MainFormDispatcher _disp;
    private readonly ToolTip _tip = new() { InitialDelay = 350, ReshowDelay = 120, AutoPopDelay = 8000 };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
    private readonly ContextMenuStrip _menu;

    /// <summary>球体直径（96dpi 逻辑值）；外面还有 SHADOW 的余量给阴影</summary>
    private const int D = 58;
    private const int SHADOW = 10;
    private const int BoxSize = D + SHADOW * 2;

    private float _scale = 1f;
    private HealthState _state = HealthState.Unknown;
    private bool _repairing;
    private string _text = "IcsWatch";
    private double _rx, _tx;

    private float _angle, _hover, _hoverTarget;
    private bool _dirty = true;
    private bool _pressed, _dragging;
    private Point _downScreen, _downLoc;
    private bool _hidden;

    // ── 贴边停靠（像 QQ 那样：拖到屏幕边缘就"吸"上去，平时收起只露一条边，鼠标移过去滑出来）──
    private enum DockEdge { None, Left, Right, Top, Bottom }

    private DockEdge _dock = DockEdge.None;
    private Point _dockShown, _dockHidden;      // 贴边后的"完全展开位"与"收起位"
    private float _slide = 1f;                  // 0=收起，1=展开
    private float _slideTarget = 1f;
    private long _hideAt;                       // 到点自动收起；0=不收起

    private const int SnapPx = 12;              // 松手时离边缘多近算"贴边"
    private const int PeekPx = 10;              // 收起后还露出来的宽度
    private const int HideDelayMs = 600;        // 鼠标移开多久后收起

    public FloatingBall(AppConfig cfg, MainFormDispatcher disp, Action? hideRequested = null)
    {
        _cfg = cfg;
        _disp = disp;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(20, 22, 28);
        Size = new Size(BoxSize, BoxSize);

        _menu = new ContextMenuStrip();
        var miOpen = new ToolStripMenuItem("打开主界面(&O)") { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        miOpen.Click += (_, _) => _disp.ToggleMainWindow();
        var miCheck = new ToolStripMenuItem("立即体检(&C)");
        miCheck.Click += (_, _) => _disp.RequestCheck();
        var miRepair = new ToolStripMenuItem("一键维修(&R)");
        miRepair.Click += (_, _) => _disp.RequestRepair();
        var miHide = new ToolStripMenuItem("隐藏悬浮球(&H)");
        miHide.Click += (_, _) => hideRequested?.Invoke();
        var miExit = new ToolStripMenuItem("退出(&X)");
        miExit.Click += (_, _) => _disp.RequestExit();

        _menu.Items.AddRange(new ToolStripItem[]
        {
            miOpen, new ToolStripSeparator(), miCheck, miRepair,
            new ToolStripSeparator(), miHide, miExit,
        });

        _timer.Tick += (_, _) => Animate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW（不进 Alt-Tab）
            cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE（点击不抢焦点）
            cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
            return cp;
        }
    }

    // ── 对外 ─────────────────────────────────────────────────────

    public void ShowBall()
    {
        try
        {
            if (_hidden) { _hidden = false; }
            Location = RestorePosition();
            Show();
            InitDock();
            Render();
            _timer.Start();
        }
        catch (Exception ex) { AppLog.Swallow("FloatingBall.ShowBall", ex); }
    }

    public void HideBall()
    {
        _hidden = true;
        try { Hide(); } catch { }
    }

    public bool IsHidden => _hidden;

    /// <summary>状态更新（由 MainForm 的 UI 定时器每 700ms 调一次；内容没变就不重画）</summary>
    public void SetState(HealthState state, bool repairing, string tooltip, double rx = 0, double tx = 0)
    {
        var changed = state != _state || repairing != _repairing
                      || Math.Abs(rx - _rx) > 0.05 || Math.Abs(tx - _tx) > 0.05;
        _state = state;
        _repairing = repairing;
        _rx = rx;
        _tx = tx;
        if (!string.Equals(tooltip, _text, StringComparison.Ordinal))
        {
            _text = tooltip ?? "";
            try { _tip.SetToolTip(this, _text); } catch { }
            changed = true;
        }
        if (changed)
        {
            _dirty = true;
            _timer.Start();     // 有变化就把动画定时器叫醒（空闲时会自己停）
        }
    }

    // ── 动画 ─────────────────────────────────────────────────────

    private void Animate()
    {
        if (IsDisposed || !Visible) { _timer.Stop(); return; }

        var moving = Math.Abs(_hover - _hoverTarget) > 0.01f;
        if (moving) _hover += (_hoverTarget - _hover) * 0.25f;

        if (_repairing)
        {
            _angle = (_angle + 8f) % 360f;
            _dirty = true;
        }

        // 到点自动收起（鼠标移开 HideDelayMs 之后）
        if (_hideAt > 0 && !_pressed && Environment.TickCount64 >= _hideAt)
        {
            _hideAt = 0;
            _slideTarget = 0f;
        }

        // 贴边滑出/收回：位置变了就得重新 Apply（分层窗口每次都要带新位置）
        var sliding = Math.Abs(_slide - _slideTarget) > 0.004f;
        if (sliding)
        {
            _slide += (_slideTarget - _slide) * 0.45f;   // 约 5 帧（200ms）到位，慢了显得拖沓
            if (Math.Abs(_slide - _slideTarget) < 0.01f) _slide = _slideTarget;
            Location = new Point(
                (int)Math.Round(_dockHidden.X + (_dockShown.X - _dockHidden.X) * _slide),
                (int)Math.Round(_dockHidden.Y + (_dockShown.Y - _dockHidden.Y) * _slide));
            Render();
            _dirty = false;
        }

        if (_dirty || moving) { Render(); _dirty = false; }

        // 不维修、不悬停动画、不滑动、没有待收起、也没有新状态 → 停表（静止时零开销）
        if (!_repairing && !moving && !sliding && _hideAt == 0
            && Math.Abs(_hover - _hoverTarget) < 0.01f && !_dirty)
            _timer.Stop();
    }

    private void Render()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            _scale = DpiScale();
            using var bmp = PaintBall();
            Layered.Apply(Handle, bmp, Location);
        }
        catch (Exception ex) { AppLog.Swallow("FloatingBall.Render", ex); }
    }

    private float DpiScale()
    {
        try
        {
            using var g = Graphics.FromHwnd(Handle);
            return Math.Max(1f, g.DpiX / 96f);
        }
        catch { return 1f; }
    }

    /// <summary>
    /// 球体 = 黑色圆心 + 一圈状态色圆环（形状取自品牌图标：圆环很厚，约占半径的四成）。
    ///
    /// 【为什么是"环"而不是"实心球"】状态只靠圆环的颜色表达：
    ///   正常 青绿 / 降级 黄 / 故障 红 / 维修中 状态色 + 绕圈光弧 / 冷却 紫 / 未知 灰。
    /// 黑芯保证任何壁纸下都有对比度，也和应用图标里的"黑瞳 + 绿环"是同一套语言。
    /// </summary>
    private Bitmap PaintBall()
    {
        var s = _scale;
        var side = (int)Math.Ceiling(BoxSize * s);
        var bmp = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(s, s);

        var cx = BoxSize / 2f;
        var cy = BoxSize / 2f;
        var r = D / 2f * (1f + 0.07f * _hover);

        // 圆环外径 r，黑芯半径 0.575r（比例照抄品牌图标：黑芯 168 / 外径 293）
        var core = r * 0.575f;
        var ring = RingColor(_state);

        // 阴影：几层同心圆叠出柔和外扩，比单圈黑边自然
        for (var i = 4; i >= 1; i--)
        {
            var rr = r + i * 1.5f;
            using var sh = new SolidBrush(Color.FromArgb(9 + i * 2, 0, 0, 0));
            g.FillEllipse(sh, cx - rr, cy - rr + 1.2f, rr * 2, rr * 2);
        }

        // 圆环：外圈实色 + 一圈内描边压出厚度
        var outerRect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        using (var ringBrush = new SolidBrush(ring))
            g.FillEllipse(ringBrush, outerRect);
        using (var inner = new Pen(Color.FromArgb(38, 255, 255, 255), Math.Max(1f, r * 0.09f)))
            g.DrawArc(inner, cx - r + r * 0.08f, cy - r + r * 0.08f, (r - r * 0.08f) * 2, (r - r * 0.08f) * 2, 178f, 150f);

        // 维修中：沿圆环绕圈跑的一段亮光（只有动效，不改变状态色）
        if (_repairing)
        {
            using var arc = new Pen(Color.FromArgb(235, 255, 255, 255), Math.Max(2f, r * 0.20f))
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var w = r - r * 0.20f;
            g.DrawArc(arc, cx - w, cy - w, w * 2, w * 2, _angle, 84f);
        }

        // 黑色圆心：留一圈极细的暗边，避免和圆环糊在一起
        using (var coreRect = new SolidBrush(Color.Black))
            g.FillEllipse(coreRect, cx - core, cy - core, core * 2, core * 2);
        using (var rim = new Pen(Color.FromArgb(70, 0, 0, 0), Math.Max(1f, r * 0.05f)))
            g.DrawEllipse(rim, cx - core, cy - core, core * 2, core * 2);

        return bmp;
    }

    /// <summary>圆环状态色：正常=品牌青绿(#83CC72)，降级=黄，故障=红</summary>
    private static Color RingColor(HealthState s) => s switch
    {
        HealthState.Healthy => Color.FromArgb(131, 204, 114),
        HealthState.Degraded => Color.FromArgb(245, 179, 1),
        HealthState.Broken => Color.FromArgb(229, 72, 77),
        HealthState.Cooling => Color.FromArgb(160, 120, 220),
        _ => Color.FromArgb(144, 149, 160),
    };

    // ── 鼠标 ─────────────────────────────────────────────────────

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hoverTarget = 1f;
        if (_dock != DockEdge.None)   // 贴边状态：鼠标进来就滑出来
        {
            _hideAt = 0;
            _slideTarget = 1f;
        }
        _timer.Start();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverTarget = 0f;
        if (_dock != DockEdge.None && !_pressed)   // 贴边状态：离开一会儿就自己收回去
            _hideAt = Environment.TickCount64 + HideDelayMs;
        if (!_pressed) _timer.Start();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _hideAt = 0;

        // 贴边收起时被按住：先完整滑出来，再按新的位置算拖动偏移
        if (_dock != DockEdge.None && _slide < 0.99f)
        {
            _slide = 1f;
            _slideTarget = 1f;
            Location = _dockShown;
            Render();
        }

        _pressed = true;
        _dragging = false;
        _downScreen = Cursor.Position;
        _downLoc = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed) return;
        var now = Cursor.Position;
        var dx = now.X - _downScreen.X;
        var dy = now.Y - _downScreen.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < 4) return;   // 抖动阈值，避免"想点变成拖"
        _dragging = true;
        Location = ClampToScreen(new Point(_downLoc.X + dx, _downLoc.Y + dy));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        Capture = false;
        var wasDrag = _dragging;
        _pressed = false;
        _dragging = false;

        if (wasDrag)
        {
            SnapToEdge();           // 松手时判断有没有贴边
            SavePosition();
        }
        else
        {
            _disp.ToggleMainWindow();
            _hoverTarget = 0f;
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Right)
        {
            try { _menu.Show(Cursor.Position); } catch { }
        }
    }

    private Point RestorePosition()
    {
        var s = DpiScale();
        var w = (int)Math.Ceiling(BoxSize * s);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);

        if (_cfg.BallX >= 0 && _cfg.BallY >= 0)
            return ClampToScreen(new Point(_cfg.BallX, _cfg.BallY));

        // 默认位置：主屏右侧、竖直居中偏下（避开任务栏搜索框那一块）
        return ClampToScreen(new Point(area.Right - w - 8, area.Top + (area.Height - w) * 2 / 3));
    }

    private void SavePosition()
    {
        try
        {
            var p = PositionForSave();
            _cfg.BallX = p.X;
            _cfg.BallY = p.Y;
            _cfg.Save();
        }
        catch (Exception ex) { AppLog.Swallow("FloatingBall.SavePosition", ex); }
    }

    private Point ClampToScreen(Point p)
    {
        var s = _scale <= 0 ? 1f : _scale;
        var w = (int)Math.Ceiling(BoxSize * s);
        var vs = SystemInformation.VirtualScreen;
        var x = Math.Max(vs.Left, Math.Min(p.X, vs.Right - w));
        var y = Math.Max(vs.Top, Math.Min(p.Y, vs.Bottom - w));
        return new Point(x, y);
    }

    // ── 贴边停靠 ─────────────────────────────────────────────────

    /// <summary>启动时按保存的位置判断要不要贴边（上次贴边的，重启后照样贴边收起）。</summary>
    private void InitDock()
    {
        try
        {
            _scale = DpiScale();
            SnapToEdge();
            _slide = 1f;
            _slideTarget = 1f;
            if (_dock == DockEdge.None) return;

            Location = _dockShown;
            _hideAt = Environment.TickCount64 + 1500;   // 先完整露 1.5 秒让人看见，然后自己收起
            _timer.Start();
        }
        catch (Exception ex) { AppLog.Swallow("FloatingBall.InitDock", ex); }
    }

    /// <summary>
    /// 松手位置离哪条边缘最近就贴哪条；超过 SnapPx 则取消停靠。
    /// 用「工作区」而不是「整个屏幕」——否则贴右边会钻到任务栏底下。
    /// </summary>
    private void SnapToEdge()
    {
        try
        {
            _scale = DpiScale();
            var w = (int)Math.Ceiling(BoxSize * _scale);
            var probe = new Point(Location.X + w / 2, Location.Y + w / 2);
            var area = Screen.FromPoint(probe).WorkingArea;

            var dl = Location.X - area.Left;
            var dr = area.Right - (Location.X + w);
            var dt = Location.Y - area.Top;
            var db = area.Bottom - (Location.Y + w);
            var best = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));

            _dock = best > SnapPx ? DockEdge.None
                  : best == dl ? DockEdge.Left
                  : best == dr ? DockEdge.Right
                  : best == dt ? DockEdge.Top
                  : DockEdge.Bottom;

            if (_dock == DockEdge.None) { _slide = _slideTarget = 1f; return; }

            var peek = (int)Math.Ceiling(PeekPx * _scale);
            switch (_dock)
            {
                case DockEdge.Left:
                    _dockShown = new Point(area.Left, Location.Y);
                    _dockHidden = new Point(area.Left - w + peek, Location.Y);
                    break;
                case DockEdge.Right:
                    _dockShown = new Point(area.Right - w, Location.Y);
                    _dockHidden = new Point(area.Right - peek, Location.Y);
                    break;
                case DockEdge.Top:
                    _dockShown = new Point(Location.X, area.Top);
                    _dockHidden = new Point(Location.X, area.Top - w + peek);
                    break;
                default:
                    _dockShown = new Point(Location.X, area.Bottom - w);
                    _dockHidden = new Point(Location.X, area.Bottom - peek);
                    break;
            }

            // 落边后就收起来（和 QQ 一样：一松手就贴进去）
            Location = _dockShown;
            _slide = 1f;
            _slideTarget = 0f;
            _hideAt = 0;
            _timer.Start();
        }
        catch (Exception ex) { AppLog.Swallow("FloatingBall.SnapToEdge", ex); }
    }

    /// <summary>贴边状态下记住的是"完全展开位"，这样重启后还能算回收起位。</summary>
    private Point PositionForSave()
        => _dock == DockEdge.None ? Location : _dockShown;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _timer.Stop(); _timer.Dispose(); } catch { }
            try { _tip.Dispose(); } catch { }
            try { _menu.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
