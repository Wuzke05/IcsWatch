using System.Drawing;
using System.Windows.Forms;
using IcsWatch.Config;
using IcsWatch.Core;
using IcsWatch.Monitor;

namespace IcsWatch.Ui;

public sealed partial class MainForm
{
    private TabPage BuildAutomationTab()
    {
        var page = new TabPage("自动化") { BackColor = Theme.PageBg };

        _gridTasks = MkGrid();
        _gridTasks.Columns.Add("name", "计划任务");
        _gridTasks.Columns.Add("state", "状态");
        _gridTasks.Columns.Add("last", "上次运行");
        _gridTasks.Columns.Add("result", "上次结果");
        _gridTasks.Columns.Add("trig", "触发器");
        _gridTasks.Height = 88;
        _gridTasks.Dock = DockStyle.Top;

        _gridScripts = MkGrid();
        _gridScripts.Columns.Add("file", "内嵌脚本");
        _gridScripts.Columns.Add("hash", "一致性");
        _gridScripts.Columns.Add("path", "磁盘路径");
        _gridScripts.Height = 88;
        _gridScripts.Dock = DockStyle.Top;

        _txtLog = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            BackColor = Theme.LogBg,
            ForeColor = Theme.LogFg,
            WordWrap = false,
        };

        var caption = MkLabel("运行日志", 10F, true);
        caption.Dock = DockStyle.Top;
        caption.Height = 26;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6, 4, 6, 0) };
        var btnOpenLog = MkButton("打开日志文件夹", 140, Theme.Neutral);
        btnOpenLog.Click += (_, _) => OpenPath(AppLog.CurrentFile);
        // 旧的 ICS Boot Fix / Net Fix Watch 已停用（职责由本程序接管），
        // 所以这两个按钮改成直接调本程序自己的维修，避免点了没反应
        var btnRunBoot = MkButton("♻  全量重建", 150, Theme.ActionWarn);
        btnRunBoot.Click += (_, _) => RepairNow(true);
        var btnRunWatch = MkButton("🔧  轻量维修", 150, Theme.Primary);
        btnRunWatch.Click += (_, _) => RepairNow(false);
        var btnLegacy = MkButton("应急回退", 150, Theme.Neutral);
        btnLegacy.Click += (_, _) => RepairLegacy();
        bar.Controls.AddRange(new Control[] { btnOpenLog, btnRunBoot, btnRunWatch, btnLegacy });

        page.Controls.Add(_txtLog);
        page.Controls.Add(bar);
        page.Controls.Add(caption);
        page.Controls.Add(_gridScripts);
        page.Controls.Add(_gridTasks);
        return page;
    }


    private void RefreshTasks()
    {
        _gridTasks.Rows.Clear();
        foreach (var name in new[] { _cfg.BootFixTaskName, _cfg.WatchTaskName })
        {
            var t = TaskWatcher.Get(name);
            _gridTasks.Rows.Add(name, t.Found ? t.State : "未部署",
                t.LastRun?.ToString("MM-dd HH:mm:ss") ?? "—",
                t.ResultText, t.TriggerText);
        }
    }


    private void RefreshScripts()
    {
        _gridScripts.Rows.Clear();
        foreach (var s in EmbeddedScripts.Describe(_cfg.ScriptPath))
            if (!s.Name.Contains("healthcheck"))
                _gridScripts.Rows.Add(s.Name, s.StatusText, s.DiskPath);
    }


    private void RepairLegacy()
    {
        if (_engine.IsRepairing) { MessageBox.Show(this, "已有维修在进行中。", "IcsWatch"); return; }
        var r = MessageBox.Show(this,
            "将调用内嵌脚本 fix-ics-final.ps1。确认继续？",
            "IcsWatch", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (r != DialogResult.OK) return;
        AppendLog("开始应急回退…");
        Task.Run(() =>
        {
            var rec = Repairer.RunLegacy(_cfg, "手动应急回退", m => Ui(() => AppendLog("  " + m)));
            Ui(() =>
            {
                RefreshRepairs();
                AppendLog("应急回退完成: " + rec.Summary);
                _engine.ProbeTick("应急回退后复检");
            });
        });
    }


    private void RunTask(string name)
    {
        if (TaskWatcher.Run(name)) AppendLog($"已触发计划任务: {name}");
        else AppendLog($"触发计划任务失败: {name}");
        Task.Delay(1500).ContinueWith(_ => Ui(RefreshTasks));
    }
}
