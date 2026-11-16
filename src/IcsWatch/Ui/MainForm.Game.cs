using System.Drawing;
using System.Windows.Forms;
using IcsWatch.Config;
using IcsWatch.Core;
using IcsWatch.Monitor;

namespace IcsWatch.Ui;

public sealed partial class MainForm
{
    private TabPage BuildGameTab()
    {
        var page = new TabPage("游戏模式") { BackColor = PageBg, Padding = new Padding(18, 16, 18, 16) };

        var p = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(24, 18, 24, 18),
            BackColor = CardBg,
        };
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string cap, Control c, int h = 44)
        {
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            var l = MkLabel(cap, 9.5F, false, Theme.TextSecondary);
            l.AutoSize = false; l.Dock = DockStyle.Fill; l.TextAlign = ContentAlignment.MiddleLeft; l.Margin = new Padding(0);
            p.Controls.Add(l, 0, p.RowCount);
            p.Controls.Add(c, 1, p.RowCount);
            p.RowCount++;
        }
        p.RowCount = 0;

        _lblGameState = MkLabel("游戏模式：关", 19F, true);
        _lblGameState.AutoSize = false; _lblGameState.Dock = DockStyle.Fill;
        _lblGameState.TextAlign = ContentAlignment.MiddleLeft; _lblGameState.Margin = new Padding(0);
        Row("当前状态", _lblGameState, 54);

        _lblGameDetail = MkLabel("", 9.5F, false, Theme.TextSecondary);
        _lblGameDetail.AutoSize = false; _lblGameDetail.Dock = DockStyle.Fill;
        _lblGameDetail.TextAlign = ContentAlignment.MiddleLeft; _lblGameDetail.Margin = new Padding(0);
        Row("当前出口", _lblGameDetail, 46);

        var usbBar = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        _cmbUsb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 290, Margin = new Padding(0, 6, 12, 0) };
        var btnRescan = MkButton("重新识别", 110, Theme.Neutral, Theme.BtnHSm);
        btnRescan.Margin = new Padding(0, 5, 0, 0);
        btnRescan.Click += (_, _) => { RefreshGameAdapters(); AppendLog("已重新扫描网卡"); };
        usbBar.Controls.Add(_cmbUsb);
        usbBar.Controls.Add(btnRescan);
        Row("USB 共享网卡", usbBar);

        _flowPause = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        Row("进入时暂停", _flowPause, 56);

        // ── 自动切换 ──────────────────────────────────────────────
        var autoBar = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        var small = Theme.TextSecondary;
        _chkAuto = new CheckBox
        {
            Text = "自动切换",
            AutoSize = true,
            Checked = _cfg.GameModeAuto,
            Margin = new Padding(0, 13, 18, 0),
        };
        _chkAuto.CheckedChanged += (_, _) =>
        {
            _cfg.GameModeAuto = _chkAuto.Checked;
            _cfg.Save();
            AppendLog("自动切换游戏模式: " + (_cfg.GameModeAuto ? "开" : "关"));
        };

        Label Tiny(string t) { var l = MkLabel(t, 9F, false, small); l.AutoSize = true; l.Margin = new Padding(0, 15, 4, 0); return l; }

        _numAutoEnter = new NumericUpDown
        {
            Minimum = 3, Maximum = 600, Width = 62,
            Value = Math.Clamp(_cfg.AutoEnterDebounceSec, 3, 600),
            Margin = new Padding(0, 12, 4, 0),
        };
        _numAutoEnter.ValueChanged += (_, _) => { _cfg.AutoEnterDebounceSec = (int)_numAutoEnter.Value; _cfg.Save(); };

        _numAutoExit = new NumericUpDown
        {
            Minimum = 3, Maximum = 600, Width = 62,
            Value = Math.Clamp(_cfg.AutoExitDebounceSec, 3, 600),
            Margin = new Padding(0, 12, 4, 0),
        };
        _numAutoExit.ValueChanged += (_, _) => { _cfg.AutoExitDebounceSec = (int)_numAutoExit.Value; _cfg.Save(); };

        autoBar.Controls.AddRange(new Control[]
        {
            _chkAuto, Tiny("进"), _numAutoEnter, Tiny("秒 · 退"), _numAutoExit, Tiny("秒"),
        });
        Row("切换方式", autoBar, 50);

        // ── 轻量化 ────────────────────────────────────────────────
        var liteBar = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        _chkLite = new CheckBox
        {
            Text = "游戏模式省电",
            AutoSize = true,
            Checked = _cfg.GameModeLite,
            Margin = new Padding(0, 13, 18, 0),
        };
        _chkLite.CheckedChanged += (_, _) =>
        {
            _cfg.GameModeLite = _chkLite.Checked;
            _cfg.Save();
            AppendLog("游戏模式轻量化: " + (_cfg.GameModeLite ? "开" : "关"));
        };

        _numGameProbe = new NumericUpDown
        {
            Minimum = 2, Maximum = 120, Width = 62,
            Value = Math.Clamp(_cfg.GameProbeIntervalSec, 2, 120),
            Margin = new Padding(0, 12, 4, 0),
        };
        _numGameProbe.ValueChanged += (_, _) => { _cfg.GameProbeIntervalSec = (int)_numGameProbe.Value; _cfg.Save(); };

        liteBar.Controls.AddRange(new Control[] { _chkLite, Tiny("体检"), _numGameProbe, Tiny("秒") });
        Row("轻量化", liteBar, 50);

        var bar = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        _btnGameOn = MkButton("⚡  进入游戏模式", 190, Theme.Accent);
        _btnGameOn.Click += (_, _) => ToggleGameMode(true);
        _btnGameOff = MkButton("⏹  退出游戏模式", 190, Theme.Neutral);
        _btnGameOff.Click += (_, _) => ToggleGameMode(false);
        bar.Controls.AddRange(new Control[] { _btnGameOn, _btnGameOff });

        _chkExitOnBoot = new CheckBox
        {
            Text = "开机自动退出",
            AutoSize = true,
            Checked = _cfg.GameExitOnBoot,
            Font = new Font("Microsoft YaHei UI", 9.5F),
            ForeColor = Theme.Text,
            Margin = new Padding(14, 14, 0, 0),
        };
        _chkExitOnBoot.CheckedChanged += (_, _) =>
        {
            _cfg.GameExitOnBoot = _chkExitOnBoot.Checked;
            _cfg.Save();
            AppendLog("开机自动退出游戏模式: " + (_cfg.GameExitOnBoot ? "开" : "关"));
        };
        bar.Controls.Add(_chkExitOnBoot);
        Row("", bar, 64);

        _lblGameMsg = MkLabel("", 9F, false, Theme.TextSecondary);
        _lblGameMsg.AutoSize = false; _lblGameMsg.Dock = DockStyle.Fill;
        _lblGameMsg.TextAlign = ContentAlignment.TopLeft; _lblGameMsg.Margin = new Padding(0, 8, 0, 0);
        Row("", _lblGameMsg, 90);

        p.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(p);

        RefreshGameAdapters();
        return page;
    }


    /// <summary>扫描网卡：填 USB 候选、暂停列表、按钮可用性</summary>
    private void RefreshGameAdapters()
    {
        var cands = GameMode.Candidates();

        var cur = (_cfg.GameUsbAdapter ?? "").Trim();
        if (cur.Length == 0) cur = GameMode.GuessUsbAdapter();

        _cmbUsb.Items.Clear();
        foreach (var n in cands) _cmbUsb.Items.Add(n);
        if (cur.Length > 0 && !_cmbUsb.Items.Contains(cur)) _cmbUsb.Items.Insert(0, cur);
        _cmbUsb.Text = cur;

        // 暂停列表：首次（没保存过）默认全勾
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var saved = _cfg.GamePauseAdapters ?? new List<string>();
        // 首次默认只勾 ICS 那对网卡：卡1（分享出去的 WiFi）+ 卡2（校园网）。
        // 无线网卡不在候选里，永远不会被勾上。
        if (saved.Count > 0) foreach (var n in saved) chosen.Add(n);
        else { chosen.Add(_cfg.PrivateAdapter); chosen.Add(_cfg.PublicAdapter); }

        _flowPause.Controls.Clear();
        _chkPause.Clear();
        foreach (var n in cands)
        {
            var cb = new CheckBox
            {
                Text = n,
                AutoSize = true,
                Checked = chosen.Contains(n),
                Margin = new Padding(0, 10, 20, 0),
            };
            // ICS 两端是必停项（见 GameMode.Enter 的 mustPause）：勾上并锁住，
            // 避免看起来"可以不选" —— 游戏模式只留 USB 一路，校园网必须停
            var isIcsEnd = string.Equals(n, _cfg.PrivateAdapter, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(n, _cfg.PublicAdapter, StringComparison.OrdinalIgnoreCase);
            if (isIcsEnd) { cb.Checked = true; cb.Enabled = false; }

            _flowPause.Controls.Add(cb);
            _chkPause[n] = cb;
        }

        UpdateGameStatus();
    }


    /// <summary>只刷新状态文字/按钮（定时调用，开销小）</summary>
    private void UpdateGameStatus()
    {
        if (_lblGameState == null) return;
        var on = _cfg.GameModeActive;
        _lblGameState.Text = on ? "游戏模式：开" : "游戏模式：关";
        _lblGameState.ForeColor = on ? Theme.Accent : Theme.Neutral;
        _lblGameDetail.Text = GameMode.Describe(_cfg);
        _btnGameOn.Enabled = !on;
        _btnGameOff.Enabled = on;
    }


    private void ToggleGameMode(bool on)
    {
        if (_engine.IsRepairing) { MessageBox.Show(this, "已有维修在进行中。", "IcsWatch"); return; }

        // 先把界面选择存进配置
        _cfg.GameUsbAdapter = (_cmbUsb.Text ?? "").Trim();
        _cfg.GamePauseAdapters = _chkPause.Where(kv => kv.Value.Checked).Select(kv => kv.Key).ToList();
        _cfg.Save();

        if (on)
        {
            if (_cfg.GamePauseAdapters.Count == 0)
            {
                MessageBox.Show(this, "请至少勾选一个要暂停的网卡。", "IcsWatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var msg = $"进入游戏模式：\n\n　只保留 {_cfg.GameUsbAdapter} 出网\n" +
                      $"　禁用 {string.Join("、", _cfg.GamePauseAdapters)}\n\n" +
                      "等于把网线拔掉，局域网与下游设备会断网。确认继续？";
            if (MessageBox.Show(this, msg, "IcsWatch —— 游戏模式", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning) != DialogResult.OK) return;
        }
        else
        {
            if (MessageBox.Show(this, "退出游戏模式：\n\n　恢复被禁用的网卡\n　重建 ICS 共享（约 20 秒，期间断网）\n\n确认继续？",
                    "IcsWatch —— 退出游戏模式", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning) != DialogResult.OK) return;
        }

        _btnGameOn.Enabled = false;
        _btnGameOff.Enabled = false;
        _lblGameMsg.Text = on ? "正在切换到 USB 出网…" : "正在恢复网络…";
        AppendLog(on ? "===== 进入游戏模式 =====" : "===== 退出游戏模式 =====");

        // 切换期间抑制自动维修：GameMode 内部直接调 Repairer 做重建，不经过引擎的
        // IsRepairing 标记，否则 5 秒体检会看到"半恢复"的中间状态并插一脚。
        _engine.SuppressAutoRepair = true;

        Task.Run(() =>
        {
            var r = on
                ? GameMode.Enter(_cfg, m => Ui(() => AppendLog("  " + m)))
                : GameMode.Exit(_cfg, m => Ui(() => AppendLog("  " + m)));

            Ui(() =>
            {
                _engine.SuppressAutoRepair = false;
                _lblGameMsg.Text = r.msg;
                AppendLog((on ? "游戏模式: " : "退出游戏模式: ") + r.msg);
                RefreshGameAdapters();
                _engine.ProbeTick("游戏模式切换后复检");
            });
        });
    }

    private static NumericUpDown MkNum(int min, int max, int val)
        => new() { Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max), Width = 100 };


    /// <summary>
    /// 让总览 / 检查项 / 流量三个页面跟「游戏模式」联动：
    ///  · 总览参数卡：卡1/卡2/ICS/下游 → 「游戏模式 · 已暂停」，NAT 出口 → 手机 USB 共享
    ///  · 检查项页：由 Checker.ApplyGameModeView 把故意停掉的项改成 INFO
    ///  · 流量页：采样对象从卡1 切到手机 USB 网卡（卡1 被禁用后计数器永远是 0）
    /// </summary>
    private void SyncGameModeUi()
    {
        bool gm = _cfg.GameModeActive;
        var usb = (_cfg.GameUsbAdapter ?? "").Trim();

        // ── 流量页：采样对象跟着模式走 ──────────────────────────
        var target = gm && usb.Length > 0 ? usb : _cfg.PrivateAdapter;
        if (!string.Equals(_engine.Sampler.AdapterName, target, StringComparison.OrdinalIgnoreCase))
        {
            _engine.Sampler.SetAdapter(target);
            AppLog.Info($"流量采样对象 -> {target}");
            AppendLog($"流量采样对象 -> {target}");
        }
        // 曲线现在画的是"每台下游设备"，不再是整块网卡，标题只报采集口是谁
        if (_lblTrafficCap != null) _lblTrafficCap.Text = $"各设备吞吐（采集口 {target}）";

        // 游戏模式下禁止手动维修（会跟游戏模式对打）；退出后自动恢复可用
        _btnRepair.Enabled = !gm;
        _btnFull.Enabled = !gm;
        _btnRepair.Text = gm ? "🔧  游戏模式中" : "🔧  一键维修";

        if (!gm) return;

        // ── 总览参数卡：整行换成游戏模式语义 ────────────────────
        var ad = NetProbe.ReadAdapter(usb);
        var ip = ad.Ipv4.FirstOrDefault() ?? "无地址";
        SetKv("k1",  "游戏模式 · 已暂停", "", CheckStatus.Info);
        SetKv("k2",  "游戏模式 · 已暂停", "", CheckStatus.Info);
        SetKv("ics", "游戏模式 · 已暂停", "", CheckStatus.Info);
        SetKv("svc", "游戏模式 · 已暂停", "", CheckStatus.Info);
        SetKv("nat", ip, $"手机 USB 共享 · {usb}",
               ad.Ipv4.Count > 0 ? CheckStatus.Pass : CheckStatus.Warn);
        SetKv("dev", "游戏模式 · 已暂停", "", CheckStatus.Info);
    }
}
