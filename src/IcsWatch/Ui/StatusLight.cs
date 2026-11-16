using System.Drawing.Drawing2D;
using IcsWatch.Core;

namespace IcsWatch.Ui;

/// <summary>
/// 总览页顶部「状态卡」：左侧状态圆 + 右侧大号状态文字/详情 + 最右统计徽章。
///
/// 排版要点（之前踩过的坑）：
///  · 严格裁剪 —— 所有文字都画在自己的可用宽度内，不会溢出到别的控件下面造成遮挡；
///  · 自适应 —— 文字过长时按宽度自动缩略（…），窗口再窄也不会互相压字；
///  · 徽章从右往左排，空间不够就整体不画，绝不重叠。
/// </summary>
public sealed class StatusLight : Control
{
    private HealthState _state = HealthState.Unknown;
    private string _title = "未知";
    private string _detail = "";
    private int _pass = -1, _warn = -1, _fail = -1;

    public StatusLight()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Height = 132;
    }

    public void Set(HealthState s, string title, string detail,
                    int pass = -1, int warn = -1, int fail = -1)
    {
        _state = s;
        _title = title ?? "";
        _detail = detail ?? "";
        _pass = pass; _warn = warn; _fail = fail;
        Invalidate();
    }

    // 状态色统一由 Theme 提供（托盘图标、状态卡、表格共用一套，避免三处漂移）
    public static Color ColorFor(HealthState s) => Theme.ForState(s);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);
        if (Width < 80 || Height < 48) return;

        var color = ColorFor(_state);

        // ── 左侧状态圆（垂直居中，不侵占右侧文字区）──
        float d = Math.Min(96f, Height - 30f);
        if (d < 44f) d = 44f;
        float cx = 26f, cy = (Height - d) / 2f;

        using (var glow = new SolidBrush(Color.FromArgb(30, color)))
            g.FillEllipse(glow, cx - 9, cy - 9, d + 18, d + 18);
        using (var ring = new Pen(Color.FromArgb(232, 236, 242), 2f))
            g.DrawEllipse(ring, cx, cy, d, d);
        using (var body = new SolidBrush(color))
            g.FillEllipse(body, cx + 8, cy + 8, d - 16, d - 16);
        using (var hl = new SolidBrush(Color.FromArgb(66, 255, 255, 255)))
            g.FillEllipse(hl, cx + d * 0.30f, cy + d * 0.23f, d * 0.26f, d * 0.17f);

        // ── 右侧文字区（先给徽章留位，再算文字可用宽度）──
        float tx = cx + d + 28f;
        float rightLimit = Width - 22f;

        float badgeW = MeasureBadges(g, out var badgeTexts, out var badgeFont);
        float badgeLeft = badgeW > 0 ? rightLimit - badgeW : rightLimit;

        // 【稳定布局 ①】文字区宽度不能跟着徽章宽度走：徽章文字是"通过 5/警告 0/故障 0"，
        // 计数一变（甚至位数变化）徽章就变宽/变窄，换行位置随之改变 → 看起来像文字在跳。
        // 给徽章固定预留一块区域，文字区宽度就恒定。徽章本身仍然贴右按真实宽度绘制。
        float reserve = badgeW > 0 ? Math.Max(badgeW, 300f) : 0f;
        float availW = rightLimit - reserve - 18f - tx;
        if (availW < 60f)
        {
            // 太窄：只画圆，不画字（宁可不画也不重叠）
            return;
        }

        g.SetClip(new RectangleF(tx, 0, availW, Height));

        using var fTitle = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold);
        using var fDetail = new Font("Microsoft YaHei UI", 10F);
        using var bTitle = new SolidBrush(Theme.TextTitle);
        using var bDetail = new SolidBrush(Theme.TextSecondary);

        var title = Fit(g, _title, fTitle, availW);
        float titleH = g.MeasureString(title, fTitle).Height;

        // 详情行只在"确实有内容"时占位：全好时不写解释性文案，标题自己垂直居中。
        var lines = string.IsNullOrWhiteSpace(_detail) ? new List<string>() : Wrap(g, _detail, fDetail, availW, 2);
        // 【稳定布局 ②】详情高度固定按 2 行预留（有详情时）。
        // 原来 total 按实际行数算、再垂直居中 → 详情行数在 1↔2 之间变化时，
        // 整块文字（标题+详情）会上下跳动十几像素。这是"文字跳动"的直接原因。
        const int DetailReservedLines = 2;
        float detailH = lines.Count == 0 ? 0f : DetailReservedLines * (fDetail.Height + 4f);
        float total = titleH + (detailH > 0 ? 8f + detailH : 0f);
        float y = Math.Max(14f, (Height - total) / 2f);

        g.DrawString(title, fTitle, bTitle, tx, y);
        y += titleH + 8f;
        foreach (var ln in lines)
        {
            g.DrawString(ln, fDetail, bDetail, tx, y);
            y += fDetail.Height + 4f;
        }

        g.ResetClip();

        // ── 统计徽章（右侧居中）──
        if (badgeW > 0) DrawBadges(g, badgeTexts, badgeFont, badgeLeft);
    }

    // ── 徽章 ───────────────────────────────────────────────────

    private static readonly string[] BadgeLabels = { "通过", "警告", "故障" };

    private float MeasureBadges(Graphics g, out string[] texts, out Font font)
    {
        texts = Array.Empty<string>();
        font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        if (_pass < 0 || Width < 620) return 0;          // 窗口不够宽就不显示徽章

        texts = new[] { $"{BadgeLabels[0]} {_pass}", $"{BadgeLabels[1]} {_warn}", $"{BadgeLabels[2]} {_fail}" };
        float total = 0;
        foreach (var t in texts) total += g.MeasureString(t, font).Width + 24f;   // 左右各 12px 内边距
        total += 2 * 10f;                                                          // 徽章间距
        return total;
    }

    private void DrawBadges(Graphics g, string[] texts, Font font, float left)
    {
        var colors = new[]
        {
            Theme.Success,
            Theme.Warning,
            Theme.Danger,
        };
        float h = 30f, cy = (Height - h) / 2f, x = left;

        for (int i = 0; i < texts.Length && i < colors.Length; i++)
        {
            var sz = g.MeasureString(texts[i], font);
            float w = sz.Width + 24f;
            using (var path = Round(new RectangleF(x, cy, w, h), h / 2f))
            {
                using (var bg = new SolidBrush(Color.FromArgb(20, colors[i])))
                    g.FillPath(bg, path);
                using (var pen = new Pen(Color.FromArgb(96, colors[i]), 1f))
                    g.DrawPath(pen, path);
            }
            using (var tb = new SolidBrush(colors[i]))
                g.DrawString(texts[i], font, tb, x + 12f, cy + (h - sz.Height) / 2f);
            x += w + 10f;
        }
    }

    private static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float dia = radius * 2f;
        p.AddArc(r.X, r.Y, dia, dia, 180, 90);
        p.AddArc(r.Right - dia, r.Y, dia, dia, 270, 90);
        p.AddArc(r.Right - dia, r.Bottom - dia, dia, dia, 0, 90);
        p.AddArc(r.X, r.Bottom - dia, dia, dia, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ── 文字自适应 ─────────────────────────────────────────────

    /// <summary>宽度不够时逐字缩略，保证绝不超出给定宽度</summary>
    private static string Fit(Graphics g, string s, Font f, float maxW)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (g.MeasureString(s, f).Width <= maxW) return s;
        for (int n = s.Length - 1; n > 0; n--)
        {
            var t = s[..n] + "…";
            if (g.MeasureString(t, f).Width <= maxW) return t;
        }
        return "…";
    }

    /// <summary>按宽度折行，最多 maxLines 行（避免详情把卡片撑破）</summary>
    private static List<string> Wrap(Graphics g, string s, Font f, float maxW, int maxLines)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return res;

        var cur = "";
        foreach (var ch in s)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                res.Add(cur);
                cur = "";
                if (res.Count >= maxLines) return res;
                continue;
            }
            var t = cur + ch;
            if (cur.Length > 0 && g.MeasureString(t, f).Width > maxW)
            {
                res.Add(cur);
                if (res.Count >= maxLines) return res;
                cur = ch.ToString();
            }
            else cur = t;
        }
        if (cur.Length > 0 && res.Count < maxLines) res.Add(cur);
        return res;
    }
}
