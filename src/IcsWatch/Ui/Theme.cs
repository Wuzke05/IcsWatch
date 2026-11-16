using System.Drawing;
using System.Windows.Forms;
using IcsWatch.Core;

namespace IcsWatch.Ui;

/// <summary>
/// 界面基础样式：色板 / 字体 / 控件工厂。
///
/// 【为什么要有这一层】原来 10 个 partial 里散着 100 多处 <c>Color.FromArgb(...)</c>：
/// 同一个"次要文字灰"有 6 个不同取值（120/124/128/134/142/148…），
/// 按钮高度有 28/32/34/38/40/44/46 七种，四个表格的表头第一格还会被画成高亮蓝。
/// 结果就是六页长得像六个不同的人做的。
///
/// 现在：颜色只在这里定义、控件只从这里造。要改观感改这一个文件即可。
/// 新增界面时**不要**再写 <c>Color.FromArgb</c>，从 Theme 里取。
/// </summary>
internal static class Theme
{
    // ── 背景 ────────────────────────────────────────────────────
    public static readonly Color PageBg = Color.FromArgb(244, 246, 249);
    public static readonly Color CardBg = Color.White;

    /// <summary>分组条（表格里的设备小计行）</summary>
    public static readonly Color GroupBg = Color.FromArgb(237, 241, 246);

    // ── 文字（3 档：正文 / 标题 / 次要）──────────────────────────
    public static readonly Color Text          = Color.FromArgb(45, 45, 52);
    public static readonly Color TextTitle     = Color.FromArgb(32, 34, 42);
    public static readonly Color TextSecondary = Color.FromArgb(128, 132, 144);
    public static readonly Color TextMuted     = Color.FromArgb(150, 153, 163);

    // ── 语义色 ──────────────────────────────────────────────────
    public static readonly Color Primary     = Color.FromArgb(0, 122, 204);   // 主操作 / 选中
    public static readonly Color Success     = Color.FromArgb(76, 209, 55);   // 状态圆（大色块）
    public static readonly Color SuccessText = Color.FromArgb(46, 150, 40);   // 文字里的"正常"
    public static readonly Color Warning     = Color.FromArgb(255, 176, 32);
    public static readonly Color WarningText = Color.FromArgb(190, 130, 0);
    public static readonly Color Danger      = Color.FromArgb(232, 65, 66);
    public static readonly Color DangerText  = Color.FromArgb(210, 45, 46);
    public static readonly Color Neutral     = Color.FromArgb(108, 112, 124); // 次要按钮
    public static readonly Color Accent      = Color.FromArgb(46, 170, 220);  // 游戏模式
    public static readonly Color ActionWarn  = Color.FromArgb(158, 96, 46);   // 全量重建（有中断代价）
    public static readonly Color Repairing   = Color.FromArgb(0, 152, 235);
    public static readonly Color Cooling     = Color.FromArgb(160, 120, 220);
    public static readonly Color Unknown     = Color.FromArgb(150, 152, 160);

    // ── 图表 ────────────────────────────────────────────────────
    public static readonly Color ChartRx = Success;
    public static readonly Color ChartTx = Color.FromArgb(255, 159, 67);
    public static readonly Color ChartGrid = Color.FromArgb(232, 234, 238);
    public static readonly Color ChartAxis = Color.FromArgb(150, 152, 160);

    // ── 运行日志（终端风格）─────────────────────────────────────
    public static readonly Color LogBg = Color.FromArgb(30, 32, 38);
    public static readonly Color LogFg = Color.FromArgb(220, 222, 228);

    // ── 表格 ────────────────────────────────────────────────────
    public static readonly Color GridHeader     = Color.FromArgb(243, 245, 248);
    public static readonly Color GridHeaderText = Color.FromArgb(80, 82, 92);
    public static readonly Color GridLine       = Color.FromArgb(238, 240, 244);
    public static readonly Color GridSelection  = Color.FromArgb(225, 238, 250);

    // ── 尺寸 ────────────────────────────────────────────────────
    public const int BtnH   = 44;   // 主操作按钮
    public const int BtnHSm = 34;   // 次级按钮（表格上方那一类）

    // ── 字体 ────────────────────────────────────────────────────
    private const string Family = "Microsoft YaHei UI";

    public static Font MakeFont(float size, bool bold = false)
        => new(Family, size, bold ? FontStyle.Bold : FontStyle.Regular);

    // ── 状态 → 颜色（全程序唯一来源）────────────────────────────
    public static Color ForState(HealthState s, bool repairing = false)
    {
        if (repairing || s == HealthState.Repairing) return Repairing;
        return s switch
        {
            HealthState.Healthy  => Success,
            HealthState.Degraded => Warning,
            HealthState.Broken   => Danger,
            HealthState.Cooling  => Cooling,
            HealthState.GameMode => Accent,
            _ => Unknown,
        };
    }

    /// <summary>检查项状态 → 文字颜色（表格里用）</summary>
    public static Color ForStatus(CheckStatus s) => s switch
    {
        CheckStatus.Pass => SuccessText,
        CheckStatus.Warn => WarningText,
        CheckStatus.Fail => DangerText,
        _ => Color.FromArgb(110, 112, 122),
    };

    // ══════════════════════════════════════════════════════════
    //  控件工厂
    // ══════════════════════════════════════════════════════════

    public static Label MkLabel(string text, float size = 9F, bool bold = false, Color? color = null)
        => new()
        {
            Text = text,
            AutoSize = true,
            Font = MakeFont(size, bold),
            ForeColor = color ?? Text,
            Margin = new Padding(3, 3, 3, 3),
        };

    public static Button MkButton(string text, int width, Color back, int height = BtnH)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = height,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = Color.White,
            Font = MakeFont(10F, bold: true),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 14, 0),
        };
        b.FlatAppearance.BorderSize = 0;
        // 悬停稍微提亮一点，点击反馈靠系统默认下沉
        b.FlatAppearance.MouseOverBackColor = Lighten(back, 0.08f);
        return b;
    }

    /// <summary>
    /// 统一表格样式。
    /// 【关键修复】表头第一格原来会被画成高亮蓝（DataGridView 的列头 selection 色），
    /// 四个页面都这样，很扎眼 —— 这里把列头的选中态固定成表头底色。
    /// </summary>
    public static DataGridView MkGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = CardBg,
            BorderStyle = BorderStyle.None,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 30,
            EnableHeadersVisualStyles = false,
            GridColor = GridLine,
            RowTemplate = { Height = 26 },
        };

        g.ColumnHeadersDefaultCellStyle.BackColor = GridHeader;
        g.ColumnHeadersDefaultCellStyle.ForeColor = GridHeaderText;
        g.ColumnHeadersDefaultCellStyle.Font = MakeFont(9F, bold: true);
        // ↓ 这两行就是"表头染蓝"的解药
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = GridHeader;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = GridHeaderText;

        g.DefaultCellStyle.BackColor = CardBg;
        g.DefaultCellStyle.ForeColor = Text;
        g.DefaultCellStyle.SelectionBackColor = GridSelection;
        g.DefaultCellStyle.SelectionForeColor = TextTitle;
        return g;
    }

    /// <summary>把颜色按比例提亮（用于按钮悬停）</summary>
    private static Color Lighten(Color c, float amount)
    {
        int Ch(int v) => Math.Min(255, (int)(v + (255 - v) * amount));
        return Color.FromArgb(c.A, Ch(c.R), Ch(c.G), Ch(c.B));
    }
}
