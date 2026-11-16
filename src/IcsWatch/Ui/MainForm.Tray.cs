using System.Drawing;
using System.Windows.Forms;
using IcsWatch.Config;
using IcsWatch.Core;
using IcsWatch.Monitor;

namespace IcsWatch.Ui;

public sealed partial class MainForm
{
    public void ToggleFromTray()
    {
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            if (_cfg.CloseToTray || _tray != null) HideToTray();
            else WindowState = FormWindowState.Minimized;
        }
        else ShowFromTray();
    }


    public void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        NativeMethods.ForceForeground(Handle);
        Activate();
        BringToFront();
    }


    public void HideToTray()
    {
        if (_tray == null)
        {
            WindowState = FormWindowState.Minimized;
            return;
        }
        Hide();
        _tray.Balloon("IcsWatch 已转入后台", "仍在守护", ToolTipIcon.Info);
    }


    public void ExitApp()
    {
        _exiting = true;
        UserRequestedExit = true;
        try { _engine.Dispose(); } catch (Exception __ex) { AppLog.Swallow("MainForm.Tray.cs:48", __ex); }
        _tray?.Dispose();
        Application.Exit();
    }

    /// <summary>是用户主动点的"退出"（用于写标记，让看门狗别又把它拉起来）</summary>
    public bool UserRequestedExit { get; private set; }
}
