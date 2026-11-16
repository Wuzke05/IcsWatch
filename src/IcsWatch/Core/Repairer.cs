using IcsWatch.Config;
using Microsoft.Win32;

namespace IcsWatch.Core;

public sealed class RepairRecord
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Reason { get; init; } = "";
    public string Mode { get; init; } = "light";
    public double Seconds { get; set; }
    public List<string> Steps { get; } = new();
    public string Detail { get; set; } = "";

    /// <summary>
    /// 复检后仍未修好的项。
    /// 【为什么要有这个】旧实现"只要没抛异常就 Success=true"，
    /// 于是出现过：网卡还在"正在识别网络"、类别根本没改成，日志却写"成功 ✅" —— 骗用户。
    /// 现在一律以**复检回读结果**为准。
    /// </summary>
    public List<string> Failed { get; } = new();
    public bool Success => Failed.Count == 0;
    public string ResultText => Failed.Count == 0 ? "成功" : $"部分失败 {Failed.Count}";

    public string Summary =>
        $"{Time:HH:mm:ss}  [{Mode}]  {Reason}  耗时 {Seconds:0.0}s  " +
        (Success ? "成功 ✅" : $"部分失败 ⚠️  未修好: {string.Join("、", Failed)}");
}

/// <summary>
/// 修复执行器 —— 把 fix-ics-final.ps1 的逻辑原生搬进 app（"脚本整合进 app"）。
/// 分成两档：light（只修轻量项，毫秒级）/ full（全量重建 ICS）。
/// </summary>
public static class Repairer
{
    // ══════════════════════════════════════════════════════════════
    //  维修计划（plan）
    //
    //  【为什么用"计划表"而不是一长串顺序代码 + 另一个复检清单】
    //  原来有两个各自独立的东西：
    //    ① RunLight/RunFull 里的动作顺序（顺序是有硬约束的，见下）
    //    ② 另一个 RepairTargets 字符串数组，告诉复检"要检查哪几项"
    //  这两者的对应关系是**隐式**的：改一个中文项名，编译照过、运行不报错，
    //  但复检会永远"找不到该项"→ 静默变成假成功。这就是这轮要消灭的契约。
    //
    //  现在每一步自己声明"我为哪些检查项负责"（Keys），复检目标由计划表**推导**出来，
    //  想漏检都漏不掉；项名改了也不会影响（键是 ASCII 常量）。
    // ══════════════════════════════════════════════════════════════

    /// <summary>一步维修动作。</summary>
    private sealed class PlanStep
    {
        /// <summary>日志里显示的步骤名</summary>
        public string Title { get; init; } = "";

        /// <summary>这一步负责修好的检查项（用 <see cref="CheckKeys"/>）；复检目标由它推导</summary>
        public string[] Keys { get; init; } = Array.Empty<string>();

        /// <summary>null = 只登记复检项、没有动作（例如 NAT 出口只能靠实测验证）</summary>
        public Action<RepairCtx>? Act { get; init; }
    }

    /// <summary>计划执行上下文</summary>
    private sealed class RepairCtx
    {
        public AppConfig Cfg = null!;
        public Action<string> Log = _ => { };
        public bool RolesChanged;

        /// <summary>
        /// 角色对调**之前**的私有/公网端网卡名。
        /// 【为什么需要】配置一旦改成新名字，重建就再也看不到旧名字了 ——
        /// 而旧私有端此刻仍然是 ICS 的私有端、仍然占着 192.168.137.1。
        /// 不把它撤下来，新私有端再写同一个地址就会变成"两张网卡同 IP"（10-26 实测踩到）。
        /// </summary>
        public string? OldPrivate;
        public string? OldPublic;

        /// <summary>本次涉及的全部网卡名（旧 + 新，去重）—— 撤共享/清理时都要覆盖到</summary>
        public List<string> AllAdapters()
        {
            var set = new List<string>();
            void Add(string? n)
            {
                if (string.IsNullOrWhiteSpace(n)) return;
                if (!set.Contains(n!, StringComparer.OrdinalIgnoreCase)) set.Add(n!);
            }
            Add(OldPrivate); Add(OldPublic);
            Add(Cfg.PrivateAdapter); Add(Cfg.PublicAdapter);
            return set;
        }
    }

    /// <summary>轻量维修计划 —— 顺序是硬约束，改动前先读每一步的注释。</summary>
    private static readonly PlanStep[] LightPlan =
    {
        // ⓪ 服务兜底：SharedAccess / NlaSvc 不在跑就先拉起来
        //    （它们停着的话，后面"类别判定""共享重开"都无从谈起）
        new()
        {
            Title = "服务兜底",
            Keys = new[] { CheckKeys.SharedAccess, CheckKeys.NlaSvc },
            Act = c => { EnsureService("SharedAccess", c.Log); EnsureService("NlaSvc", c.Log); },
        },

        // ① 卡1 静态地址（顺带清掉可能冒出来的默认网关 —— 那会抢走出口）
        //    这一步同时把从属 IP 补回来（netsh 写静态会清空该网卡其它地址）
        new()
        {
            Title = "卡1 静态地址",
            Keys = new[] { CheckKeys.PrivateIp, CheckKeys.PrivateRoute, CheckKeys.PrivateAltIp },
            Act = c => { ForceStaticAddress(c.Cfg, c.Log); },
        },

        // ② 注册表 EnableDHCP = 0（卡1 不得从路由器拿网关）
        new()
        {
            Title = "注册表 EnableDHCP",
            Keys = new[] { CheckKeys.DhcpRegistry },
            Act = c => { SetEnableDhcpReg(c.Cfg.PrivateAdapter, 0, c.Log); },
        },

        // ③（已移到 ⑤ 之后）网络类别 → Private
        //    【为什么挪走】启用共享会把私有网卡重新分类回 Public，
        //    排在共享之前修等于白修 —— 现场 12:15 那次维修就是这么"部分失败"的。

        // ④ ICS 共享两端被关则重开（ipnat 驱动由它拉起；DHCP 监听随之出现）
        new()
        {
            Title = "ICS 共享两端",
            Keys = new[] { CheckKeys.IcsPrivate, CheckKeys.IcsPublic, CheckKeys.IcsDhcpListen, CheckKeys.IpNat },
            Act = c => FixIcsSharing(c.Cfg, c.Log),
        },

        // ④b 有设备在要地址却拿不到 → 重置 DHCP 分配器
        //     （服务在跑、67 也在听，但分配器的内部状态卡住了，只能靠重开共享）
        new()
        {
            Title = "重置 DHCP 分配器",
            Keys = new[] { CheckKeys.IcsDhcpStuck },
            Act = c => ForceRecycleSharing(c.Cfg, c.Log),
        },

        // ⑤ 接口度量 —— 必须放在共享之后：ICS 启用共享时会重置度量
        new()
        {
            Title = "接口度量",
            Keys = new[] { CheckKeys.PrivateMetric, CheckKeys.PublicMetric },
            Act = c =>
            {
                FixMetric(c.Cfg.PrivateAdapter, c.Cfg.PrivateMetric, c.Log);
                FixMetric(c.Cfg.PublicAdapter, c.Cfg.PublicMetric, c.Log);
            },
        },

        // ⑥ 网络类别 → Private ——**必须放在启用共享之后**。
        //    启用共享会把私有网卡重新分类回 Public（NLA 看到"无网关/无 DNS"就这么判），
        //    所以排在共享之前修等于白修。现场 12:15 那次维修就是这么"部分失败"的。
        //    设类别不会影响已建立的共享/地址，放在这里安全。
        new()
        {
            Title = "网络类别 → Private",
            Keys = new[] { CheckKeys.PrivateCategory },
            Act = c => FixCategory(c.Cfg.PrivateAdapter, c.Log),
        },

        // ⑦ 从属 IP 也必须最后补：ICS 启用共享时会**重置私有网卡的地址表**，
        //    放在前面补会被冲掉（实测：日志说补好了，复检发现又没了）。同为顺序问题。
        new()
        {
            Title = "补从属 IP",
            Keys = new[] { CheckKeys.PrivateAltIp },
            Act = c => EnsureAltIp(c.Cfg, NetProbe.ReadAdapter(c.Cfg.PrivateAdapter), c.Log),
        },

        // ⑧ 有设备要不到地址 → 重置分配器（放在共享与类别之后，别把刚修好的东西又冲掉）
        new()
        {
            Title = "重置 DHCP 分配器",
            Keys = new[] { CheckKeys.IcsDhcpStuck },
            Act = c => ForceRecycleSharing(c.Cfg, c.Log),
        },

        // ⑨ NAT 出口没有"动作"可做，只能靠实测验证前面几步是否真的通了
        new()
        {
            Title = "NAT 出口实测（仅复检）",
            Keys = new[] { CheckKeys.NatEgress },
        },

        // ⑩ 清理僵尸租约 —— 放在最后：只动 ARP 表里已确认死掉的条目，不影响前面任何一步。
        //    不放进复检 Keys：它修好与否不改变任何检查项的结论（租约条数不是故障）。
        new()
        {
            Title = "清理僵尸租约",
            Keys = new[] { CheckKeys.ZombieLease },
            Act = c => NeighborJanitor.Cleanup(c.Cfg, c.Log),
        },
    };

    public static RepairRecord RunLight(AppConfig cfg, string reason, Action<string>? log = null)
        => RunPlan(cfg, reason, "light", "[维修] ", LightPlan, log, rolesChanged: false);

    /// <param name="rolesChanged">
    /// 是否刚做过「网卡角色对调」。**只有角色真的换了**才需要动公网端的 DHCP 配置
    /// （校园网靠 DHCP + 认证拿地址，乱动会把连接搞掉）。普通重建一律 false。
    /// </param>
    /// <param name="oldPrivate">对调前的私有端网卡名（仅 rolesChanged=true 时有意义）</param>
    /// <param name="oldPublic">对调前的公网端网卡名（仅 rolesChanged=true 时有意义）</param>
    public static RepairRecord RunFull(AppConfig cfg, string reason, Action<string>? log = null,
                                       bool rolesChanged = false,
                                       string? oldPrivate = null, string? oldPublic = null)
        => RunPlan(cfg, reason, "full", "[重建] ", FullPlan, log, rolesChanged, oldPrivate, oldPublic);

    /// <summary>全量重建计划 —— 顺序是硬约束，改动前先读每一步的注释。</summary>
    private static readonly PlanStep[] FullPlan =
    {
        // ⓪ NlaSvc 兜底 —— 第 ⑦ 步「网络类别 → Private」全靠它算出来的网络位置，
        //    它停着的话 WMI 的 MSFT_NetConnectionProfile 改不动。
        new()
        {
            Title = "NlaSvc 兜底",
            Keys = new[] { CheckKeys.NlaSvc },
            Act = c => { EnsureService("NlaSvc", c.Log); },
        },

        // ① 先撤掉**所有相关**网卡的共享（旧角色 + 新角色）。
        //    【10-26 的坑】原来只撤新名字两端的共享：角色对调后，旧私有端仍是 ICS 私有端、
        //    仍占着 192.168.137.1，于是新私有端再写同一个地址 → 两张网卡同 IP →
        //    ICS DHCP 监听绑不上 67、NLA 也认不出网络类型（日志里两条都出现过）。
        new()
        {
            Title = "撤销共享（新旧角色全覆盖）",
            Act = c =>
            {
                using var ics = new IcsShareManager();
                if (!ics.Available) { c.Log("ICS COM 不可用，跳过撤销共享: " + ics.LastError); return; }

                foreach (var name in c.AllAdapters())
                {
                    var st = ics.Get(name);
                    if (st == null) continue;                  // 该名字不对应任何连接
                    if (!st.Enabled) { c.Log($"共享未启用: {name}"); continue; }
                    ics.Disable(name);
                    c.Log($"已撤销共享: {name}");
                }
                Thread.Sleep(1500);
            },
        },

        // ①.5 角色适配（**只在角色真的对调过时**才做）：
        //      私有端用静态地址 → 注册表关 DHCP；公网端要能重新 DHCP 拿上游地址。
        //      【教训】这步原来无条件执行，等于每次重建都去改校园网网卡的 DHCP 租约，
        //      会把上游连接打断（测试时就这么把网络掐了）。普通重建必须跳过。
        //
        //      【顺序不能反】新公网端 = 旧私有端，身上还挂着 192.168.137.1 / 192.168.3.x。
        //      必须**先**把它交还给 DHCP（清掉静态地址），**再**去给新私有端写 192.168.137.1；
        //      否则两张网卡同时持有同一个 IP，ICS 的 DHCP 服务会绑不上 67 端口（10-26 实测）。
        new()
        {
            Title = "角色适配",
            Act = c =>
            {
                if (!c.RolesChanged) { c.Log("角色未变，跳过公网端 DHCP 调整"); return; }

                if (!string.IsNullOrWhiteSpace(c.OldPrivate) || !string.IsNullOrWhiteSpace(c.OldPublic))
                    c.Log($"角色对调: 私有端 {c.OldPrivate} → {c.Cfg.PrivateAdapter}　" +
                          $"公网端 {c.OldPublic} → {c.Cfg.PublicAdapter}");

                // ① 新私有端：原来靠 DHCP 拿校园网地址，现在必须锁成静态、关 DHCP
                SetEnableDhcpReg(c.Cfg.PrivateAdapter, 0, c.Log);

                // ② 新公网端（= 旧私有端）：交出静态地址，切回 DHCP
                if (!EnsurePublicDhcp(c.Cfg.PublicAdapter, c.Log))
                    c.Log("⚠ 公网端没确认到 DHCP 地址 —— 复检会把这一项判为未通过");
            },
        },

        // ② 停止 SharedAccess（卡死则清僵尸 svchost）
        new()
        {
            Title = "停止 SharedAccess",
            Act = c => StopServiceWithZombieCleanup("SharedAccess", c.Log),
        },

        // ③ 卡1 静态 IP：192.168.137.1/24，不设网关
        new()
        {
            Title = "卡1 静态地址",
            Keys = new[] { CheckKeys.PrivateIp, CheckKeys.PrivateRoute, CheckKeys.PrivateAltIp },
            Act = c => { ForceStaticAddress(c.Cfg, c.Log); },
        },

        // ④ 卡1 必须关 DHCP（原来只在"角色对调"时做，于是普通重建复检会挂
        //    「注册表 EnableDHCP」却没人修 —— 现在无条件执行，它本来就是"已知良好状态"的一部分）
        new()
        {
            Title = "卡1 关闭 DHCP",
            Keys = new[] { CheckKeys.DhcpRegistry },
            Act = c => { SetEnableDhcpReg(c.Cfg.PrivateAdapter, 0, c.Log); },
        },

        // ⑤ ScopeAddress 注册表
        new()
        {
            Title = "ScopeAddress",
            Act = c =>
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(
                        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters", writable: true);
                    k?.SetValue("ScopeAddress", c.Cfg.PrivateIp, RegistryValueKind.String);
                    c.Log($"ScopeAddress = {c.Cfg.PrivateIp}");
                }
                catch (Exception ex) { c.Log("写 ScopeAddress 失败: " + ex.Message); }
            },
        },

        // ⑥ 启动 SharedAccess
        new()
        {
            Title = "启动 SharedAccess",
            Keys = new[] { CheckKeys.SharedAccess },
            Act = c => StartService("SharedAccess", c.Log),
        },

        // ⑦ 按顺序启用共享：公网端 → 私有端（ipnat / DHCP 监听随之就位）
        new()
        {
            Title = "启用共享",
            Keys = new[] { CheckKeys.IcsPrivate, CheckKeys.IcsPublic, CheckKeys.IcsDhcpListen, CheckKeys.IpNat },
            Act = c =>
            {
                using var ics = new IcsShareManager();
                if (ics.Available)
                {
                    ics.Enable(c.Cfg.PublicAdapter, 0);
                    Thread.Sleep(1500);
                    ics.Enable(c.Cfg.PrivateAdapter, 1);
                    c.Log("已启用共享: 卡2=公网端 卡1=私有端");
                }
                else c.Log("ICS COM 不可用: " + ics.LastError);
                Thread.Sleep(2500);
            },
        },

        // ⑧ 网络类别 → Private（带重试；启共享会把网卡重分类回 Public）
        new()
        {
            Title = "网络类别 → Private",
            Keys = new[] { CheckKeys.PrivateCategory },
            Act = c => FixCategory(c.Cfg.PrivateAdapter, c.Log, retries: 6),
        },

        // ⑨ 度量（ICS 启用后可能被重置，必须最后设）
        new()
        {
            Title = "接口度量",
            Keys = new[] { CheckKeys.PrivateMetric, CheckKeys.PublicMetric },
            Act = c =>
            {
                FixMetric(c.Cfg.PrivateAdapter, c.Cfg.PrivateMetric, c.Log);
                FixMetric(c.Cfg.PublicAdapter, c.Cfg.PublicMetric, c.Log);
            },
        },

        // ⑩ 从属 IP 同样必须在启用共享之后再补（否则被 ICS 重置掉）
        new()
        {
            Title = "补从属 IP",
            Keys = new[] { CheckKeys.PrivateAltIp },
            Act = c => EnsureAltIp(c.Cfg, NetProbe.ReadAdapter(c.Cfg.PrivateAdapter), c.Log),
        },

        // ⑪ NAT 出口只能靠实测
        new()
        {
            Title = "NAT 出口实测（仅复检）",
            Keys = new[] { CheckKeys.NatEgress },
        },
    };

    /// <summary>
    /// 计划表里声明负责的全部检查项 Key —— 供 tests\IcsWatch.SelfCheck 校验
    /// "计划声明的 Key 确实都有对应体检项"（漏一个 = 那项永远不被复检）。
    /// </summary>
    internal static IReadOnlyList<string> PlannedKeys =>
        LightPlan.Concat(FullPlan).SelectMany(s => s.Keys).Distinct().ToList();

    /// <summary>
    /// 统一执行器：按顺序跑计划 → 用推导出的 Key 集合复检。
    /// 每一步单独 try/catch —— 某一步炸了不该让后面全不执行（原来整轮一个 catch，
    /// 中途异常会留下"改了一半"的状态，而后面本该把它救回来的步骤全被跳过）。
    /// </summary>
    private static RepairRecord RunPlan(AppConfig cfg, string reason, string mode, string logPrefix,
                                        PlanStep[] plan, Action<string>? log, bool rolesChanged,
                                        string? oldPrivate = null, string? oldPublic = null)
    {
        var rec = new RepairRecord { Reason = reason, Mode = mode };
        var t0 = DateTime.Now;
        void L(string s) { rec.Steps.Add(s); log?.Invoke(s); AppLog.Info(logPrefix + s); }

        var ctx = new RepairCtx
        {
            Cfg = cfg, Log = L, RolesChanged = rolesChanged,
            OldPrivate = oldPrivate, OldPublic = oldPublic,
        };
        var stepErrors = new List<string>();

        // 电源状态刚切换（休眠 / 唤醒）时先等一会儿 —— 这时候动 ICS 会拿到 0x8007030F，
        // 一次失败会连带后面 7 项一起失败（10-18 一天出现 5 次）。最多等 20 秒。
        PowerStateWatcher.WaitStable(20_000, L);

        foreach (var step in plan)
        {
            if (step.Act == null) { L($"{step.Title}"); continue; }
            try { step.Act(ctx); }
            catch (Exception ex)
            {
                var msg = $"{step.Title} 异常: {AppLog.Ex(ex)}";
                stepErrors.Add(msg);
                L(msg);
            }
        }

        // 复检目标从计划表推导 —— 每一步自己声明负责哪些项，不存在"忘了加进清单"这种事
        var targets = plan.SelectMany(s => s.Keys).Distinct().ToList();

        // 角色对调时额外验"新公网端"：这是整个操作里唯一会被改坏、又不在计划项里的东西。
        // 【为什么必须加】10-26 11:04 那次，netsh 切 DHCP 明明失败了、上游没拿到地址，
        // 但因为复检清单里没有「卡2 地址」，14 项全过 → 报了"成功 ✅"。
        if (rolesChanged)
        {
            targets.Add(CheckKeys.PublicIp);
            targets.Add(CheckKeys.PublicRoute);
        }

        VerifyAfterRepair(cfg, rec, L, targets.Distinct().ToArray());

        if (stepErrors.Count > 0)
            rec.Detail = (rec.Detail + " | " + string.Join(" | ", stepErrors)).Trim(' ', '|');

        rec.Seconds = (DateTime.Now - t0).TotalSeconds;
        return rec;
    }

    /// <summary>应急回退：强制走内嵌/磁盘上的 fix-ics-final.ps1（保留已验证的老路）</summary>
    public static RepairRecord RunLegacy(AppConfig cfg, string reason, Action<string>? log = null)
    {
        var rec = new RepairRecord { Reason = reason, Mode = "legacy" };
        var t0 = DateTime.Now;
        void L(string s) { rec.Steps.Add(s); log?.Invoke(s); AppLog.Info("[legacy] " + s); }

        var script = Path.Combine(cfg.ScriptPath, "fix-ics-final.ps1");
        if (!File.Exists(script))
        {
            rec.Failed.Add("找不到内嵌脚本");
            rec.Detail = "找不到脚本: " + script;
            L(rec.Detail);
            rec.Seconds = (DateTime.Now - t0).TotalSeconds;
            return rec;
        }

        L($"调用外部脚本: {script} · 内含 120 秒等待");
        var r = ProcessRunner.RunPowerShellHidden(script, isFile: true, timeoutMs: 600_000);
        L($"退出码 = {r.ExitCode}");
        if (!string.IsNullOrWhiteSpace(r.StdOut)) L("输出: " + TextUtil.Trunc(r.StdOut, 800));
        if (!string.IsNullOrWhiteSpace(r.StdErr)) L("错误: " + TextUtil.Trunc(r.StdErr, 800));
        if (!r.Ok) rec.Failed.Add("外部脚本执行失败");
        rec.Detail = r.Ok ? "外部脚本执行成功" : "外部脚本执行失败";
        rec.Seconds = (DateTime.Now - t0).TotalSeconds;
        return rec;
    }

    // ── 单项操作 ────────────────────────────────────────────────

    /// <summary>
    /// 维修后复检 —— 用同一套 22 项判定回读，只有真的 PASS 才算修好。
    /// 【这是修 Bug 的关键】旧实现"没抛异常 = 成功"，导致网卡还没起来、
    /// 类别根本没改成，也照样报"成功 ✅"。
    ///
    /// 【两轮判定】netsh 写地址 / 改类别 / 启共享 的生效延迟实测可达 1~2 秒：
    /// 10-24 11:06:48 日志说"已补回从属 IP"，11:06:49 复检却报"缺 192.168.3.x"，
    /// 到 11:07:26 又自己全绿了 —— 纯粹的时序毛刺，却让整轮维修记成"部分失败"。
    /// 所以第一轮有未通过项时，隔 1.2 秒只对**未通过的项**再判一次。
    ///
    /// 【跳过 ≠ 失败】Checker 在"卡1 无链路 / 卡2 未接入"时会把依赖项降级成
    /// INFO「跳过 · …」。那不是维修失败，必须跳过，否则网卡不在时每轮维修都白记一笔
    /// "部分失败"（10-23 18:04、20:05 两次实证），还会白白吃掉每小时维修配额。
    /// </summary>
    private static void VerifyAfterRepair(AppConfig cfg, RepairRecord rec, Action<string> log, string[] keys)
    {
        Thread.Sleep(700);

        // pending 里存 (Key, 显示名)：匹配用 Key，报给用户的是中文名
        var pending = new List<(string Key, string Name)>();
        if (!VerifyOnce(cfg, keys, pending, log))
        {
            rec.Failed.Add("复检执行失败");
            rec.Detail = "复检执行失败";
            return;
        }

        // 第二次机会：只针对仍未通过的项，避免整轮重来浪费 0.5 秒以上
        if (pending.Count > 0)
        {
            log($"复检有 {pending.Count} 项未通过，1.2 秒后重试: {string.Join("、", pending.Select(p => p.Name))}");
            Thread.Sleep(1200);

            var again = new List<(string Key, string Name)>();
            if (VerifyOnce(cfg, pending.Select(p => p.Key).ToArray(), again, log))
            {
                foreach (var p in pending)
                    if (!again.Any(a => a.Key == p.Key)) log($"复检重试通过: {p.Name}");
                pending = again;
            }
        }

        foreach (var p in pending) rec.Failed.Add(p.Name);
        rec.Detail = rec.Failed.Count == 0
            ? "全部目标项复检通过"
            : $"复检未通过 {rec.Failed.Count} 项: {string.Join("、", rec.Failed)}";
    }

    /// <summary>
    /// 跑一次体检，把「仍未通过」的目标项填进 failed（Key + 显示名）。
    /// 返回 false 表示复检本身执行失败（体检抛异常），与"某项没修好"要区分开。
    /// </summary>
    private static bool VerifyOnce(AppConfig cfg, string[] keys,
                                   List<(string Key, string Name)> failed, Action<string> log)
    {
        CheckSnapshot snap;
        try { snap = Checker.Run(cfg); }
        catch (Exception ex)
        {
            log("复检执行失败: " + AppLog.Ex(ex));
            return false;
        }

        foreach (var key in keys)
        {
            var it = snap.Find(key);
            if (it == null)
            {
                // 该项在当前拓扑下不适用（例如上游没有默认网关时"不产生 NAT 出口实测"），
                // 不算失败 —— 否则校园网一断就会误报"部分失败"
                log($"复检跳过: 本轮未产出该项（{key}）· 当前不适用");
                continue;
            }
            if (it.Status == CheckStatus.Pass)
            {
                log($"复检通过: {it.Name}");
                continue;
            }
            if (it.Status == CheckStatus.Info)
            {
                // 「跳过 · 卡2 未接入」这类降级项：链路根本不在，不是维修的锅
                log($"复检跳过: {it.Name} · {it.Detail}");
                continue;
            }
            failed.Add((key, it.Name));
            log($"复检未通过: {it.Name} → {it.Status} {it.Detail}");
        }
        return true;
    }

    /// <summary>确保服务在跑；不在跑就启动并回读确认</summary>
    private static bool EnsureService(string service, Action<string> log)
    {
        try
        {
            using (var sc = new System.ServiceProcess.ServiceController(service))
            {
                if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running)
                {
                    log($"{service} 已在运行");
                    return true;
                }
                log($"{service} 状态 {sc.Status} → 尝试启动…");
                sc.Start();
                try { sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(15)); }
                catch (Exception __ex) { AppLog.Swallow("Repairer.cs:278", __ex); }
            }
        }
        catch (Exception ex)
        {
            log($"启动 {service} 出错: {ex.Message}");
            return false;
        }

        var st = NetProbe.ServiceState(service);
        if (st == "Running") { log($"{service} 已启动"); return true; }
        log($"{service} 启动后状态: {st} 非 Running");
        return false;
    }

    private static void FixMetric(string adapter, int metric, Action<string> log)
    {
        // 网卡整个不在（拔了 / 掉出总线）时 netsh 会报"找不到元素"，
        // 这条失败既没意义又会污染维修记录 —— 直接跳过，交给体检的「跳过 · 未接入」呈现。
        if (!NetProbe.ReadAdapter(adapter).Exists)
        {
            log($"跳过度量: {adapter} 当前不在（未插 / 已禁用）");
            return;
        }

        var cur = NetProbe.GetInterfaceMetric(adapter);
        if (cur == metric) { log($"度量已正确: {adapter} = {metric}"); return; }
        var r = ProcessRunner.Run("netsh.exe", $"interface ipv4 set interface \"{adapter}\" metric={metric}", 20_000);
        log(r.Ok ? $"度量已设置: {adapter} {cur} -> {metric}" : $"设置度量失败: {r.All}");
    }

    /// <summary>写注册表 EnableDHCP（0=关/1=开）；关的时候顺带清掉陈旧 DHCP 租约键</summary>
    private static bool SetEnableDhcpReg(string adapter, int value, Action<string> log)
    {
        var ad = NetProbe.ReadAdapter(adapter);
        if (string.IsNullOrEmpty(ad.Guid)) { log($"跳过 EnableDHCP: 取不到 {adapter} 的 GUID"); return false; }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{ad.Guid}", writable: true);
            if (k == null) { log("跳过 EnableDHCP: 打不开注册表项"); return false; }
            var cur = k.GetValue("EnableDHCP");
            if (cur == null || Convert.ToInt32(cur) != value)
            {
                k.SetValue("EnableDHCP", value, RegistryValueKind.DWord);
                log($"注册表 EnableDHCP {adapter}: {cur} -> {value}");
            }
            if (value == 0)
            {
                foreach (var name in new[] { "DhcpIPAddress", "DhcpDefaultGateway", "DhcpSubnetMask", "DhcpNameServer", "DhcpServer" })
                {
                    try { if (k.GetValue(name) != null) { k.DeleteValue(name, false); log($"已清理陈旧租约键: {name}"); } } catch (Exception __ex) { AppLog.Swallow("Repairer.cs:321", __ex); }
                }
            }
        }
        catch (Exception ex) { log("EnableDHCP 处理失败: " + ex.Message); return false; }

        return NetProbe.GetEnableDhcpReg(ad.Guid) == value;
    }

    /// <summary>
    /// 把一块网卡从静态地址**交还给 DHCP**（角色对调时，旧私有端要变回上游网卡）。
    ///
    /// 【三条都是 10-26 实测踩出来的】
    ///  ① **顺序**：必须先 `netsh source=dhcp`，再动注册表。反过来先写 EnableDHCP=1 之后，
    ///     netsh 会认为"已在此接口上启用 DHCP"直接报错退出，静态地址反而清不掉
    ///     （日志实录：`公网端 以太网 切 DHCP 失败: 已在此接口上启用 DHCP。`）。
    ///  ② **判据**：不能拿"网卡上有 IP"当"已经在用 DHCP"。刚从私有端换过来的网卡正带着
    ///     192.168.137.1 / 192.168.3.x，旧实现就此早退，还在日志里写
    ///     `公网端 以太网 2 已在使用 DHCP: 192.168.137.1, 192.168.3.x` —— 自相矛盾。
    ///  ③ **验收**：必须回读确认"地址真的换成了 DHCP 租约"，而不是看命令退出码。
    /// </summary>
    private static bool EnsurePublicDhcp(string adapter, Action<string> log)
    {
        var before = NetProbe.ReadAdapter(adapter);
        if (!before.Exists) { log($"跳过 {adapter}: 网卡不存在"); return false; }

        var beforeIps = before.Ipv4.ToHashSet(StringComparer.Ordinal);
        if (beforeIps.Count > 0)
            log($"公网端 {adapter} 现有静态地址: {string.Join(", ", beforeIps)} · 交还给 DHCP");

        // ① 先让接口交出静态地址（这条命令本身也会把 EnableDHCP 置 1）
        var r = ProcessRunner.Run("netsh.exe",
            $"interface ipv4 set address name=\"{adapter}\" source=dhcp", 25_000);
        if (!r.Ok)
            log($"公网端 {adapter} 切 DHCP 命令返回 {r.ExitCode}: {TextUtil.Trunc(r.All, 160)}");

        // ② 兜底：命令失败时开关可能还没置位
        SetEnableDhcpReg(adapter, 1, log);

        // ③ 已有静态地址时，光切开关不会自己跑 DHCP —— 主动 renew 一次
        ProcessRunner.Run("ipconfig.exe", $"/renew \"{adapter}\"", 25_000);

        // ④ 轮询到"确实是 DHCP 地址"为止（最多 12 秒）
        var deadline = DateTime.Now.AddSeconds(12);
        while (DateTime.Now < deadline)
        {
            var now = NetProbe.ReadAdapter(adapter);
            var dhcpOn = NetProbe.GetEnableDhcpReg(now.Guid) == 1;
            if (IsDhcpConverted(dhcpOn, now.Ipv4, beforeIps))
            {
                log($"公网端 {adapter} 已拿到 DHCP 地址: {string.Join(", ", now.Ipv4)}");
                return true;
            }
            Thread.Sleep(600);
        }

        // 超时：如实报失败，绝不假成功
        var last = NetProbe.ReadAdapter(adapter);
        var lastOn = NetProbe.GetEnableDhcpReg(last.Guid) == 1;
        log($"公网端 {adapter} 未确认到 DHCP 地址（EnableDHCP={(lastOn ? 1 : 0)}，" +
            $"当前地址 {(last.Ipv4.Count > 0 ? string.Join(", ", last.Ipv4) : "无")}）");
        return false;
    }

    /// <summary>
    /// 判定"这块网卡是否真的从静态切到了 DHCP"：
    /// DHCP 开关已开 + 有 IPv4 地址 + **一个切换前的静态地址都不剩**。
    ///
    /// 【为什么"都不剩"而不是"出现新地址就行"】`netsh source=dhcp` 失败时，
    /// Windows 可能保留着静态地址、同时又补一个 DHCP 租约 —— 这时网卡上新旧地址并存，
    /// 旧的 192.168.137.1 会和私有端撞车。只看"有新地址"就会把这种情况判成成功。
    ///
    /// 抽成纯函数是为了让契约自检能覆盖这条曾经出错的判据。
    /// </summary>
    internal static bool IsDhcpConverted(bool enableDhcpOn,
                                         IReadOnlyCollection<string> nowIps,
                                         ISet<string> beforeIps)
    {
        if (!enableDhcpOn) return false;
        if (nowIps.Count == 0) return false;
        foreach (var ip in nowIps)
            if (beforeIps.Contains(ip)) return false;   // 还有旧静态地址残留 → 没真正切过去
        return true;
    }

    private static void FixCategory(string adapter, Action<string> log, int retries = 2)
    {
        for (int i = 1; i <= retries; i++)
        {
            if (NetworkCategoryApi.GetCategory(adapter) == NetworkCategoryApi.Private)
            {
                log($"网络类别已是 Private · {adapter}");
                return;
            }

            // ① 首选：WMI 原生写 MSFT_NetConnectionProfile.NetworkCategory（立即生效）
            if (NetworkCategoryApi.SetPrivate(adapter, out var err))
            {
                Thread.Sleep(700);
                if (NetworkCategoryApi.GetCategory(adapter) == NetworkCategoryApi.Private)
                {
                    log($"网络类别已设为 Private · 第 {i} 次");
                    return;
                }
                log($"第 {i} 次设置后回读仍非 Private，继续尝试…");
            }
            else
            {
                log($"第 {i} 次原生设置类别失败: {err}");
            }

            // ② 兜底：PowerShell 等价命令（语义相同，仅当原生失败时走）
            var ps = $"Set-NetConnectionProfile -InterfaceAlias '{adapter}' -NetworkCategory Private";
            var r2 = ProcessRunner.RunPowerShellHidden(ps, isFile: false, timeoutMs: 30_000);
            log(r2.Ok ? $"兜底 PowerShell 设置类别完成 · 第 {i} 次"
                      : $"兜底也失败 (exit={r2.ExitCode}): {TextUtil.Trunc(r2.All, 180)}");
            Thread.Sleep(1200);
            if (NetworkCategoryApi.GetCategory(adapter) == NetworkCategoryApi.Private) return;
        }
        log($"网络类别仍未设为 Private · {adapter}");
    }

    private static void FixIcsSharing(AppConfig cfg, Action<string> log)
    {
        var ad1 = NetProbe.ReadAdapter(cfg.PrivateAdapter);
        if (!ad1.IsUp) { log("卡1 链路未 Up，跳过 ICS 共享检查"); return; }

        using var ics = new IcsShareManager();
        if (!ics.Available) { log("ICS COM 不可用: " + ics.LastError); return; }

        var c1 = ics.Get(cfg.PrivateAdapter);
        var c2 = ics.Get(cfg.PublicAdapter);
        if (c1 == null || c2 == null) { log("未找到网卡对应的 ICS 连接"); return; }

        // 【为什么不能只看 c1.Enabled && c2.Enabled 就 return】
        // COM 的 Enabled 只说明"配置里写着共享"，不说明 NAT 引擎和 DHCP 真的在跑。
        // 实测：`Restart-Service SharedAccess` 之后两个标志仍是 True，但 ipnat=Stopped、
        // 192.168.137.1:67 没人监听 —— 下游设备全卡在"正在获取 IP"。
        // 那时若按标志早退，维修会打印"ICS 共享两端均已启用"然后报成功，等于自欺。
        var ipnatState = NetProbe.ServiceState("ipnat");
        var natAlive = string.Equals(ipnatState, "Running", StringComparison.OrdinalIgnoreCase);
        var dhcpAlive = NetProbe.UdpListening(cfg.PrivateIp, 67);
        if (c1.Enabled && c2.Enabled && natAlive && dhcpAlive)
        {
            log("ICS 共享两端均已启用");
            return;
        }

        if (c1.Enabled && c2.Enabled)
            log($"ICS 标志为启用，但引擎没在跑（ipnat={ipnatState}，67 端口{(dhcpAlive ? "在" : "不在")}）· 强制重开");
        else
            log($"ICS 共享被关闭: 卡1={c1.Enabled} 卡2={c2.Enabled} · 重新启用…");
        ics.Disable(cfg.PrivateAdapter);
        ics.Disable(cfg.PublicAdapter);
        Thread.Sleep(1500);
        ics.Enable(cfg.PublicAdapter, 0);
        Thread.Sleep(1500);
        ics.Enable(cfg.PrivateAdapter, 1);
        Thread.Sleep(1000);
        log("ICS 共享已重新启用");
    }

    /// <summary>
    /// 强制把共享重开一遍，用于「引擎活着、分配器卡住」：设备反复发 DHCP 请求却拿不到地址。
    ///
    /// <summary>
    /// 重置 ICS 的 DHCP 分配器（把共享禁用再启用一遍）。
    ///
    /// 【为什么不能只 EnsureService】服务是 Running、67 端口也在监听、ipnat 也在跑，
    /// 问题藏在 DHCP 分配器的内部状态里（它维护着一张租约表，长期不释放会越走越偏）。
    /// 只有把共享重新禁用+启用一遍，分配器才会重新初始化。
    ///
    /// 【代价与护栏】重开共享会让**所有已连设备**断一次（4 秒左右），而且它们下次会拿新地址。
    /// 曾经因为没有护栏，19:37 连续重置两次，把正在正常上网的手机（192.168.137.25）踢下线，
    /// 之后手机卡在"获取 IP"——比原问题更糟。所以现在：
    /// ① 两次重置之间至少隔 20 分钟（<see cref="RecycleCooldownMs"/>）；
    /// ② 重置后 5 分钟进入"观察中"，不再判卡住、也不再触发第二次重置（见 DhcpWatch.SuppressAfterReset）。
    /// </summary>
    private const long RecycleCooldownMs = 20 * 60 * 1000;
    private static long _lastRecycleAt;

    private static void ForceRecycleSharing(AppConfig cfg, Action<string> log)
    {
        var since = Environment.TickCount64 - _lastRecycleAt;
        if (_lastRecycleAt != 0 && since < RecycleCooldownMs)
        {
            log($"刚重置过分配器（{since / 1000} 秒前），冷却中，本次跳过");
            return;
        }

        using var ics = new IcsShareManager();
        if (!ics.Available) { log("ICS COM 不可用: " + ics.LastError); return; }

        log("有设备要不到地址 · 重置 DHCP 分配器（重开共享）…");
        ics.Disable(cfg.PrivateAdapter);
        ics.Disable(cfg.PublicAdapter);
        Thread.Sleep(1200);
        ics.Enable(cfg.PublicAdapter, 0);
        Thread.Sleep(1200);
        ics.Enable(cfg.PrivateAdapter, 1);
        Thread.Sleep(800);

        _lastRecycleAt = Environment.TickCount64;
        Monitor.DhcpWatch.SuppressAfterReset(300);   // 不 Reset，否则复检必然"通过"
        log("ICS 共享已重新启用");
    }

    private static bool ForceStaticAddress(AppConfig cfg, Action<string> log)
    {
        var ad = NetProbe.ReadAdapter(cfg.PrivateAdapter);

        // ⚠️ 判定条件曾经写成 `Ipv4.Count == 1`，是个坑：
        //    卡1 上常态就有 2 个地址（主地址 + 从属 IP），于是每次维修都会走下面的重写分支，
        //    而 `netsh set address ... static` 会**清掉该网卡上的其他所有 IP** ——
        //    结果把 192.168.3.x 抹掉了（进路由器管理页的地址）。已修正为"包含主地址且无网关"。
        if (ad.Ipv4.Contains(cfg.PrivateIp) && !ad.HasGateway)
        {
            log($"卡1 静态地址已正确: {cfg.PrivateIp}");
            EnsureAltIp(cfg, ad, log);
            return true;
        }

        // 重写静态地址（顺带清掉可能从路由器 DHCP 冒出来的网关）
        var r = ProcessRunner.Run("netsh.exe",
            $"interface ipv4 set address name=\"{cfg.PrivateAdapter}\" static {cfg.PrivateIp} 255.255.255.0", 30_000);
        if (!r.Ok) { log($"写静态地址失败: {TextUtil.Trunc(r.All, 160)}"); return false; }
        log($"已写静态地址: {cfg.PrivateIp}/24 · 不设网关");

        // 上面那句 netsh 会连带清掉从属 IP，必须补回来
        Thread.Sleep(600);
        EnsureAltIp(cfg, NetProbe.ReadAdapter(cfg.PrivateAdapter), log);
        return true;
    }

    /// <summary>确保卡1 上的从属 IP（进下游路由器管理页用）还在，丢了就补回来</summary>
    private static void EnsureAltIp(AppConfig cfg, AdapterInfo ad, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(cfg.AltIp)) return;
        if (ad.Ipv4.Contains(cfg.AltIp)) return;

        var r = ProcessRunner.Run("netsh.exe",
            $"interface ipv4 add address \"{cfg.PrivateAdapter}\" {cfg.AltIp} 255.255.255.0 skipassource=true", 20_000);
        if (!r.Ok)
        {
            log($"补从属 IP 失败: {TextUtil.Trunc(r.All, 160)}");
            return;
        }

        // netsh 返回 0 只代表"命令执行了"，地址真正出现在网卡上还要 1~2 秒。
        // 这里主动等它落地，避免紧随其后的复检读到旧状态、误报"缺从属 IP"。
        var deadline = DateTime.Now.AddSeconds(3);
        while (DateTime.Now < deadline)
        {
            if (NetProbe.ReadAdapter(cfg.PrivateAdapter).Ipv4.Contains(cfg.AltIp))
            {
                log($"已补回从属 IP: {cfg.AltIp}");
                return;
            }
            Thread.Sleep(300);
        }
        log($"已下发从属 IP 命令，但 3 秒内未在网卡上看到 {cfg.AltIp}（复检会再确认）");
    }

    private static void StopServiceWithZombieCleanup(string service, Action<string> log)
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(service);
            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                try { sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
                catch (Exception __ex) { AppLog.Swallow("Repairer.cs:468", __ex); }
            }
            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped) { log($"{service} 已停止"); return; }
        }
        catch (Exception ex) { log($"停止 {service} 出错: {ex.Message}"); }

        // 卡在 StopPending → 杀掉仅承载它的 svchost（用 Win32_Service 精确判断，别解析 tasklist）
        for (int i = 1; i <= 5; i++)
        {
            string state = NetProbe.ServiceState(service);
            if (state != "StopPending") { log($"{service} 状态: {state}"); break; }

            var pid = ServiceProcessId(service);
            if (pid <= 0) { log($"{service} 卡在 StopPending，但取不到宿主 PID"); Thread.Sleep(2000); continue; }

            var siblings = ServicesInProcess(pid);
            if (siblings.Count <= 1)
            {
                log($"杀死僵尸 svchost (PID {pid}，同进程仅托管 {service})");
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch (Exception ex) { log("杀进程失败: " + ex.Message); }
            }
            else
            {
                log($"跳过: svchost PID {pid} 还托管 {siblings.Count - 1} 个服务，不宜杀");
                break;
            }
            Thread.Sleep(2500);
        }
    }

    private static void StartService(string service, Action<string> log)
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(service);
            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running) { log($"{service} 已在运行"); return; }
            sc.Start();
            try { sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(15)); } catch (Exception __ex) { AppLog.Swallow("Repairer.cs:505", __ex); }
            log($"{service} 启动后状态: {NetProbe.ServiceState(service)}");
        }
        catch (Exception ex) { log($"启动 {service} 失败: {ex.Message}"); }
    }

    // ── 用 WMI 精确拿"服务->PID"与"PID->同进程服务列表" ──────────
    private static int ServiceProcessId(string serviceName)
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(
                new System.Management.ObjectQuery($"SELECT ProcessId FROM Win32_Service WHERE Name='{serviceName}'"));
            foreach (System.Management.ManagementBaseObject o in s.Get())
            {
                using (o as IDisposable) { }
                var v = o["ProcessId"];
                if (v != null) return Convert.ToInt32(v);
            }
        }
        catch (Exception __ex) { AppLog.Swallow("Repairer.cs:525", __ex); }
        return -1;
    }

    private static List<string> ServicesInProcess(int pid)
    {
        var list = new List<string>();
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(
                new System.Management.ObjectQuery($"SELECT Name FROM Win32_Service WHERE ProcessId={pid}"));
            foreach (System.Management.ManagementBaseObject o in s.Get())
            {
                using (o as IDisposable) { }
                var n = o["Name"]?.ToString();
                if (!string.IsNullOrEmpty(n)) list.Add(n!);
            }
        }
        catch (Exception __ex) { AppLog.Swallow("Repairer.cs:543", __ex); }
        return list;
    }
}
