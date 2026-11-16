using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using IcsWatch.Core;

namespace IcsWatch.Ui;

/// <summary>
/// 启动动画：一张圆角小卡片，左边是线稿 + 旋转弧，下面是状态文字和进度条。
///
/// 【设计取舍】
/// · 不抢焦点：加 WS_EX_NOACTIVATE，程序从计划任务/看门狗拉起时不会打断用户正在做的事。
/// · 不假装：进度条不是"假动画"，它的推进会跟真实步骤绑定（加载配置 → 建界面 → 起引擎）；
///   步骤太快就停在最终值等最短展示时间，而不是硬拖时间。
/// · 最短展示 900ms：低于这个人眼会觉得"闪了一下"，反而像故障。
/// </summary>
public sealed class SplashForm : Form
{
    private const int LW = 420, LH = 236;      // 96dpi 下的逻辑尺寸
    private const int MinShowMs = 900;

    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly Bitmap? _face;
    private string _status = "正在加载配置…";
    private float _target = 0.15f;
    private float _shown;
    private float _angle;
    private float _alpha = 1f;
    private bool _pumping;
    private bool _done;

    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(15, 17, 22);
        DoubleBuffered = true;
        _face = AppIcon.Face;

        var scr = Screen.FromPoint(Cursor.Position);
        var r = scr.WorkingArea;
        var s = ScaleFactor(scr);
        Location = new Point(r.Left + (r.Width - (int)(LW * s)) / 2,
                             r.Top + (r.Height - (int)(LH * s)) / 2);
    }

    /// <summary>不激活、不进任务栏、不进 Alt-Tab</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
            return cp;
        }
    }

    private static float ScaleFactor(Screen scr)
    {
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            return Math.Max(1f, g.DpiX / 96f);
        }
        catch { return 1f; }
    }

    // ── 对外：启动流程驱动 ────────────────────────────────────────

    /// <summary>显示并画出第一帧</summary>
    public void ShowBoot()
    {
        try
        {
            Show();
            Pump();
        }
        catch (Exception ex) { AppLog.Swallow("SplashForm.ShowBoot", ex); }
    }

    /// <summary>推进到某一步（文字 + 进度 0..1），并立刻重画一帧</summary>
    public void Step(string status, float progress)
    {
        _status = status;
        _target = Math.Clamp(progress, 0f, 1f);
        Pump();
    }

    /// <summary>收尾：补足最短展示时间 → 淡出 → 关闭（会 pump 消息，调用方不用管）</summary>
    public void Finish(string? status = null, float progress = 1f)
    {
        if (_done) return;
        try
        {
            if (status != null) _status = status;
            _target = progress;

            // 步骤跑得比肉眼快就等一下；已经超过最短时间就直接收
            while (_sw.ElapsedMilliseconds < MinShowMs)
            {
                Pump();
                Application.DoEvents();
                Thread.Sleep(16);
            }
            Pump();

            for (var a = 1f; a > 0f; a -= 0.14f)
            {
                _alpha = Math.Max(0f, a);
                Pump();
                Application.DoEvents();
                Thread.Sleep(16);
            }
        }
        catch (Exception ex) { AppLog.Swallow("SplashForm.Finish", ex); }
        finally
        {
            _done = true;
            try { Close(); } catch { }
            try { Dispose(); } catch { }
        }
    }

    /// <summary>推进一帧动画（不处理消息；调用方需要时自己 DoEvents）</summary>
    public void Pump()
    {
        if (_pumping || _done || IsDisposed) return;
        _pumping = true;
        try
        {
            _angle = (_angle + 7f) % 360f;
            _shown += (_target - _shown) * 0.22f;
            if (_target - _shown < 0.004f) _shown = _target;

            using var bmp = Render();
            Layered.Apply(Handle, bmp, Location);
        }
        catch (Exception ex) { AppLog.Swallow("SplashForm.Pump", ex); }
        finally { _pumping = false; }
    }

    // ── 绘制 ──────────────────────────────────────────────────────

    private float _scale = 1f;

    private Bitmap Render()
    {
        var scr = Screen.FromPoint(Location);
        _scale = ScaleFactor(scr);
        var s = _scale;

        var bmp = new Bitmap((int)Math.Ceiling(LW * s), (int)Math.Ceiling(LH * s), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.ScaleTransform(s, s);

            var card = Rounded(new RectangleF(0.5f, 0.5f, LW - 1f, LH - 1f), 18f);

            // 背景：上深下浅一点点，避免纯色板感
            using (var bg = new LinearGradientBrush(new RectangleF(0, 0, LW, LH),
                       Color.FromArgb(14, 16, 21), Color.FromArgb(28, 32, 43), 90f))
                g.FillPath(bg, card);
            using (var edge = new Pen(Color.FromArgb(46, 52, 66), 1f))
                g.DrawPath(edge, card);

            // 左上角品牌图形（与应用图标同一张图）+ 旋转弧
            var cx = 34f + 34f; var cy = 40f + 34f;
            using (var glow = new SolidBrush(Color.FromArgb(26, 253, 226, 77)))
                g.FillEllipse(glow, cx - 38, cy - 38, 76, 76);
            using (var track = new Pen(Color.FromArgb(38, 46, 60), 2.4f))
                g.DrawEllipse(track, cx - 33, cy - 33, 66, 66);
            using (var arc = new Pen(Theme.Primary, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(arc, cx - 33, cy - 33, 66, 66, _angle, 96f);
            if (_face != null)
            {
                var tile = Rounded(new RectangleF(cx - 24, cy - 24, 48, 48), 11f);
                var saved = g.Clip;
                g.SetClip(tile);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_face, new RectangleF(cx - 24, cy - 24, 48, 48));
                g.Clip = saved;
            }

            // 标题
            using (var t = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold, GraphicsUnit.Point))
            using (var b = new SolidBrush(Color.FromArgb(244, 246, 250)))
                g.DrawString("IcsWatch", t, b, 118f, 40f);
            using (var t = new Font("Microsoft YaHei UI", 9F))
            using (var b = new SolidBrush(Color.FromArgb(124, 131, 145)))
                g.DrawString("ICS 双网卡守护", t, b, 120f, 78f);
            using (var t = new Font("Microsoft YaHei UI", 8F))
            using (var b = new SolidBrush(Color.FromArgb(86, 93, 108)))
            {
                var v = typeof(SplashForm).Assembly.GetName().Version;
                g.DrawString($"v{v?.Major}.{v?.Minor}.{v?.Build}", t, b, 120f, 100f);
            }

            // 状态文字
            using (var t = new Font("Microsoft YaHei UI", 9F))
            using (var b = new SolidBrush(Color.FromArgb(169, 176, 190)))
                g.DrawString(_status, t, b, 34f, 150f);

            // 进度条
            var pct = _shown;
            var trackRect = new RectangleF(34f, 182f, LW - 68f, 6f);
            using (var p = Rounded(trackRect, 3f))
            using (var b = new SolidBrush(Color.FromArgb(35, 40, 51)))
                g.FillPath(b, p);
            if (pct > 0.002f)
            {
                var fillRect = new RectangleF(34f, 182f, Math.Max(6f, (LW - 68f) * pct), 6f);
                using var p = Rounded(fillRect, 3f);
                using var b = new LinearGradientBrush(fillRect, Theme.Primary, Color.FromArgb(34, 211, 166), 0f);
                g.FillPath(b, p);
            }

            using (var t = new Font("Microsoft YaHei UI", 8F))
            using (var b = new SolidBrush(Color.FromArgb(86, 93, 108)))
            {
                var txt = $"{(int)Math.Round(pct * 100)}%";
                var sz = g.MeasureString(txt, t);
                g.DrawString(txt, t, b, LW - 34f - sz.Width, 170f);
            }
        }

        if (_alpha >= 0.999f) return bmp;

        // 淡出：整体乘一个 alpha（分层窗口不能靠 Form.Opacity，只能烘焙进位图）
        using var src = (Bitmap)bmp.Clone();
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var ia = new ImageAttributes();
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = _alpha });
            g.DrawImage(src, new Rectangle(0, 0, bmp.Width, bmp.Height),
                0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        }
        return bmp;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2f;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
