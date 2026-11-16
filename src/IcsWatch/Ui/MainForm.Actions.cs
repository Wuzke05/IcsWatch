using System.Drawing;
using System.Windows.Forms;
using IcsWatch.Config;
using IcsWatch.Core;
using IcsWatch.Monitor;

namespace IcsWatch.Ui;

public sealed partial class MainForm
{
    public void CheckNow()
    {
        AppendLog("手动触发体检…");
        RefreshAll();
    }


    public void RepairNow(bool full)
    {
        // 游戏模式下卡1/卡2 是被**主动禁用**的，维修等于跟游戏模式对打 —— 直接拒绝
        if (_cfg.GameModeActive)
        {
            MessageBox.Show(this,
                "游戏模式进行中：卡1 / 卡2 是主动暂停的，不需要维修。\n\n要恢复网络请到「游戏模式」页点「退出游戏模式」。",
                "IcsWatch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_engine.IsRepairing) { MessageBox.Show(this, "已有维修在进行中。", "IcsWatch"); return; }
        var reason = full ? "手动全量重建" : "手动一键维修";
        if (full)
        {
            var r = MessageBox.Show(this,
                "全量重建会重启 ICS 共享服务，网络中断约 10~30 秒。\n\n确认继续？",
                "IcsWatch —— 全量重建", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) return;
        }
        AppendLog($"开始{reason}…");
        _btnRepair.Enabled = false;
        Task.Run(() => _engine.RunRepair(full, reason));
    }
}
