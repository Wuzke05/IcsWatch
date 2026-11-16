using System.Drawing;
using System.Windows.Forms;
using IcsWatch.Core;
using IcsWatch.Monitor;

namespace IcsWatch.Ui;

public sealed partial class MainForm
{
    // ── 用量统计 ────────────────────────────────────────────────
    private Label _lblStatCap = null!;
    private Label _lblStatToday = null!, _lblStatRx = null!, _lblStatTx = null!, _lblStatTotal = null!;
    private DataGridView _gridHosts = null!;
    private readonly Dictionary<string, DataGridViewRow> _hostRows = new(StringComparer.Ordinal);
    private string _hostSig = "";
    private DateTime _lastStatRefresh = DateTime.MinValue;
    private DateTime _lastStatResort = DateTime.MinValue;
    private Button _btnStatPause = null!;

    // ── 访问记录 ────────────────────────────────────────────────
    private DataGridView _gridAccess = null!;
    private CheckBox _chkAccessOn = null!, _chkAccessSelf = null!;
    private TextBox _txtAccessFilter = null!;
    private DateTime _lastAccessRefresh = DateTime.MinValue;
    private string _accessSig = "";
    private IReadOnlyList<AccessEntry> _accessAll = Array.Empty<AccessEntry>();
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenGroups = new(StringComparer.Ordinal);

    private TabPage BuildStatsTab()
    {
        var page = new TabPage("用量统计") { BackColor = Theme.PageBg };

        // ── KPI 卡片 ─────────────────────────────────────────
        var kpi = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 96,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Theme.PageBg,
            Padding = new Padding(0, 0, 0, 0),
        };
        for (var i = 0; i < 4; i++) kpi.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        kpi.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        (_lblStatToday, _lblStatRx, _lblStatTx, _lblStatTotal) = (
            AddKpiCard(kpi, 0, "今日总量", Theme.Text),
            AddKpiCard(kpi, 1, "今日下载", Theme.ChartRx),
            AddKpiCard(kpi, 2, "今日上传", Theme.ChartTx),
            AddKpiCard(kpi, 3, "累计总量", Theme.TextSecondary));

        // ── 工具条 ───────────────────────────────────────────
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(0, 4, 0, 0),
            WrapContents = false,
        };

        _lblStatCap = MkLabel("抓包状态：准备中…", 9F, false, Theme.TextSecondary);
        // 固定宽度 + 省略号：这行字会随网卡名长短变化，AutoSize 会把它推到按钮底下
        _lblStatCap.AutoSize = false;
        _lblStatCap.Width = 360;
        _lblStatCap.Height = 26;
        _lblStatCap.TextAlign = ContentAlignment.MiddleLeft;
        _lblStatCap.AutoEllipsis = true;
        _lblStatCap.Margin = new Padding(2, 4, 14, 0);
        bar.Controls.Add(_lblStatCap);

        _btnStatPause = MkButton("暂停统计", 104, Theme.Neutral, Theme.BtnHSm);
        _btnStatPause.Margin = new Padding(0, 0, 8, 0);
        _btnStatPause.Click += (_, _) => ToggleStatCapture();
        bar.Controls.Add(_btnStatPause);

        var btnResetDay = MkButton("清零今日", 104, Theme.Neutral, Theme.BtnHSm);
        btnResetDay.Margin = new Padding(0, 0, 8, 0);
        btnResetDay.Click += (_, _) => ResetStats(todayOnly: true);
        bar.Controls.Add(btnResetDay);

        var btnResetAll = MkButton("清零全部", 104, Theme.Neutral, Theme.BtnHSm);
        btnResetAll.Margin = new Padding(0, 0, 8, 0);
        btnResetAll.Click += (_, _) => ResetStats(todayOnly: false);
        bar.Controls.Add(btnResetAll);

        // ── 设备用量表 ───────────────────────────────────────
        _gridHosts = MkGrid();
        _gridHosts.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridHosts.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ip", HeaderText = "设备 IP", FillWeight = 22,
        });
        _gridHosts.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "kind", HeaderText = "设备类型", FillWeight = 20,
        });
        _gridHosts.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "rx", HeaderText = "今日下载", FillWeight = 15,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        _gridHosts.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "tx", HeaderText = "今日上传", FillWeight = 15,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        _gridHosts.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "day", HeaderText = "今日合计", FillWeight = 15,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        _gridHosts.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "sess", HeaderText = "本次运行", FillWeight = 15,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        _gridHosts.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "last", HeaderText = "最后活跃", FillWeight = 18,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        _gridHosts.Dock = DockStyle.Fill;
        var devSec = Section("设备用量", _gridHosts);

        // ── 访问记录表 ───────────────────────────────────────
        _gridAccess = MkGrid();
        _gridAccess.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        // 设备名在分组条上，明细行不再重复一列 —— 少一列，眼睛就少跳一次
        _gridAccess.Columns.Add(new DataGridViewTextBoxColumn { Name = "host", HeaderText = "设备 / 域名", FillWeight = 30 });
        _gridAccess.Columns.Add(new DataGridViewTextBoxColumn { Name = "peer", HeaderText = "对端地址", FillWeight = 19 });
        _gridAccess.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "port", HeaderText = "端口", FillWeight = 10,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        _gridAccess.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "rx", HeaderText = "下载", FillWeight = 13,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        _gridAccess.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "tx", HeaderText = "上传", FillWeight = 13,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        _gridAccess.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "last", HeaderText = "最后活跃", FillWeight = 15,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        // 点分组条折叠/展开
        _gridAccess.CellClick += (_, e) => ToggleAccessGroup(e.RowIndex);
        _gridAccess.AllowUserToResizeRows = false;
        _gridAccess.Dock = DockStyle.Fill;

        var accBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = 34,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 6),
            BackColor = Theme.PageBg,
        };
        _chkAccessOn = MkCheck("记录访问", _cfg.AccessLogEnabled);
        _chkAccessOn.Margin = new Padding(2, 6, 16, 0);
        _chkAccessOn.CheckedChanged += (_, _) => ApplyAccessSettings();
        accBar.Controls.Add(_chkAccessOn);

        _chkAccessSelf = MkCheck("包含本机", _cfg.AccessLogIncludeSelf);
        _chkAccessSelf.Margin = new Padding(0, 6, 16, 0);
        _chkAccessSelf.CheckedChanged += (_, _) => ApplyAccessSettings();
        accBar.Controls.Add(_chkAccessSelf);

        var lblFilter = MkLabel("过滤", 9F, false, Theme.TextSecondary);
        lblFilter.AutoSize = true;
        lblFilter.Margin = new Padding(0, 9, 6, 0);
        accBar.Controls.Add(lblFilter);

        _txtAccessFilter = new TextBox
        {
            Width = 180,
            Font = Theme.MakeFont(9F),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 5, 10, 0),
        };
        _txtAccessFilter.TextChanged += (_, _) => { _accessSig = ""; _lastAccessRefresh = DateTime.MinValue; };
        accBar.Controls.Add(_txtAccessFilter);

        var btnClearAccess = MkButton("清空记录", 96, Theme.Neutral, Theme.BtnHSm);
        btnClearAccess.Margin = new Padding(0, 0, 0, 0);
        btnClearAccess.Click += (_, _) => ClearAccessLog();
        accBar.Controls.Add(btnClearAccess);

        var accSec = Section("访问记录", _gridAccess, accBar);

        // 用 3 行 TableLayoutPanel 兜住，别靠 Dock 叠罗汉 ——
        // 之前说明文字被底边切掉半行，就是 Dock.Bottom 和 Fill 抢空间的后果。
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.PageBg,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        kpi.Dock = DockStyle.Fill;
        kpi.Margin = new Padding(0, 0, 0, 10);
        bar.Dock = DockStyle.Fill;
        bar.Margin = new Padding(0, 0, 0, 8);

        // 上下分栏：上面设备用量，下面访问记录
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 8,
            BackColor = Theme.PageBg,
            Margin = new Padding(0, 0, 0, 4),
            Size = new Size(900, 480),
        };
        // 顺序有讲究：先定 SplitterDistance 再定最小尺寸。
        // 反过来写会在构造时抛 "SplitterDistance must be between Panel1MinSize and Width - Panel2MinSize"
        // —— 默认距离 50 小于 Panel1MinSize，初始化器里直接炸。
        split.SplitterDistance = 148;
        split.Panel1MinSize = 130;
        split.Panel2MinSize = 170;
        split.Panel1.BackColor = Theme.PageBg;
        split.Panel2.BackColor = Theme.PageBg;
        split.Panel1.Controls.Add(devSec);
        split.Panel2.Controls.Add(accSec);
        split.HandleCreated += (_, _) =>
        {
            try { split.SplitterDistance = 190; }
            catch (Exception ex) { AppLog.Swallow("MainForm.Stats.split", ex); }
        };

        root.Controls.Add(kpi, 0, 0);
        root.Controls.Add(bar, 0, 1);
        root.Controls.Add(split, 0, 2);
        page.Controls.Add(root);
        return page;
    }

    /// <summary>带标题（和可选工具条）的内容区</summary>
    private static Panel Section(string title, Control body, Control? bar = null)
    {
        var rows = bar is null ? 2 : 3;
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = rows,
            BackColor = Theme.PageBg,
            Margin = new Padding(0),
        };
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));

        var t = MkLabel(title, 9F, true, Theme.Text);
        t.Dock = DockStyle.Fill;
        t.TextAlign = ContentAlignment.MiddleLeft;
        host.Controls.Add(t, 0, 0);

        if (bar is not null)
        {
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, bar.Height + 2));
            host.Controls.Add(bar, 0, 1);
        }

        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        body.Dock = DockStyle.Fill;
        host.Controls.Add(body, 0, rows - 1);

        var wrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.PageBg };
        wrap.Controls.Add(host);
        return wrap;
    }

    private static CheckBox MkCheck(string text, bool value)
    {
        var cb = new CheckBox
        {
            Text = text,
            Checked = value,
            AutoSize = true,
            Font = Theme.MakeFont(9F),
            ForeColor = Theme.Text,
            BackColor = Theme.PageBg,
        };
        return cb;
    }

    private static Label AddKpiCard(TableLayoutPanel host, int col, string caption, Color valueColor)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardBg,
            Margin = new Padding(0, 0, col == 3 ? 0 : 10, 0),
            Padding = new Padding(16, 14, 16, 10),
        };
        var t = MkLabel(caption, 9F, false, Theme.TextSecondary);
        t.Dock = DockStyle.Top;
        t.Height = 20;

        var v = MkLabel("—", 17F, true, valueColor);
        v.Dock = DockStyle.Fill;
        v.TextAlign = ContentAlignment.MiddleLeft;

        card.Controls.Add(v);
        card.Controls.Add(t);
        host.Controls.Add(card, col, 0);
        return v;
    }

    // ════════════════════════════════════════════════════════════
    //  刷新
    // ════════════════════════════════════════════════════════════
    private void RefreshStats()
    {
        if ((DateTime.Now - _lastStatRefresh).TotalSeconds < 1) return;
        _lastStatRefresh = DateTime.Now;

        TrafficStatsSnapshot s;
        try { s = _engine.Traffic.Snapshot(); }
        catch (Exception ex) { AppLog.Swallow("MainForm.Stats.RefreshStats", ex); return; }

        _lblStatToday.Text = FmtBytes(s.TodayRx + s.TodayTx);
        _lblStatRx.Text = FmtBytes(s.TodayRx);
        _lblStatTx.Text = FmtBytes(s.TodayTx);
        _lblStatTotal.Text = FmtBytes(s.TotalRx + s.TotalTx);

        // 抓包状态行
        var cap = s.CaptureRunning
            ? $"{s.CaptureAdapter} → {s.UplinkAdapter} · {s.CapturedPackets:N0} 包"
            : $"未抓包：{s.CaptureError}";
        if (!string.IsNullOrEmpty(s.UplinkError)) cap += " · " + s.UplinkError;
        if (!Equals(_lblStatCap.Text, cap)) _lblStatCap.Text = cap;
        _lblStatCap.ForeColor = s.CaptureRunning ? Theme.TextSecondary : Theme.WarningText;
        _btnStatPause.Text = _engine.Traffic.CaptureEnabled ? "暂停统计" : "恢复统计";
        _btnStatPause.BackColor = _engine.Traffic.CaptureEnabled ? Theme.Neutral : Theme.Primary;

        RefreshHostGrid(s);
        RefreshAccessGrid();
    }

    private sealed record AccessGroupTag(string Device);

    /// <summary>
    /// 访问记录按设备分组：本机一条、每台下游设备各一条，点分组条折叠/展开。
    /// 之前所有记录混在一张平表里，一眼看不出"这条是谁发出的"，分完才看得清。
    /// </summary>
    private void RefreshAccessGrid()
    {
        if ((DateTime.Now - _lastAccessRefresh).TotalSeconds < 2) return;
        _lastAccessRefresh = DateTime.Now;

        IReadOnlyList<AccessEntry> all;
        try { all = _engine.Traffic.Access.Snapshot(2000); }
        catch (Exception ex) { AppLog.Swallow("MainForm.Stats.RefreshAccess", ex); return; }
        _accessAll = all;

        var filter = _txtAccessFilter.Text.Trim();
        var rows = string.IsNullOrEmpty(filter)
            ? all.ToList()
            : all.Where(e => e.Device.Contains(filter, StringComparison.OrdinalIgnoreCase)
                          || e.Domain.Contains(filter, StringComparison.OrdinalIgnoreCase)
                          || e.Peer.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        // 按"上线时间"排：最近连上来的设备排最前。
        // 一台设备可能有多条记录（多个对端），取它所有记录里最晚的 First —— 那就是它这一次上线的时刻；
        // First 记的是"抓包看到的第一次通信"，比拿最后一次活跃排序更接近"什么时候连上来的"。
        var groups = rows
            .GroupBy(e => e.Device)
            .OrderByDescending(g => g.Max(e => e.First))
            .ToList();

        var sig = string.Join("|", groups.Select(g => g.Key + "#" + g.Count()));
        if (sig != _accessSig)
        {
            _accessSig = sig;
            _gridAccess.SuspendLayout();
            _gridAccess.Rows.Clear();
            foreach (var g in groups)
            {
                var i = _gridAccess.Rows.Add("", "", "", "", "", "");
                var head = _gridAccess.Rows[i];
                head.Tag = new AccessGroupTag(g.Key);
                head.Height = 32;
                head.DefaultCellStyle.BackColor = Theme.GroupBg;
                head.DefaultCellStyle.SelectionBackColor = Theme.GroupBg;
                head.DefaultCellStyle.Font = Theme.MakeFont(9F, true);
                foreach (var e in g.OrderByDescending(x => x.Last))
                {
                    var child = _gridAccess.Rows[_gridAccess.Rows.Add("", "", "", "", "", "")];
                    child.Tag = e;
                }
            }
            _gridAccess.ResumeLayout();
            // 重建后滚动位置会停在旧偏移上（看到的是半截列表，分组条被顶到看不见），归零
            try { if (_gridAccess.Rows.Count > 0) _gridAccess.FirstDisplayedScrollingRowIndex = 0; }
            catch (Exception ex) { AppLog.Swallow("MainForm.Stats.accessScroll", ex); }
        }

        // 第一次见到的"本机"默认收起：它条目最多，展开会把下游设备顶到看不见
        foreach (var g in groups)
            if (_seenGroups.Add(g.Key) && g.Key.StartsWith("本机", StringComparison.Ordinal))
                _collapsed.Add(g.Key);

        _gridAccess.CurrentCell = null;   // 隐藏"当前行"会抛异常，先摘掉
        var r = 0;
        foreach (var g in groups)
        {
            var collapsed = _collapsed.Contains(g.Key);
            var head = _gridAccess.Rows[r++];
            Set(head, "host", (collapsed ? "▸  " : "▾  ") + g.Key, Theme.TextTitle);
            Set(head, "peer", $"{g.Count()} 条 · {ClockText(g.Max(e => e.First))} 连接", Theme.TextSecondary);
            Set(head, "port", "", Theme.TextMuted);
            Set(head, "rx", FmtBytes(g.Sum(e => e.Rx)), Theme.ChartRx);
            Set(head, "tx", FmtBytes(g.Sum(e => e.Tx)), Theme.ChartTx);
            Set(head, "last", AgoText(g.Max(e => e.Last)), Theme.TextMuted);
            head.Visible = true;

            foreach (var e in g.OrderByDescending(x => x.Last))
            {
                if (r >= _gridAccess.Rows.Count) break;
                var row = _gridAccess.Rows[r++];
                row.Tag = e;
                Set(row, "host", string.IsNullOrEmpty(e.Domain) ? "（未知域名）" : e.Domain,
                    string.IsNullOrEmpty(e.Domain) ? Theme.TextMuted : Theme.Primary);
                Set(row, "peer", e.Peer, Theme.TextSecondary);
                Set(row, "port", $"{e.Port} {e.Proto}", Theme.TextMuted);
                Set(row, "rx", FmtBytes(e.Rx), Theme.ChartRx);
                Set(row, "tx", FmtBytes(e.Tx), Theme.ChartTx);
                Set(row, "last", AgoText(e.Last), Theme.TextMuted);
                row.Visible = !collapsed;
            }
        }
    }

    private void ToggleAccessGroup(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _gridAccess.Rows.Count) return;
        if (_gridAccess.Rows[rowIndex].Tag is not AccessGroupTag g) return;

        if (!_collapsed.Remove(g.Device)) _collapsed.Add(g.Device);
        // 不能在 CellClick 里直接重建行 —— 事件还在 DataGridView 内部展开，此时 Rows.Clear 属于重入
        _gridAccess.BeginInvoke(new Action(() =>
        {
            _lastAccessRefresh = DateTime.MinValue;
            _accessSig = "";
            RefreshAccessGrid();
        }));
    }

    private void ApplyAccessSettings()
    {
        _cfg.AccessLogEnabled = _chkAccessOn.Checked;
        _cfg.AccessLogIncludeSelf = _chkAccessSelf.Checked;
        try { _cfg.Save(); }
        catch (Exception ex) { AppLog.Swallow("MainForm.Stats.ApplyAccessSettings", ex); }

        _engine.Traffic.ApplyAccessLogSettings();
        _chkAccessSelf.Enabled = _chkAccessOn.Checked;
        _accessSig = "";
        _lastAccessRefresh = DateTime.MinValue;
        AppendLog($"访问记录：{(_chkAccessOn.Checked ? "已开启" : "已关闭")}"
                  + (_cfg.AccessLogIncludeSelf ? "，含本机" : "，只看下游设备"));
        RefreshStats();
    }

    private void ClearAccessLog()
    {
        if (MessageBox.Show(this, "确定要清空访问记录吗？", "访问记录",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _engine.Traffic.Access.Clear();
        _accessSig = "";
        _lastAccessRefresh = DateTime.MinValue;
        RefreshStats();
        AppendLog("访问记录：已清空");
    }

    private void RefreshHostGrid(TrafficStatsSnapshot s)
    {
        var sig = string.Join("|", s.Hosts.Select(h => h.Ip));
        var reSort = (DateTime.Now - _lastStatResort).TotalSeconds >= 30;

        if (sig != _hostSig || reSort)
        {
            _hostSig = sig;
            _lastStatResort = DateTime.Now;
            _hostRows.Clear();
            _gridHosts.Rows.Clear();
            foreach (var h in s.Hosts)
            {
                var i = _gridHosts.Rows.Add(h.Ip, KindText(h), "", "", "", "", "");
                var row = _gridHosts.Rows[i];
                row.Tag = h.Ip;
                _hostRows[h.Ip] = row;
            }
        }

        foreach (var h in s.Hosts)
        {
            if (!_hostRows.TryGetValue(h.Ip, out var row)) continue;
            Set(row, "kind", KindText(h), Theme.TextSecondary);
            Set(row, "rx", FmtBytes(h.TodayRx), Theme.ChartRx);
            Set(row, "tx", FmtBytes(h.TodayTx), Theme.ChartTx);
            Set(row, "day", FmtBytes(h.TodayTotal), Theme.Text);
            Set(row, "sess", FmtBytes(h.SessionTotal), Theme.TextSecondary);
            Set(row, "last", AgoText(h.LastSeen), Theme.TextMuted);
        }
    }

    private static void Set(DataGridViewRow row, string col, string text, Color color)
    {
        var c = row.Cells[col];
        if (!Equals(c.Value?.ToString(), text)) c.Value = text;
        if (c.Style.ForeColor != color) c.Style.ForeColor = color;
    }

    private static string KindText(HostUsage h)
    {
        if (!string.IsNullOrEmpty(h.Label)) return h.Label;
        return string.IsNullOrEmpty(h.Mac) ? "未知" : h.Mac;
    }

    private static string AgoText(DateTime t)
    {
        if (t == DateTime.MinValue) return "—";
        var sec = (DateTime.Now - t).TotalSeconds;
        if (sec < 60) return "刚刚";
        if (sec < 3600) return $"{(int)(sec / 60)} 分钟前";
        if (sec < 86400) return $"{(int)(sec / 3600)} 小时前";
        return t.ToString("MM-dd HH:mm");
    }

    /// <summary>时刻 → 简写（今天只给 HH:mm，跨天带上日期）</summary>
    private static string ClockText(DateTime t)
    {
        if (t == DateTime.MinValue) return "—";
        return t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("MM-dd HH:mm");
    }

    /// <summary>字节数 → 人读的字符串（按 1024 进位，与资源管理器一致）</summary>
    internal static string FmtBytes(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        foreach (var u in units)
        {
            v /= 1024;
            if (v < 1024 || u == "TB") return v >= 100 ? $"{v:0} {u}" : $"{v:0.0} {u}";
        }
        return $"{v:0.0} TB";
    }

    // ════════════════════════════════════════════════════════════
    //  动作
    // ════════════════════════════════════════════════════════════
    private void ToggleStatCapture()
    {
        var on = !_engine.Traffic.CaptureEnabled;
        _engine.Traffic.SetCaptureEnabled(on);
        AppendLog(on ? "用量统计：已恢复按设备抓包" : "用量统计：已暂停按设备抓包（总量继续统计）");
        _lastStatRefresh = DateTime.MinValue;
        RefreshStats();
    }

    private void ResetStats(bool todayOnly)
    {
        var what = todayOnly ? "今日用量" : "全部用量记录（含累计）";
        if (MessageBox.Show(this, $"确定要清零{what}吗？", "用量统计",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        if (todayOnly) _engine.Traffic.ResetToday();
        else _engine.Traffic.ResetAll();

        // 曲线同步归零，否则清了"用量"却还留着刚才那几条线
        if (!todayOnly) _engine.Devices.Reset();

        _hostRows.Clear();
        _hostSig = "";
        _lastStatRefresh = DateTime.MinValue;
        RefreshStats();
        AppendLog("用量统计：已清零" + what);
    }
}
