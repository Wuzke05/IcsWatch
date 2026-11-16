using System.Drawing;
using System.Windows.Forms;
using IcsWatch.Config;
using IcsWatch.Core;
using IcsWatch.Monitor;

namespace IcsWatch.Ui;

public sealed partial class MainForm
{
    /// <summary>唤醒后稳定期（秒）—— 见 PowerStateWatcher</summary>
    private NumericUpDown _numPowerSettle = null!;

    private TabPage BuildSettingsTab()
    {
        var page = new TabPage("设置") { BackColor = PageBg, Padding = new Padding(18, 16, 18, 16) };

        // 设置项会越加越多 —— 放进可滚动容器，避免行高被压扁
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = CardBg };

        var p = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(20, 12, 20, 16),
            BackColor = CardBg,
        };
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int r = 0;

        void Row(string caption, Control c)
        {
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            var lab = MkLabel(caption, 9F);
            lab.AutoSize = false;
            lab.Dock = DockStyle.Fill;
            lab.TextAlign = ContentAlignment.MiddleLeft;
            lab.Margin = new Padding(0);
            p.Controls.Add(lab, 0, r);
            p.Controls.Add(c, 1, r);
            r++;
        }

        void Section(string title)
        {
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            var l = MkLabel(title, 11F, true);
            l.AutoSize = false;
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.BottomLeft;
            l.Margin = new Padding(0, 8, 0, 0);
            l.ForeColor = Theme.TextTitle;
            p.Controls.Add(l, 0, r);
            p.SetColumnSpan(l, 2);
            r++;
        }

        // ══ ① 网卡角色（校园网与局域网侧对调时用）══════════════════
        Section("网卡角色");

        _cmbPriv = MkAdapterCombo(_cfg.PrivateAdapter);
        Row("私有端", _cmbPriv);
        _cmbPub = MkAdapterCombo(_cfg.PublicAdapter);
        Row("公网端", _cmbPub);

        var roleBar = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(0, 4, 0, 0), Margin = new Padding(0) };
        // 一步到位：交换后立刻确认并执行（原来要点两次，很容易以为第一次就生效了）
        var btnSwap = MkButton("⇄  对调并应用", 158, Theme.Primary, Theme.BtnHSm);
        btnSwap.Click += (_, _) => SwapAndApplyRoles();
        var btnApplyRoles = MkButton("应用并全量重建", 158, Theme.ActionWarn, Theme.BtnHSm);
        btnApplyRoles.Click += (_, _) => ApplyAdapterRoles();
        roleBar.Controls.AddRange(new Control[] { btnSwap, btnApplyRoles });
        Row("", roleBar);
        // ══ ② 启动自动化 ═══════════════════════════════════════════
        Section("启动自动化");
        _chkBootRebuild = new CheckBox
        {
            Text = "启动时执行一次",
            Checked = _cfg.BootRebuildEnabled,
            AutoSize = true,
        };
        Row("启动重建", _chkBootRebuild);

        _numBootDelay = MkNum(5, 3600, _cfg.BootRebuildDelaySec);
        Row("启动后延迟（秒）", _numBootDelay);

        var bootBar = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(0, 4, 0, 0), Margin = new Padding(0) };
        var btnNow = MkButton("立即全量重建一次", 178, Theme.ActionWarn, Theme.BtnHSm);
        btnNow.Click += (_, _) => RepairNow(true);
        bootBar.Controls.Add(btnNow);
        Row("", bootBar);
        // ══ ③ 维修策略 ═════════════════════════════════════════════
        Section("维修策略");
        _cboRepair = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
        _cboRepair.Items.AddRange(new object[]
        {
            "off · 只告警",
            "light · 轻量",
            "full · 全量",
        });
        _cboRepair.SelectedIndex = _cfg.RepairMode switch { "off" => 0, "full" => 2, _ => 1 };
        Row("自动维修等级", _cboRepair);

        _numProbe = MkNum(1, 300, _cfg.ProbeIntervalSec);
        Row("体检周期（秒）", _numProbe);
        _numTraffic = MkNum(1, 60, _cfg.TrafficIntervalSec);
        Row("流量采样周期（秒）", _numTraffic);
        _numCooldown = MkNum(10, 3600, _cfg.CooldownSec);
        Row("维修冷却（秒）", _numCooldown);
        _numMaxRepair = MkNum(1, 60, _cfg.MaxRepairPerHour);
        Row("每小时最多自动维修（次）", _numMaxRepair);

        // 唤醒后稳定期：休眠/唤醒的转换窗口里动 ICS 会抛 0x8007030F（见 PowerStateWatcher）
        _numPowerSettle = MkNum(0, 600, _cfg.PowerSettleSec);
        Row("唤醒后稳定期（秒）", _numPowerSettle);

        // ══ ④ 界面与托盘 ═══════════════════════════════════════════
        Section("界面与托盘");
        _chkTray = new CheckBox { Text = "显示托盘图标", Checked = _cfg.TrayIcon, AutoSize = true };
        Row("托盘", _chkTray);
        _chkStartMin = new CheckBox { Text = "只留托盘", Checked = _cfg.StartMinimized, AutoSize = true };
        Row("启动", _chkStartMin);
        _chkCloseTray = new CheckBox { Text = "隐藏到托盘", Checked = _cfg.CloseToTray, AutoSize = true };
        Row("关闭行为", _chkCloseTray);
        _chkLeftClick = new CheckBox { Text = "左键唤出主窗口", Checked = _cfg.LeftClickTrayOpens, AutoSize = true };
        Row("托盘交互", _chkLeftClick);

        // 启动动画 / 悬浮球：都属于"看得见的那一层"，关掉不影响守护本身
        _chkBall = new CheckBox { Text = "桌面悬浮球", Checked = _cfg.FloatingBall, AutoSize = true };
        Row("悬浮球", _chkBall);
        _chkSplash = new CheckBox { Text = "启动动画", Checked = _cfg.ShowSplash, AutoSize = true };
        Row("启动画面", _chkSplash);

        // ══ ⑤ 保存 ═════════════════════════════════════════════════
        _lblCfgPath = MkLabel(_cfg.ConfigPath, 8.5F, false, Theme.TextSecondary);
        Row("位置", _lblCfgPath);

        var bar = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(0, 8, 0, 0), Margin = new Padding(0) };
        var save = MkButton("保存设置", 130, Theme.Success);
        save.Click += (_, _) => SaveSettings();
        var openCfg = MkButton("打开配置文件", 140, Theme.Neutral);
        openCfg.Click += (_, _) => OpenPath(_cfg.ConfigPath);
        bar.Controls.AddRange(new Control[] { save, openCfg });
        Row("", bar);

        scroll.Controls.Add(p);
        page.Controls.Add(scroll);
        return page;
    }


    /// <summary>网卡下拉框（可手动输入，也可以从列表里挑）</summary>
    private static ComboBox MkAdapterCombo(string current)
    {
        var cbo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 320 };
        var names = AdapterChoices();
        foreach (var n in names) cbo.Items.Add(n);
        if (!string.IsNullOrWhiteSpace(current) && !names.Contains(current)) cbo.Items.Insert(0, current);
        cbo.Text = current;
        return cbo;
    }


    /// <summary>本机以太网类网卡（排除 VMware/虚拟/蓝牙）</summary>
    private static List<string> AdapterChoices()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var t = ni.NetworkInterfaceType;
                if (t != System.Net.NetworkInformation.NetworkInterfaceType.Ethernet &&
                    t != System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet) continue;

                var d = ni.Description ?? "";
                if (d.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                    d.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    d.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                    ni.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)) continue;

                if (!list.Contains(ni.Name)) list.Add(ni.Name);
            }
        }
        catch (Exception __ex) { AppLog.Swallow("MainForm.Settings.cs:174", __ex); }
        return list;
    }


    /// <summary>一键对调：私有端 ↔ 公网端</summary>
    /// <summary>
    /// 「对调并应用」：交换两个下拉框，然后**立刻**走确认 + 执行。
    ///
    /// 【为什么合并成一步】原来「对调」只改下拉框里的文字，不落盘、也不动系统，
    /// 必须再点一次「应用并全量重建」才会生效；按钮又并排放着，很容易以为点一下就完了
    /// （10-26 就是这么误判成"功能没效果"的）。
    /// </summary>
    private void SwapAndApplyRoles()
    {
        var a = _cmbPriv.Text;
        _cmbPriv.Text = _cmbPub.Text;
        _cmbPub.Text = a;
        AppendLog($"已对调: 私有端={_cmbPriv.Text}  公网端={_cmbPub.Text}");
        ApplyAdapterRoles();          // 直接进确认框并执行，不需要第二次点击
    }


    /// <summary>应用网卡角色并全量重建（对调后静态 IP / ICS 共享 / 度量都要跟着换边）</summary>
    private void ApplyAdapterRoles()
    {
        var priv = (_cmbPriv.Text ?? "").Trim();
        var pub = (_cmbPub.Text ?? "").Trim();

        if (priv.Length == 0 || pub.Length == 0)
        {
            MessageBox.Show(this, "两个网卡都要填。", "IcsWatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.Equals(priv, pub, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "私有端和公网端不能是同一块网卡。", "IcsWatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_engine.IsRepairing)
        {
            MessageBox.Show(this, "已有维修在进行中，稍后再试。", "IcsWatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // 游戏模式下卡1/卡2 是被主动暂停的（度量 9999、ICS 不转发），
        // 这时候对调角色等于对着"已经停摆"的链路做全量重建 —— 结果一定是稀里糊涂的。
        // 「一键维修」早就有这个拦截，对调这条路漏了。
        if (_cfg.GameModeActive)
        {
            MessageBox.Show(this,
                "游戏模式进行中：卡1 / 卡2 已被主动暂停。\n\n" +
                "请先到「游戏模式」页点「退出游戏模式」，再对调网卡角色。",
                "IcsWatch —— 网卡角色", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var oldPriv = _cfg.PrivateAdapter;
        var oldPub  = _cfg.PublicAdapter;
        var changed = priv != oldPriv || pub != oldPub;

        var msg = changed
            ? $"角色将改为：\n\n　私有端 → {priv}（锁静态 {_cfg.PrivateIp}）\n　公网端 → {pub}（切回 DHCP 拿上游地址）\n\n" +
              "随后执行全量重建，局域网与下游设备断网约 15~30 秒。\n\n确认继续？"
            : "角色没有变化，仍然执行一次全量重建？";

        if (MessageBox.Show(this, msg, "IcsWatch —— 应用网卡角色",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

        _cfg.PrivateAdapter = priv;
        _cfg.PublicAdapter = pub;
        _cfg.Save();
        AppendLog($"已保存网卡角色: 私有端={priv}  公网端={pub}");
        AppendLog("开始全量重建…");

        Task.Run(() =>
        {
            // 【关键】旧名字必须传给重建：旧私有端此刻仍是 ICS 私有端、仍占着 192.168.137.1，
            // 只按新名字撤共享的话，新私有端再写同一个地址就成了"两张网卡同 IP"（10-26 实测）。
            var rec = _engine.RunRepair(true,
                changed ? "切换网卡角色" : "应用网卡角色并重建",
                rolesChanged: changed,
                oldPrivate: changed ? oldPriv : null,
                oldPublic: changed ? oldPub : null);

            if (!changed) return;

            if (rec.Success)
            {
                Ui(() => MessageBox.Show(this,
                    "网卡角色已切换完成：\n\n" +
                    $"　私有端 → {priv}\n　公网端 → {pub}\n\n" +
                    "上下游均已按新角色重建并通过复检。",
                    "IcsWatch —— 切换完成", MessageBoxButtons.OK, MessageBoxIcon.Information));
                return;
            }

            // 失败：先把配置回滚，否则之后每 5 秒的自愈都会按"错误但已保存"的角色去修
            _cfg.PrivateAdapter = oldPriv;
            _cfg.PublicAdapter = oldPub;
            _cfg.Save();
            var bad = rec.Failed.Count > 0
                ? string.Join("、", rec.Failed)
                : (string.IsNullOrEmpty(rec.Detail) ? "未知原因" : rec.Detail);
            AppLog.Warn($"角色切换失败，配置已回滚: 私有端={oldPriv} 公网端={oldPub} · 未修好: {bad}");

            Ui(() =>
            {
                _cmbPriv.Text = oldPriv;
                _cmbPub.Text = oldPub;
                MessageBox.Show(this,
                    "网卡角色切换未完成，配置已回滚：\n\n" +
                    $"　未修好: {bad}\n\n" +
                    $"　私有端仍为 {oldPriv}，公网端仍为 {oldPub}\n\n" +
                    "网络可能处在中间状态，程序会按回滚后的角色继续自愈；\n" +
                    "需要的话可在「总览」再点一次「全量重建」。",
                    "IcsWatch —— 切换失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });
        });
    }

    // ════════════════════════════════════════════════════════════
    //  游戏模式（只保留手机 USB 共享出网）
    // ════════════════════════════════════════════════════════════


    private void SaveSettings()
    {
        _cfg.RepairMode = _cboRepair.SelectedIndex switch { 0 => "off", 2 => "full", _ => "light" };
        _cfg.ProbeIntervalSec = (int)_numProbe.Value;
        _cfg.TrafficIntervalSec = (int)_numTraffic.Value;
        _cfg.CooldownSec = (int)_numCooldown.Value;
        _cfg.MaxRepairPerHour = (int)_numMaxRepair.Value;
        _cfg.PowerSettleSec = (int)_numPowerSettle.Value;
        PowerStateWatcher.SetSettleSeconds(_cfg.PowerSettleSec);   // 立即生效，不用重启
        _cfg.TrayIcon = _chkTray.Checked;
        _cfg.StartMinimized = _chkStartMin.Checked;
        _cfg.CloseToTray = _chkCloseTray.Checked;
        _cfg.LeftClickTrayOpens = _chkLeftClick.Checked;
        _cfg.ShowSplash = _chkSplash.Checked;
        _cfg.BootRebuildEnabled = _chkBootRebuild.Checked;
        _cfg.BootRebuildDelaySec = (int)_numBootDelay.Value;
        _cfg.Save();
        if (_tray != null) { _tray.LeftClickOpens = _cfg.LeftClickTrayOpens; _tray.Show(_cfg.TrayIcon); }
        SetBallVisible(_chkBall.Checked);   // 悬浮球开关立即生效（同时同步托盘菜单的勾选）
        AppendLog("设置已保存");
        MessageBox.Show(this,
            "已保存（周期设置下次启动生效）",
            "IcsWatch", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
