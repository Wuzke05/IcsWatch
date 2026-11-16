using System.Drawing;
using System.Windows.Forms;
using IcsWatch.Config;
using IcsWatch.Core;
using IcsWatch.Monitor;

namespace IcsWatch.Ui;

public sealed partial class MainForm
{
    /// <summary>
    /// 总览页 —— 竖向卡片流，互不重叠：
    ///   ① 状态卡（状态圆 + 大字 + 统计徽章）
    ///   ② 关键参数卡（6 行，行距拉宽）
    ///   ③ 操作按钮条（按钮加大）
    ///   ④ 后台运行状态行
    ///   ⑤ 维修记录卡（吃掉剩余高度，不留大片空白）
    /// 之前版本的问题：状态圆和参数表并排但列宽不够，大字溢出压到参数表上；
    /// 参数表 7 行塞进 154px 被裁掉最后一行；按钮条占满剩余空间却只有 34px 高。已全部改掉。
    /// </summary>
    private TabPage BuildOverviewTab()
    {
        var page = new TabPage("总览") { BackColor = PageBg, Padding = new Padding(18, 16, 18, 16) };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = PageBg,
            Margin = new Padding(0),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));   // ① 状态卡
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 252));   // ② 关键参数卡
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));    // ③ 操作按钮
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));    // ④ 运行状态行
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // ⑤ 维修记录（填满）

        _light = new StatusLight { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14) };
        root.Controls.Add(_light, 0, 0);

        root.Controls.Add(BuildKvCard(), 0, 1);
        root.Controls.Add(BuildActionBar(), 0, 2);

        _lblEngine = MkLabel("", 9F, false, Theme.TextSecondary);
        _lblEngine.AutoSize = false;
        _lblEngine.Dock = DockStyle.Fill;
        _lblEngine.TextAlign = ContentAlignment.MiddleLeft;
        _lblEngine.Margin = new Padding(6, 0, 0, 6);
        root.Controls.Add(_lblEngine, 0, 3);

        root.Controls.Add(BuildRepairCard(), 0, 4);

        page.Controls.Add(root);
        return page;
    }


    /// <summary>关键参数卡：白底 + 标题 + 6 行参数，行距拉宽不再挤</summary>
    private Control BuildKvCard()
    {
        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = CardBg,
            Margin = new Padding(0, 0, 0, 14),
            Padding = new Padding(24, 14, 24, 16),
        };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var cap = MkLabel("关键参数", 10.5F, true);
        cap.AutoSize = false;
        cap.Dock = DockStyle.Fill;
        cap.TextAlign = ContentAlignment.MiddleLeft;
        cap.Margin = new Padding(0);
        card.Controls.Add(cap, 0, 0);

        var kv = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = KvRows.Length,
            Margin = new Padding(0),
        };
        kv.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 134));
        kv.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        kv.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        for (int i = 0; i < KvRows.Length; i++)
        {
            kv.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / KvRows.Length));
            AddKvRow(kv, i, KvRows[i].key, KvRows[i].caption);
        }
        card.Controls.Add(kv, 0, 1);
        return card;
    }


    private void AddKvRow(TableLayoutPanel p, int row, string key, string caption)
    {
        var c = MkLabel(caption, 9.5F, false, Theme.TextSecondary);
        c.AutoSize = false; c.Dock = DockStyle.Fill; c.Margin = new Padding(0);
        c.TextAlign = ContentAlignment.MiddleLeft;

        var v1 = MkLabel("—", 10F, true);
        v1.AutoSize = false; v1.Dock = DockStyle.Fill; v1.Margin = new Padding(0, 0, 10, 0);
        v1.TextAlign = ContentAlignment.MiddleLeft;
        v1.AutoEllipsis = true;                       // 过长自动 …，绝不压到下一列

        var v2 = MkLabel("", 9F, false, Theme.TextSecondary);
        v2.AutoSize = false; v2.Dock = DockStyle.Fill; v2.Margin = new Padding(0);
        v2.TextAlign = ContentAlignment.MiddleLeft;
        v2.AutoEllipsis = true;

        p.Controls.Add(c, 0, row);
        p.Controls.Add(v1, 1, row);
        p.Controls.Add(v2, 2, row);
        _kv[key] = v1;
        _kv[key + "_sub"] = v2;
    }


    /// <summary>操作按钮条：按钮加大到 46px 高、间距拉开、垂直居中</summary>
    private Control BuildActionBar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = PageBg,
            Padding = new Padding(0, 10, 0, 14),
            Margin = new Padding(0),
        };
        _btnCheck = MkButton("🔍  立即体检", 172, Theme.Primary);
        _btnCheck.Click += (_, _) => CheckNow();
        _btnRepair = MkButton("🔧  一键维修", 172, Theme.Danger);
        _btnRepair.Click += (_, _) => RepairNow(false);
        _btnFull = MkButton("♻  全量重建", 172, Theme.ActionWarn);
        _btnFull.Click += (_, _) => RepairNow(true);
        _btnHide = MkButton("📥  挂后台", 172, Theme.Neutral);
        _btnHide.Click += (_, _) => HideToTray();
        bar.Controls.AddRange(new Control[] { _btnCheck, _btnRepair, _btnFull, _btnHide });
        return bar;
    }


    /// <summary>维修记录卡：填满剩余高度，自动维修留痕可回查</summary>
    private Control BuildRepairCard()
    {
        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = CardBg,
            Margin = new Padding(0),
            Padding = new Padding(24, 14, 24, 14),
        };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var head = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var cap = MkLabel("维修记录", 10.5F, true);
        cap.AutoSize = false; cap.Dock = DockStyle.Fill; cap.Margin = new Padding(0);
        cap.TextAlign = ContentAlignment.MiddleLeft;
        head.Controls.Add(cap, 0, 0);

        card.Controls.Add(head, 0, 0);

        _gridRepairs = MkGrid();
        _gridRepairs.Columns.Add("t", "时间");
        _gridRepairs.Columns.Add("mode", "方式");
        _gridRepairs.Columns.Add("reason", "触发原因");
        _gridRepairs.Columns.Add("res", "结果");
        _gridRepairs.Columns.Add("sec", "耗时");
        _gridRepairs.Columns["t"]!.FillWeight = 95;
        _gridRepairs.Columns["mode"]!.FillWeight = 62;
        _gridRepairs.Columns["reason"]!.FillWeight = 230;
        _gridRepairs.Columns["res"]!.FillWeight = 58;
        _gridRepairs.Columns["sec"]!.FillWeight = 55;
        _gridRepairs.Dock = DockStyle.Fill;
        _gridRepairs.Margin = new Padding(0);
        _gridRepairs.RowTemplate.Height = 28;
        card.Controls.Add(_gridRepairs, 0, 1);
        return card;
    }


    private void ApplySnapshot(CheckSnapshot snap)
    {
        // 游戏模式轻量化：界面刷新降频（700ms → 可配置，默认 2000ms）
        var wantTick = (_cfg.GameModeLite && _cfg.GameModeActive)
            ? Math.Clamp(_cfg.GameUiTickMs, 500, 10000) : 700;
        if ((int)_uiTimer.Interval != wantTick) _uiTimer.Interval = wantTick;

        // 标记"这份快照已经渲染过"，避免 TickUi（700ms）又拿同一份快照重写一遍参数卡
        _lastSnap = snap;

        // 详情行：只在确实有问题时列出问题项（最多 3 条）。全好时不写文案。
        var problems = snap.Items
            .Where(i => i.Status == CheckStatus.Fail || i.Status == CheckStatus.Warn)
            .Select(i => $"{i.Name} {i.Detail}")
            .Take(3).ToList();
        var detail = problems.Count == 0 ? "" : string.Join("　·　", problems);

        _light.Set(snap.State, Checker.StateText(snap.State), detail,
            snap.Count(CheckStatus.Pass), snap.Count(CheckStatus.Warn), snap.Count(CheckStatus.Fail));

        // 检查项列表
        _gridChecks.Rows.Clear();
        foreach (var it in snap.Items)
        {
            var idx = _gridChecks.Rows.Add(
                it.Status switch
                {
                    CheckStatus.Pass => "PASS",
                    CheckStatus.Warn => "WARN",
                    CheckStatus.Fail => "FAIL",
                    _ => "INFO",
                },
                it.Group, it.Name, it.Detail, it.Hint);
            var row = _gridChecks.Rows[idx];
            row.DefaultCellStyle.ForeColor = Theme.ForStatus(it.Status);
        }
        _lblCheckSum.Text = $"{Checker.StateText(snap.State)}    " +
                            $"通过 {snap.Count(CheckStatus.Pass)} · 警告 {snap.Count(CheckStatus.Warn)} · " +
                            $"故障 {snap.Count(CheckStatus.Fail)}    " +
                            $"{snap.Time:HH:mm:ss}{(snap.FromEvent ? " 事件触发" : "")}";

        // 参数卡：游戏模式下**只由 SyncGameModeUi 写一次**。
        // 原来这里是 ApplyKv(snap) 先写"实时值"（很长、颜色还是红的），紧接着
        // SyncGameModeUi 又覆盖成"游戏模式 · 已暂停" —— 中间只要发生一次重绘
        // （网格刷新、日志追加都会），就会看到 ICS 共享/ICS 服务/NAT 出口 后面的字闪跳。
        if (_cfg.GameModeActive) SyncGameModeUi();
        else ApplyKv(snap);

        SyncGameModeUi();   // 流量页标题 / 维修按钮状态（两种模式都要跑，幂等）
    }


    private void ApplyKv(CheckSnapshot snap)
    {
        // 【按 Key 取值，不按项名】原来这里写的是 Find(i => i.Name == "卡1 主地址")：
        // 只要 Checker 里改了项名，界面就会静默变成一片"—"，而编译和运行都不报错。
        string D(string key) => snap.Find(key)?.Detail ?? "—";
        CheckStatus st(string key) => snap.Find(key)?.Status ?? CheckStatus.Info;
        CheckStatus both(string a, string b) =>
            st(a) == CheckStatus.Pass && st(b) == CheckStatus.Pass ? CheckStatus.Pass : CheckStatus.Fail;

        // 卡1 类别详情带一长串解释，这里只取括号前的主值，避免把行撑爆
        var cat = D(CheckKeys.PrivateCategory).Split('（')[0].Trim();
        // ICS 共享只留"启用没有"，En=True Type=0 里的 Type 对内网/公网判断没意义
        string En(string key) => D(key).Split(' ', '　')[0];

        SetKv("k1", D(CheckKeys.PrivateIp),
            $"{D(CheckKeys.PrivateDhcp)} · 度量 {D(CheckKeys.PrivateMetric)} · 类别 {cat}", st(CheckKeys.PrivateIp));
        SetKv("k2", D(CheckKeys.PublicIp),
            $"默认路由 {D(CheckKeys.PublicRoute)} · 度量 {D(CheckKeys.PublicMetric)}", st(CheckKeys.PublicIp));
        SetKv("ics",
            $"卡2 {En(CheckKeys.IcsPublic)} · 卡1 {En(CheckKeys.IcsPrivate)}",
            "", both(CheckKeys.IcsPrivate, CheckKeys.IcsPublic));
        SetKv("svc", $"DHCP 监听 {D(CheckKeys.IcsDhcpListen)}",
            $"SharedAccess {D(CheckKeys.SharedAccess)} · NlaSvc {D(CheckKeys.NlaSvc)} · ipnat {D(CheckKeys.IpNat)}",
            both(CheckKeys.IcsDhcpListen, CheckKeys.SharedAccess));
        SetKv("nat", D(CheckKeys.NatEgress), "", st(CheckKeys.NatEgress));
        SetKv("dev", D(CheckKeys.Downstream), "", st(CheckKeys.Downstream));
    }


    private void SetKv(string key, string value, string sub, CheckStatus s)
    {
        if (_kv.TryGetValue(key, out var l))
        {
            l.Text = value;
            l.ForeColor = s switch
            {
                CheckStatus.Pass => Theme.SuccessText,
                CheckStatus.Warn => Theme.WarningText,
                CheckStatus.Fail => Theme.DangerText,
                _ => Theme.Text,
            };
        }
        if (_kv.TryGetValue(key + "_sub", out var l2)) l2.Text = sub;
    }


    private void ApplyState(HealthState st, string msg)
    {
        var snap = _engine.Latest;
        // 详情行只在异常时显示原因；正常状态不写解释文案。
        var detail = st == HealthState.Healthy ? "" : msg;
        _light.Set(st, Checker.StateText(st), detail,
            snap?.Count(CheckStatus.Pass) ?? 0,
            snap?.Count(CheckStatus.Warn) ?? 0,
            snap?.Count(CheckStatus.Fail) ?? 0);
        _tray?.SetState(st, BuildTrayTooltip(st));

        if (st == HealthState.Broken)
        {
            _tray?.Balloon("IcsWatch 检测到故障", msg + (msg.Contains("宽容期") ? "" : " · 将按设置尝试自动维修"));
        }
        else if (st == HealthState.Healthy)
        {
            _tray?.Balloon("IcsWatch 已恢复正常", "全部检查项通过。", ToolTipIcon.Info);
        }
        AppendLog($"[状态] {Checker.StateText(st)}: {msg}");
    }
}
