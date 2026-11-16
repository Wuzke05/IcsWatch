using System.Drawing;
using System.Windows.Forms;
using IcsWatch.Config;
using IcsWatch.Core;
using IcsWatch.Monitor;

namespace IcsWatch.Ui;

public sealed partial class MainForm
{
    private TabPage BuildChecksTab()
    {
        var page = new TabPage("检查项") { BackColor = Theme.PageBg };
        _lblCheckSum = MkLabel("尚未体检", 10F, true);
        _lblCheckSum.Dock = DockStyle.Top;
        _lblCheckSum.Height = 30;
        _lblCheckSum.Padding = new Padding(8, 6, 0, 0);

        _gridChecks = MkGrid();
        _gridChecks.Columns.Add("st", "状态");
        _gridChecks.Columns.Add("grp", "分组");
        _gridChecks.Columns.Add("name", "检查项");
        _gridChecks.Columns.Add("detail", "详情");
        _gridChecks.Columns.Add("hint", "修法/提示");
        _gridChecks.Columns["detail"]!.FillWeight = 200;
        _gridChecks.Columns["hint"]!.FillWeight = 160;
        _gridChecks.Dock = DockStyle.Fill;

        page.Controls.Add(_gridChecks);
        page.Controls.Add(_lblCheckSum);
        return page;
    }
}
