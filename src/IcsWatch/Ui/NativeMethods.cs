using System.Runtime.InteropServices;

namespace IcsWatch.Ui;

/// <summary>Win32 小工具：单实例唤醒、窗口消息等</summary>
internal static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    /// <summary>自定义消息：请求已运行的实例把主窗口唤到前台</summary>
    public static readonly int WM_ICSWATCH_SHOWMAIN = RegisterWindowMessage("IcsWatch.ShowMain.v1");

    public static void BroadcastShowMain()
    {
        try { PostMessage(HWND_BROADCAST, WM_ICSWATCH_SHOWMAIN, IntPtr.Zero, IntPtr.Zero); } catch { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    public static void ForceForeground(IntPtr hWnd)
    {
        try
        {
            ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
        }
        catch { }
    }
}
