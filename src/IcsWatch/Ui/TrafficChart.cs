using System.Drawing.Drawing2D;
using IcsWatch.Monitor;

namespace IcsWatch.Ui;

/// <summary>
/// 流量曲线 —— 每台下游设备一条线，设备数量由抓包自己决定（有几台画几条）。
///
/// 同一台设备的颜色固定不变（颜色按下标分配，不随排序变），
/// 实线是它的下行、虚线是它的上行；图例里带当前速率。
/// 原来只有"整条线路的下行/上行"两根线，看不出是谁在占带宽。
/// </summary>
public sealed class TrafficChart : Control
{
    private readonly TrafficSampler _sampler;
    private readonly DeviceSeries _devices;

    public int WindowSeconds { get; set; } = 60;
    public TrafficSample? Latest { get; private set; }

    private static readonly Color CGrid = Theme.ChartGrid;
    private static readonly Color CAxis = Theme.ChartAxis;
    private static readonly Color CMuted = Color.FromArgb(140, 140, 150);

    /// <summary>设备曲线调色板 —— 相邻色相拉开，深色底上看也分得清</summary>
    private static readonly Color[] Palette =
    {
        Color.FromArgb(42, 125, 225),   // 蓝
        Color.FromArgb(232, 116, 59),   // 橙
        Color.FromArgb(25, 169, 121),   // 绿
        Color.FromArgb(148, 94, 207),   // 紫
        Color.FromArgb(19, 164, 180),   // 青
        Color.FromArgb(227, 80, 106),   // 玫红
        Color.FromArgb(139, 122, 46),   // 橄榄
        Color.FromArgb(108, 112, 124),  // 灰
        Color.FromArgb(192, 57, 43),    // 砖红
        Color.FromArgb(46, 134, 193),   // 天蓝
        Color.FromArgb(120, 160, 40),   // 草绿
        Color.FromArgb(170, 90, 160),   // 品红
    };

    public static Color ColorOf(int slot) => Palette[((slot % Palette.Length) + Palette.Length) % Palette.Length];

    public TrafficChart(TrafficSampler sampler, DeviceSeries devices)
    {
        _sampler = sampler;
        _devices = devices;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Size = new Size(640, 240);
    }

    public void Push(TrafficSample s)
    {
        Latest = s;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var series = _devices.Window(WindowSeconds);
        var latest = Latest ?? _sampler.Latest;

        using var fLegend = new Font("Microsoft YaHei UI", 8F);
        using var fSmall = new Font("Microsoft YaHei UI", 8F);

        // ── 图例：先量一遍要占几行，再用它决定绘图区顶部 ──
        const int LegendPad = 10;
        var entries = series.Select(s =>
        {
            var text = $"{s.Ip}  ↓{Rate(s.RxMbps)} ↑{Rate(s.TxMbps)}";
            return (s, text, w: g.MeasureString(text, fLegend).Width + 26);
        }).ToList();

        var legendRows = 1;
        {
            float x = LegendPad, avail = Math.Max(100, Width - LegendPad * 2);
            foreach (var it in entries)
            {
                if (x + it.w > avail && x > LegendPad) { legendRows++; x = LegendPad; }
                x += it.w + 10;
            }
        }
        var legendH = entries.Count == 0 ? 0 : legendRows * 16 + 6;
        var mt = 12 + legendH + 6;

        const int ml = 62, mr = 14, mb = 30;
        var plot = new Rectangle(ml, mt, Math.Max(10, Width - ml - mr), Math.Max(10, Height - mt - mb));
        if (plot.Width < 20 || plot.Height < 20) return;

        // ── 纵轴上限：所有设备的最大速率 ──
        double max = 1;
        foreach (var s in series) max = Math.Max(max, s.Points.Max(p => Math.Max(p.RxMbps, p.TxMbps)));
        max = NiceCeil(max);

        using var penGrid = new Pen(CGrid, 1);
        using var penAxis = new Pen(CAxis, 1);
        using var brAxis = new SolidBrush(CAxis);
        for (int i = 0; i <= 4; i++)
        {
            float y = plot.Bottom - plot.Height * i / 4f;
            g.DrawLine(i == 0 ? penAxis : penGrid, plot.Left, y, plot.Right, y);
            var lbl = max * i / 4;
            var t = lbl >= 100 ? lbl.ToString("0") : lbl.ToString("0.##");
            var sz = g.MeasureString(t, fSmall);
            g.DrawString(t, fSmall, brAxis, plot.Left - 6 - sz.Width, y - sz.Height / 2);
        }
        g.DrawString("Mbps", fSmall, brAxis, 4, plot.Top - 16);

        // ── 曲线 ──
        var t1 = DateTime.Now;
        var t0 = t1.AddSeconds(-WindowSeconds);
        var span = Math.Max(1.0, (t1 - t0).TotalSeconds);
        PointF P(DeviceSeries.Point p, double v) => new(
            plot.Left + (float)((p.Time - t0).TotalSeconds / span * plot.Width),
            plot.Bottom - (float)(v / max * plot.Height));

        foreach (var s in series)
        {
            if (s.Points.Count == 0) continue;
            var c = ColorOf(s.Slot);
            using var penRx = new Pen(c, 1.8f) { LineJoin = LineJoin.Round };
            using var penTx = new Pen(c, 1.2f) { LineJoin = LineJoin.Round, DashStyle = DashStyle.Dash };

            if (s.Points.Count >= 2)
            {
                g.DrawLines(penRx, s.Points.Select(p => P(p, p.RxMbps)).ToArray());
                g.DrawLines(penTx, s.Points.Select(p => P(p, p.TxMbps)).ToArray());
            }
            var lastPt = P(s.Points[^1], s.Points[^1].RxMbps);
            using var dot = new SolidBrush(c);
            g.FillEllipse(dot, lastPt.X - 3f, lastPt.Y - 3f, 6, 6);
        }

        if (series.Count == 0)
        {
            using var brEmpty = new SolidBrush(Color.FromArgb(170, 170, 178));
            using var fEmpty = new Font("Microsoft YaHei UI", 9F);
            g.DrawString("正在采集…（还没有设备流量）", fEmpty, brEmpty, plot.Left + 12, plot.Top + 12);
        }
        else
        {
            // ── 图例（左上，逐行排） ──
            float x = LegendPad, y = 6;
            foreach (var it in entries)
            {
                if (x + it.w > Width - LegendPad && x > LegendPad) { x = LegendPad; y += 16; }
                using (var chip = new SolidBrush(ColorOf(it.s.Slot)))
                    g.FillRectangle(chip, x, y + 4, 9, 9);
                using var brText = new SolidBrush(Color.FromArgb(60, 62, 70));
                g.DrawString(it.text, fLegend, brText, x + 13, y);
                x += it.w + 10;
            }
        }

        // ── 时间窗提示（右上） ──
        using var fWin = new Font("Microsoft YaHei UI", 8.5F);
        using var brWin = new SolidBrush(CMuted);
        var winText = WindowSeconds switch
        {
            60 => "近 60 秒",
            600 => "近 10 分钟",
            3600 => "近 1 小时",
            86400 => "近 24 小时",
            _ => $"近 {WindowSeconds} 秒",
        };
        var wsz = g.MeasureString(winText, fWin);
        g.DrawString(winText, fWin, brWin, plot.Right - wsz.Width, 6);

        // ── 线路累计（底部） ──
        if (latest != null)
        {
            var cum = $"线路累计  收 {Fmt(latest.TotalRxBytes)}  发 {Fmt(latest.TotalTxBytes)}   " +
                      $"丢包  收 {latest.RxDiscard} / 发 {latest.TxDiscard}";
            g.DrawString(cum, fWin, brWin, plot.Left, plot.Bottom + 8);
        }
    }

    private static string Rate(double mbps) => mbps >= 10 ? $"{mbps:0}" : mbps >= 1 ? $"{mbps:0.0}" : $"{mbps:0.00}";

    private static string Fmt(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";
        return (bytes / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
    }

    private static double NiceCeil(double v)
    {
        if (v <= 1) return 1;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double n = v / exp;
        double m = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
        return m * exp;
    }
}
