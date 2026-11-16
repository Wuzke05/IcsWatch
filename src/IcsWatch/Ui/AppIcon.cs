using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;
using IcsWatch.Core;
using Microsoft.Win32;

namespace IcsWatch.Ui;

/// <summary>
/// 应用图标资源 —— 全部从**内嵌资源**读取，单文件 exe 分发时不会丢。
///
/// 素材（Resources\ 下，由品牌图稿生成）：
///   app.ico        多尺寸（16/20/24/32/40/48/64/128/256），exe 图标 + 窗口 + 托盘
///   app-256.png    同图的 256px 位图，启动动画里画品牌图形用
///
/// 托盘图标 = 品牌图形（圆角）+ 右下角状态色圆点。
/// 【为什么要加状态点】品牌图形本身是静态的，看不出"现在正不正常"；
/// 状态点（绿=正常 黄=有警告 红=故障 紫=冷却 蓝灰=维修中）补上这个信息，
/// 和桌面悬浮球的圆环颜色一一对应。
/// </summary>
internal static class AppIcon
{
    private static Icon? _window;
    private static Bitmap? _face;
    private static bool _darkTaskbar;

    static AppIcon()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // SystemUsesLightTheme: 0=深色任务栏(要白稿) 1=浅色(要黑稿)
            var v = k?.GetValue("SystemUsesLightTheme");
            _darkTaskbar = v != null && Convert.ToInt32(v) == 0;
        }
        catch { _darkTaskbar = false; }
    }

    /// <summary>窗体 / 任务栏 / Alt-Tab 图标</summary>
    public static Icon Window => _window ??= LoadIcon() ?? SystemIcons.Application;

    /// <summary>品牌图形（256px 位图）。启动动画用它，保证和应用图标是同一张图</summary>
    public static Bitmap? Face => _face ??= LoadBitmap("app-256.png");

    /// <summary>托盘图标：线稿 + 右下角状态色圆点（维修中带弧线环）</summary>
    public static Icon Tray(HealthState state, bool repairing)
    {
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);   // 16/20/24 随 DPI
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            // 品牌图形铺满，右下角叠一个状态点
            int dot = Math.Max(6, (int)Math.Round(size * 0.42));
            var face = Face;
            if (face != null) g.DrawImage(face, new Rectangle(0, 0, size, size));

            var c = StatusColor(state, repairing);
            int dx = size - dot, dy = size - dot;

            // 状态点先描一圈白底：压住图形本色，任何壁纸/主题下都读得出来
            using (var pad = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                g.FillEllipse(pad, dx - 1, dy - 1, dot + 2, dot + 2);

            using (var fg = new SolidBrush(c))
                g.FillEllipse(fg, dx, dy, dot, dot);

            using (var edge = new Pen(Color.FromArgb(60, 0, 0, 0), 1f))
                g.DrawEllipse(edge, dx, dy, dot, dot);

            if (repairing)
            {
                using var ring = new Pen(Color.FromArgb(110, 255, 255, 255), Math.Max(1f, size / 18f));
                g.DrawArc(ring, dx + 1, dy + 1, dot - 2, dot - 2, -90, 260);
            }
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally { NativeMethods.DestroyIcon(h); }
    }

    /// <summary>状态色（统一由 Theme 提供，避免托盘/状态卡/表格三处漂移）</summary>
    public static Color StatusColor(HealthState s, bool repairing = false) => Theme.ForState(s, repairing);

    // ── 资源加载 ────────────────────────────────────────────────

    private static Icon? LoadIcon()
    {
        try
        {
            using var s = FindResource(".ico");
            if (s == null) return null;
            return new Icon(s);      // 多尺寸 ico：保留全部尺寸，系统按需取用
        }
        catch (Exception ex) { AppLog.Warn("加载内嵌图标失败: " + AppLog.Ex(ex)); return null; }
    }

    private static Bitmap? LoadBitmap(string suffix)
    {
        try
        {
            using var s = FindResource(suffix);
            if (s == null) return null;
            using var tmp = new Bitmap(s);
            return new Bitmap(tmp);  // 复制一份，避免流释放后位图失效
        }
        catch (Exception ex) { AppLog.Warn($"加载内嵌素材 {suffix} 失败: " + AppLog.Ex(ex)); return null; }
    }

    private static Stream? FindResource(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
                      .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        return name == null ? null : asm.GetManifestResourceStream(name);
    }
}
