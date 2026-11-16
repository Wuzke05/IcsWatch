using System.Drawing;
using System.Windows.Forms;
using IcsWatch.Config;
using IcsWatch.Core;
using IcsWatch.Monitor;

namespace IcsWatch.Ui;

public sealed partial class MainForm
{
    private void RefreshAll()
    {
        _engine.ProbeTick("手动刷新");
        RefreshTasks();
        RefreshScripts();
        RefreshRepairs();
    }


    private void TickUi()
    {
        if (_exiting) return;

        var st = _engine.IsRepairing ? HealthState.Repairing : _engine.State;
        _lblEngine.Text = $"已运行 {(DateTime.Now - _engineStart).TotalMinutes:0} 分钟" +
                          (_engine.IsRepairing
                              ? " · 正在维修…"
                              : (string.IsNullOrEmpty(_engine.AutoRepairNote) ? "" : " · " + _engine.AutoRepairNote));
        _tray?.SetState(st, BuildTrayTooltip(st));

        // 悬浮球和托盘同一份状态：颜色=健康度，悬停=同一段摘要文字
        var lat = _engine.Sampler.Latest;
        _ball?.SetState(st, _engine.IsRepairing, BuildTrayTooltip(st),
            lat?.RxMbps ?? 0, lat?.TxMbps ?? 0);

        if (_engine.Latest != null && !ReferenceEquals(_engine.Latest, _lastSnap))
        {
            _lastSnap = _engine.Latest;
            // 【关键】游戏模式下参数卡只能由 SyncGameModeUi 写。
            // 这个 TickUi 是 700ms 定时器：若在这里调 ApplyKv，就会和 5s 体检里
            // SyncGameModeUi 写的"游戏模式 · 已暂停"互相覆盖 → ICS共享/ICS服务/NAT出口
            // 后面的文字每几秒闪跳一次（实测：两套指令同时写同一批 Label）。
            if (_cfg.GameModeActive) SyncGameModeUi();
            else ApplyKv(_engine.Latest);
        }
        if (_engine.Repairs.Count != _lastRepairShown)
        {
            _lastRepairShown = _engine.Repairs.Count;
            RefreshRepairs();
        }
        RefreshDeviceGrid();
        RefreshStats();
        UpdateGameStatus();
    }

    /// <summary>状态短词（托盘提示用，避免过长被系统截断）</summary>
    private static string StateShort(HealthState s) => s switch
    {
        HealthState.Healthy => "正常",
        HealthState.Degraded => "降级",
        HealthState.Broken => "故障",
        HealthState.Repairing => "维修中",
        HealthState.Cooling => "冷却中",
        HealthState.GameMode => "游戏模式",
        _ => "未知",
    };


    /// <summary>
    /// 托盘悬停提示：分行 + 标签用全角空格对齐，信息一眼看完。
    /// ⚠️ NotifyIcon.Text 有长度上限（.NET Core 3.0+ = 127 字符），
    /// 因此这里固定 4~5 行短句，TrayIcon.SetState 里还会再按整行裁剪兜底。
    /// </summary>
    private string BuildTrayTooltip(HealthState st)
    {
        var snap = _engine.Latest;
        var pass = snap?.Count(CheckStatus.Pass) ?? 0;
        var warn = snap?.Count(CheckStatus.Warn) ?? 0;
        var fail = snap?.Count(CheckStatus.Fail) ?? 0;
        var rx = _engine.Sampler.Latest?.RxMbps ?? 0;
        var tx = _engine.Sampler.Latest?.TxMbps ?? 0;
        var online = 0;
        lock (_devOnline) online = _devOnline.Count(kv => kv.Value);

        var lines = new List<string> { $"IcsWatch · {StateShort(st)}" };

        // 有异常就把最该关注的那一项顶上来
        var bad = snap?.Items.FirstOrDefault(i => i.Status == CheckStatus.Fail)
               ?? snap?.Items.FirstOrDefault(i => i.Status == CheckStatus.Warn);
        if (bad != null)
        {
            lines.Add($"关注　{bad.Name}");
            // 告诉用户"为什么还没修"（冷却中 / 宽容期 / 已达上限），否则会以为程序没动
            if (!string.IsNullOrEmpty(_engine.AutoRepairNote))
                lines.Add($"处置　{_engine.AutoRepairNote}");
        }

        lines.Add($"检查　{pass} 通过 · {warn} 警告 · {fail} 故障");
        lines.Add($"流量　↓{rx:0.0} / ↑{tx:0.0} Mbps");
        lines.Add($"设备　{online} 台在线");
        return string.Join("\r\n", lines);
    }


    /// <summary>刷新「维修记录」列表（自动维修留痕，方便回查根因）</summary>
    private void RefreshRepairs()
    {
        List<RepairRecord> list;
        lock (_engine.Repairs) list = _engine.Repairs.Take(20).ToList();

        _gridRepairs.Rows.Clear();
        if (list.Count == 0)
        {
            _gridRepairs.Rows.Add("—", "—", "尚无维修记录", "—", "—");
            return;
        }
        foreach (var r in list)
        {
            var i = _gridRepairs.Rows.Add(r.Time.ToString("MM-dd HH:mm:ss"), r.Mode, r.Reason,
                r.ResultText, $"{r.Seconds:0.0}s");
            _gridRepairs.Rows[i].DefaultCellStyle.ForeColor =
                r.Success ? Theme.SuccessText : Theme.WarningText;
            if (!r.Success)
            {
                _gridRepairs.Rows[i].Cells["reason"].ToolTipText = r.Detail;
                _gridRepairs.Rows[i].Cells["res"].ToolTipText = r.Detail;
            }
        }
    }

    private readonly Dictionary<string, bool> _devOnline = new();
    /// <summary>设备列表签名（IP|MAC 拼接）—— 没变就不重建表格行</summary>
    private string _devSig = "";
    private DateTime _lastDevPing = DateTime.MinValue;
    private bool _devPingRunning;
}
