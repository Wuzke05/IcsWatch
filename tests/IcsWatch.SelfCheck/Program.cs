using System.Reflection;
using System.Runtime.InteropServices;
using IcsWatch.Config;
using IcsWatch.Core;

namespace IcsWatch.SelfCheck;

/// <summary>
/// 契约自检 —— 钉住"体检项 ↔ 维修计划 ↔ 界面取值"之间那份曾经是隐式的契约。
///
/// 这个程序不测具体网络行为（那要真机真网），只测**结构**：
///   · 每个检查项都带稳定的 Key，且能用 Key 找回来；
///   · 维修计划里声明的每个 Key，在当前拓扑下确实有对应的体检项；
///   · 电源态错误码的识别逻辑（0x8007030F，含被反射包装一层的情况）。
///
/// 退出码 0 = 全通过；1 = 有失败项。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    [STAThread]
    private static int Main()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        Console.WriteLine("IcsWatch 契约自检");
        Console.WriteLine(new string('=', 60));

        Section("① 检查项 Key 契约");
        CheckItemKeyWorks();
        CheckConfigDefaults();

        Section("② 电源状态错误识别（0x8007030F）");
        CheckPowerErrorDetection();

        Section("③ 真实体检（只读，不修改系统）");
        CheckRealSnapshot();

        Section("④ 下游设备 MAC 判定");
        CheckDeviceMac();

        Section("⑤ 计划任务解析与触发器文案");
        CheckTasks();

        Section("⑥ 角色对调：DHCP 切换判定");
        CheckDhcpConversion();

        Section("⑦ DHCP 报文记账（谁在要地址）");
        CheckDhcpWatch();

        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"通过 {_passed} · 失败 {Failures.Count}");
        if (Failures.Count > 0)
        {
            Console.WriteLine("失败项:");
            foreach (var f in Failures) Console.WriteLine("  ✗ " + f);
        }
        return Failures.Count == 0 ? 0 : 1;
    }

    // ── ① Key 契约 ─────────────────────────────────────────────
    private static void CheckItemKeyWorks()
    {
        var item = CheckItem.Pass(CheckKeys.PrivateIp, "分组", "某中文名", "详情");
        Check("CheckItem 保留 Key", item.Key == CheckKeys.PrivateIp, item.Key);

        var snap = new CheckSnapshot();
        snap.Items.Add(item);
        Check("Find 能按 Key 命中", ReferenceEquals(snap.Find(CheckKeys.PrivateIp), item));
        Check("Find 对未知 Key 返回 null", snap.Find("no.such.key") == null);

        // 改中文名不影响查找 —— 这正是本契约要保证的
        var renamed = CheckItem.Warn(CheckKeys.PrivateIp, "分组", "换了个名字的项", "详情");
        var snap2 = new CheckSnapshot();
        snap2.Items.Add(renamed);
        Check("项名改了仍能按 Key 找到", snap2.Find(CheckKeys.PrivateIp) != null, renamed.Name);
    }

    private static void CheckConfigDefaults()
    {
        var cfg = new AppConfig();
        Check("默认 PowerSettleSec > 0", cfg.PowerSettleSec > 0, cfg.PowerSettleSec.ToString());
        Check("默认卡1/卡2 非空", !string.IsNullOrWhiteSpace(cfg.PrivateAdapter) &&
                                 !string.IsNullOrWhiteSpace(cfg.PublicAdapter));
    }

    // ── ② 电源态错误 ───────────────────────────────────────────
    private static void CheckPowerErrorDetection()
    {
        const int hr = unchecked((int)0x8007030F);

        Check("裸 COMException 可识别",
            PowerStateWatcher.IsPowerStateError(new COMException("电源状态", hr)));
        Check("被 TargetInvocationException 包装后仍可识别",
            PowerStateWatcher.IsPowerStateError(new TargetInvocationException(new COMException("电源状态", hr))));
        Check("两层包装仍可识别",
            PowerStateWatcher.IsPowerStateError(
                new TargetInvocationException(new TargetInvocationException(new COMException("x", hr)))));
        Check("普通异常不误报",
            !PowerStateWatcher.IsPowerStateError(new InvalidOperationException("别的错")));
        Check("未订阅时 WaitStable 立即返回（CLI 场景）",
            !PowerStateWatcher.Attached && PowerStateWatcher.WaitStable(200));
        Check("未订阅时不在稳定期", !PowerStateWatcher.InSettleWindow);
    }

    // ── ③ 真实体检 ─────────────────────────────────────────────
    private static void CheckRealSnapshot()
    {
        CheckSnapshot snap;
        try { snap = Checker.Run(new AppConfig()); }
        catch (Exception ex)
        {
            Check("Checker.Run 不抛异常", false, AppLog.Ex(ex));
            return;
        }

        Console.WriteLine($"  体检产出 {snap.Items.Count} 项，状态 {Checker.StateText(snap.State)}");
        Check("体检有产出", snap.Items.Count > 0);

        var emptyKey = snap.Items.Where(i => string.IsNullOrWhiteSpace(i.Key)).ToList();
        Check("每项都有 Key", emptyKey.Count == 0,
            emptyKey.Count == 0 ? null : string.Join("、", emptyKey.Select(i => i.Name)));

        var dupes = snap.Items.GroupBy(i => i.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Check("Key 无重复", dupes.Count == 0, string.Join("、", dupes));

        var unfindable = snap.Items.Where(i => snap.Find(i.Key) == null).ToList();
        Check("每项都能被 Find 找回", unfindable.Count == 0);

        // ── ICS COM 在本机能不能读 ───────────────────────────────────
        // 读不到时（无 ICS 组件 / 权限不足 / 非交互会话 / CI 容器），
        // ics.private 与 ics.public 这两项根本没机会产出 —— 那不是契约破了，
        // 而是这台机器没法取证。**不静默放过**：打印一行"跳过"让人看得见，
        // 但也不把它算成失败，否则在 GitHub Actions 这种环境里 CI 永远是红的。
        var icsUnavailable = snap.Find(CheckKeys.IcsAvailable) != null;
        if (icsUnavailable)
            Console.WriteLine("  ! 本机读不到 ICS 共享状态（COM 不可用），跳过 ics.private / ics.public 的齐全性断言");

        // 这些项与链路状态无关，任何拓扑下都必须产出 —— 界面取值全靠它们
        string[] core =
        {
            CheckKeys.PrivateLink, CheckKeys.PublicLink,
            CheckKeys.PrivateIp, CheckKeys.PrivateAltIp, CheckKeys.PublicIp,
            CheckKeys.PrivateRoute, CheckKeys.PublicRoute,
            CheckKeys.PrivateDhcp, CheckKeys.PrivateMetric, CheckKeys.PublicMetric, CheckKeys.DhcpRegistry,
            CheckKeys.PrivateCategory, CheckKeys.SharedAccess, CheckKeys.NlaSvc, CheckKeys.IpNat,
            CheckKeys.IcsPrivate, CheckKeys.IcsPublic, CheckKeys.IcsDhcpListen, CheckKeys.Downstream,
        };
        var missingCore = core
            .Where(k => snap.Find(k) == null)
            .Where(k => !(icsUnavailable && (k == CheckKeys.IcsPrivate || k == CheckKeys.IcsPublic)))
            .ToList();
        Check($"核心项齐全（{core.Length} 项）", missingCore.Count == 0, string.Join("、", missingCore));

        // 维修计划声明的 Key 必须能落到真实体检项上（漏了 = 复检永远"找不到该项"）
        var planned = Repairer.PlannedKeys.ToList();
        Check("维修计划非空", planned.Count > 0, $"共 {planned.Count} 个 Key");
        // NAT 出口是条件项：卡1 没链路 / 上游没网关时体检不产出，属正常
        // ICS 两端同理：COM 读不到时这两项本来就产不出来
        var missingPlanned = planned
            .Where(k => k != CheckKeys.NatEgress && snap.Find(k) == null)
            .Where(k => !(icsUnavailable && (k == CheckKeys.IcsPrivate || k == CheckKeys.IcsPublic)))
            .ToList();
        Check("维修计划里的 Key 都有对应体检项", missingPlanned.Count == 0,
            string.Join("、", missingPlanned));

        Console.WriteLine("  计划覆盖: " + string.Join(", ", planned));
    }

    // ── ④ 设备 MAC 判定 ───────────────────────────────────────
    private static void CheckDeviceMac()
    {
        // 8A-1E-… 首字节 bit1 = 1 → 本地管理地址（手机随机化 MAC）
        var phone = new DownstreamDevice { Ip = "192.168.137.189", Mac = "AA-BB-CC-DD-EE-01" };
        Check("随机 MAC 能被识别", phone.IsRandomMac, phone.Label);
        Check("随机 MAC 有标签", phone.Label.Length > 0, phone.Label);

        // 3C-84-… 首字节 bit1 = 0 → 厂商分配的正常 MAC
        var normal = new DownstreamDevice { Mac = "3C-84-6A-11-22-33" };
        Check("普通 MAC 不误判", !normal.IsRandomMac);

        var empty = new DownstreamDevice { Mac = "" };
        Check("空 MAC 不崩", !empty.IsRandomMac);
    }

    // ── ⑤ 计划任务 ────────────────────────────────────────────
    private static void CheckTasks()
    {
        var t = TaskWatcher.Get("IcsWatch");
        if (!t.Found)
        {
            Console.WriteLine("  · 跳过：本机没有名为 IcsWatch 的计划任务");
        }
        else
        {
            Check("IcsWatch 任务存在", true, $"State={t.State} LastResult={t.LastResult}");
            // 触发器文案映射修好后，登录触发应显示"登录"（原来错显示成"空闲"）
            Check("触发器识别为「登录」", t.TriggerText.Contains("登录"), t.TriggerText);
        }

        // 配置名写错时不能抛异常，必须能回落到一个可用名字
        var fallback = TaskWatcher.Resolve("__no_such_task__", watchdog: true);
        Check("Resolve 兜底不抛异常", !string.IsNullOrEmpty(fallback), fallback);
    }

    // ── ⑥ 角色对调的 DHCP 切换判定 ────────────────────────────
    private static void CheckDhcpConversion()
    {
        // 旧实现在这里翻车：它只判断"网卡上有 IP"，而刚从私有端换过来的网卡
        // 正带着 192.168.137.1 / 192.168.3.x 这两个静态地址 → 被判成"已就绪"直接跳过。
        var oldStatic = new HashSet<string> { "192.168.137.1", "192.168.3.99" };

        Check("仍是旧静态地址 → 判为未切换",
            !Repairer.IsDhcpConverted(true, new[] { "192.168.137.1", "192.168.3.99" }, oldStatic));
        Check("只掉了一个旧地址、还剩一个 → 仍判未切换",
            !Repairer.IsDhcpConverted(true, new[] { "192.168.137.1" }, oldStatic));
        Check("新旧地址并存 → 仍判未切换（残留的 137.1 会和私有端撞车）",
            !Repairer.IsDhcpConverted(true, new[] { "192.168.137.1", "203.0.113.45" }, oldStatic));
        Check("拿到新的 DHCP 地址、旧地址已清空 → 判为已切换",
            Repairer.IsDhcpConverted(true, new[] { "203.0.113.45" }, oldStatic));
        Check("DHCP 关着 → 有地址也不算切换",
            !Repairer.IsDhcpConverted(false, new[] { "203.0.113.45" }, oldStatic));
        Check("没有任何地址 → 不算切换",
            !Repairer.IsDhcpConverted(true, Array.Empty<string>(), oldStatic));
        Check("原本无地址、现在有地址 → 算切换",
            Repairer.IsDhcpConverted(true, new[] { "10.0.0.5" }, new HashSet<string>()));
    }

    // ── 输出helper ────────────────────────────────────────────
    // ── ⑦ DHCP 报文记账 ───────────────────────────────────────
    // 只验证"能按客户端 MAC 区分要不要得到地址"，不碰真实网卡。
    private static void CheckDhcpWatch()
    {
        IcsWatch.Monitor.DhcpWatch.Reset();

        var mac = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x01 };
        Check("没有报文时不报卡住", IcsWatch.Monitor.DhcpWatch.StuckClients().Count == 0);

        var discover = MakeDhcp(op: 1, mac, msgType: 1);
        for (var i = 0; i < 5; i++) IcsWatch.Monitor.DhcpWatch.OnDatagram(false, discover);

        var stuck = IcsWatch.Monitor.DhcpWatch.StuckClients();
        Check("连续 5 次 DISCOVER 无应答 → 判为卡住",
            stuck.Count == 1 && stuck[0] == "AA-BB-CC-DD-EE-01", string.Join(",", stuck));

        IcsWatch.Monitor.DhcpWatch.OnDatagram(true, MakeDhcp(op: 2, mac, msgType: 2));
        Check("服务器应答后不再报卡住", IcsWatch.Monitor.DhcpWatch.StuckClients().Count == 0);

        IcsWatch.Monitor.DhcpWatch.Reset();
    }

    private static byte[] MakeDhcp(byte op, byte[] mac, byte msgType)
    {
        var b = new byte[250];
        b[0] = op; b[1] = 1; b[2] = 6;          // op / htype / hlen
        Array.Copy(mac, 0, b, 28, 6);           // chaddr
        b[236] = 0x63; b[237] = 0x82; b[238] = 0x53; b[239] = 0x63;
        b[240] = 53; b[241] = 1; b[242] = msgType;
        b[243] = 255;
        return b;
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("▌ " + title);
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _passed++; Console.WriteLine($"  ✓ {name}"); }
        else
        {
            Failures.Add(name + (string.IsNullOrEmpty(detail) ? "" : $"  [{detail}]"));
            Console.WriteLine($"  ✗ {name}{(string.IsNullOrEmpty(detail) ? "" : $"  [{detail}]")}");
        }
    }
}
