using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IcsWatch.Ui;

/// <summary>
/// 逐像素透明（per-pixel alpha）分层窗口。
///
/// 【为什么不用 TransparencyKey / Region】
/// 色键抠图只能做"全透或全不透"，圆形边缘会出现一圈硬锯齿；
/// 用 Region 把窗口裁成圆形同理，边缘是二值的。
/// UpdateLayeredWindow 直接提交一张 32 位 ARGB 位图，半透明像素由系统合成，
/// 圆边、阴影、渐变才是真正平滑的 —— 悬浮球和启动卡都靠这一条。
///
/// 【为什么自己建 DIB 而不 Bitmap.GetHbitmap】
/// GetHbitmap 出来的 HBITMAP 在多数 GDI+ 版本里会丢掉 alpha 通道，
/// 提交上去等于整窗全透明：UpdateLayeredWindow 返回 TRUE、窗口也在，
/// 屏幕上却什么都没有。自己 CreateDIBSection 拷像素、并按预乘 alpha 写，
/// 才能保证半透明边缘真的被合成出来。
///
/// 【代价】窗口的 OnPaint 不再起作用，所有绘制都得自己画到位图上再提交。
/// </summary>
internal static class Layered
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int GWL_EXSTYLE = -20;
    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
        int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint offset);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int CX, CY; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    /// <summary>加上 WS_EX_LAYERED（重复调用无害）</summary>
    public static void EnsureLayered(IntPtr hwnd)
    {
        try
        {
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((ex & WS_EX_LAYERED) == 0) SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
        }
        catch { }
    }

    /// <summary>
    /// 把整张位图提交为窗口内容。窗口位置用 <paramref name="screenPos"/> 指定
    /// （顶层的 Location 就是屏幕坐标，但显式传入能避免"先移动再重画"的闪烁）。
    /// </summary>
    public static void Apply(IntPtr hwnd, Bitmap bmp, Point screenPos)
    {
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hBitmap = IntPtr.Zero, oldBitmap = IntPtr.Zero;
        Bitmap? armed = null;
        try
        {
            EnsureLayered(hwnd);
            int w = bmp.Width, h = bmp.Height;
            if (w <= 0 || h <= 0) return;

            screenDc = GetDC(IntPtr.Zero);
            memDc = CreateCompatibleDC(screenDc);

            var bi = new BITMAPINFO();
            bi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bi.bmiHeader.biWidth = w;
            bi.bmiHeader.biHeight = -h;      // 负高度 = 自顶向下，和 Bitmap 行序一致
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = 0;  // BI_RGB

            hBitmap = CreateDIBSection(screenDc, ref bi, 0, out var bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero) return;

            // 把 32bpp ARGB 拷进去，同时做预乘（UpdateLayeredWindow 要求预乘 alpha）
            if (bmp.PixelFormat != PixelFormat.Format32bppArgb)
            {
                armed = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using var gg = Graphics.FromImage(armed);
                gg.DrawImage(bmp, 0, 0, w, h);
                bmp = armed;
            }

            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < h; y++)
                    {
                        var srcRow = (byte*)data.Scan0 + y * data.Stride;
                        var dstRow = (byte*)bits + y * w * 4;
                        for (int x = 0; x < w; x++)
                        {
                            byte b = srcRow[x * 4 + 0], g = srcRow[x * 4 + 1], r = srcRow[x * 4 + 2], a = srcRow[x * 4 + 3];
                            if (a == 255)
                            {
                                dstRow[x * 4 + 0] = b; dstRow[x * 4 + 1] = g; dstRow[x * 4 + 2] = r; dstRow[x * 4 + 3] = 255;
                            }
                            else if (a == 0)
                            {
                                dstRow[x * 4 + 0] = 0; dstRow[x * 4 + 1] = 0; dstRow[x * 4 + 2] = 0; dstRow[x * 4 + 3] = 0;
                            }
                            else
                            {
                                dstRow[x * 4 + 0] = (byte)(b * a / 255);
                                dstRow[x * 4 + 1] = (byte)(g * a / 255);
                                dstRow[x * 4 + 2] = (byte)(r * a / 255);
                                dstRow[x * 4 + 3] = a;
                            }
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(data); }

            oldBitmap = SelectObject(memDc, hBitmap);

            var size = new SIZE { CX = w, CY = h };
            var srcPt = new POINT { X = 0, Y = 0 };
            var dst = new POINT { X = screenPos.X, Y = screenPos.Y };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };

            UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref srcPt, 0, ref blend, ULW_ALPHA);
        }
        catch { /* 分层窗口不可用时静默降级：窗口仍在，只是没有逐像素透明 */ }
        finally
        {
            armed?.Dispose();
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
