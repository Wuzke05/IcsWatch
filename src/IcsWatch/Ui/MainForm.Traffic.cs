using System.Drawing;
using System.Windows.Forms;
using IcsWatch.Config;
using IcsWatch.Core;
using IcsWatch.Monitor;

namespace IcsWatch.Ui;

public sealed partial class MainForm
{
    private TabPage BuildTrafficTab()
    {
        var page = new TabPage("流量") { BackColor = Theme.PageBg };

        // 先建曲线控件（后面的按钮高亮要用到 _chart.WindowSeconds）
        _chart = new TrafficChart(_engine.Sampler, _engine.Devices) { Dock = DockStyle.Fill };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6, 6, 6, 0) };
        _lblTrafficCap = MkLabel("各设备吞吐", 10F, true);
        bar.Controls.Add(_lblTrafficCap);
        bar.Controls.Add(new Label { Width = 24 });
        foreach (var (label, sec) in new[] { ("60 秒", 60), ("10 分钟", 600), ("1 小时", 3600), ("24 小时", 86400) })
        {
            var b = MkButton(label, 84, Theme.Unknown);
            b.Height = 28;
            b.Font = new Font("Microsoft YaHei UI", 9F);
            var s = sec;
            b.Click += (_, _) => { _chart.WindowSeconds = s; _chart.Invalidate(); HighlightWinButtons(); };
            _winBtns.Add(b);
            bar.Controls.Add(b);
        }
        HighlightWinButtons();

        _gridDev = MkGrid();
        _gridDev.Columns.Add("ip", "IP 地址");
        _gridDev.Columns.Add("mac", "MAC");
        _gridDev.Columns.Add("kind", "类型");
        _gridDev.Columns.Add("state", "状态");
        _gridDev.Dock = DockStyle.Bottom;
        _gridDev.Height = 168;

        page.Controls.Add(_chart);
        page.Controls.Add(_gridDev);
        page.Controls.Add(bar);
        return page;
    }


    private void HighlightWinButtons()
    {
        foreach (var b in _winBtns)
        {
            var sec = b.Text switch
            {
                "60 秒" => 60, "10 分钟" => 600, "1 小时" => 3600, "24 小时" => 86400, _ => 60,
            };
            b.BackColor = sec == _chart.WindowSeconds ? Theme.Primary : Theme.Unknown;
        }
    }


    /// <summary>
    /// 设备在线状态：后台并行探测 + 缓存。
    /// 注意不要在 UI 线程里串行 ping（6 台 × 1.2s = 卡顿 7 秒）。
    /// </summary>
    private void RefreshDeviceGrid()
    {
        // 游戏模式轻量化：卡1 已停用，设备探测本来查不到东西 —— 直接跳过（省每 4 秒 6 次 ping）
        if (_cfg.GameModeLite && _cfg.GameModeActive) return;

        var prefix = string.Join(".", _cfg.PrivateIp.Split('.').Take(3)) + ".";
        var devs = NetProbe.GetDevices(_cfg.PrivateAdapter, prefix);

        // 【只列真的用过的设备】ARP 邻居表里留着大量"换过 IP 的旧地址"（Permanent，永不自动过期），
        // 全列出来会让人以为有好几台设备。判据：抓包记到过字节，或最近 30 分钟活跃，或 ping 得通。
        try
        {
            var since = DateTime.Now.AddMinutes(-30);
            var used = new HashSet<string>(
                _engine.Traffic.Snapshot().Hosts
                    .Where(h => h.TodayRx + h.TodayTx + h.SessionRx + h.SessionTx > 0 || h.LastSeen >= since)
                    .Select(h => h.Ip),
                StringComparer.OrdinalIgnoreCase);
            devs = devs
                .Where(d => used.Contains(d.Ip) ||
                            (_devOnline.TryGetValue(d.Ip, out var on) && on))
                .ToList();
        }
        catch (Exception ex) { AppLog.Swallow("MainForm.Traffic.cs:knownIps", ex); }

        // 设备集合（IP+MAC）没变就不重建表格 —— 原来只比数量，700ms 一次的 TickUi
        // 会反复 Clear+重填，等于白白重绘整张表。
        var sig = string.Join("|", devs.Select(d => d.Ip + "|" + d.Mac));
        if (sig != _devSig)
        {
            _devSig = sig;
            _gridDev.Rows.Clear();
            foreach (var d in devs)
            {
                var idx = _gridDev.Rows.Add(d.Ip, d.Mac, d.Label, "…");
                _gridDev.Rows[idx].Cells["state"].Style.ForeColor = Theme.Unknown;
            }
        }

        // 用缓存刷新显示（只在文本/颜色真的变化时才写，避免无谓重绘）
        for (int i = 0; i < _gridDev.Rows.Count && i < devs.Count; i++)
        {
            if (!_devOnline.TryGetValue(devs[i].Ip, out var online)) continue;
            var cell = _gridDev.Rows[i].Cells["state"];
            var text = online ? "在线" : "无响应";
            var color = online ? Theme.SuccessText : Theme.Unknown;
            if (!Equals(cell.Value?.ToString(), text)) cell.Value = text;
            if (cell.Style.ForeColor != color) cell.Style.ForeColor = color;
        }

        // 每 4 秒后台并行探一次
        if (_devPingRunning || (DateTime.Now - _lastDevPing).TotalSeconds < 4) return;
        _lastDevPing = DateTime.Now;
        _devPingRunning = true;
        var snapshot = devs;
        Task.Run(() =>
        {
            try
            {
                var tasks = snapshot.Select(d => Task.Run(() => d.Online = IcmpProbe.PingPlain(d.Ip, 1200))).ToArray();
                Task.WaitAll(tasks, 3000);
                lock (_devOnline)
                {
                    foreach (var d in snapshot) _devOnline[d.Ip] = d.Online;
                }
            }
            catch (Exception __ex) { AppLog.Swallow("MainForm.Traffic.cs:106", __ex); }
            finally { _devPingRunning = false; }
        });
    }

    // ════════════════════════════════════════════════════════════
    //  动作
    // ════════════════════════════════════════════════════════════
}
