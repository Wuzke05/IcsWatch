using IcsWatch.Config;

namespace IcsWatch.Core;

/// <summary>
/// 22 项体检 —— 全部原生实现（NetworkInterface / WMI / 注册表 / COM / 原始套接字）。
/// 项名与 scripts\ics-healthcheck.ps1 保持一致，便于逐项对拍。
///
/// 【AutoFixable】标 true = 程序自己能修，是自动维修的触发依据；
/// 物理层/上游问题（网线没插、校园网未认证）一律 false，避免无意义反复修。
///
/// 【文案约定】Details 只写事实和数值，不写解释性括号 —— 解释归属 Hint 列。
///
/// 【Key 约定】每项都必须带 <see cref="CheckKeys"/> 里的稳定标识（构造时第一个参数）。
/// 程序内部的"按项查找"（维修复检、游戏模式改写、界面取值）**一律按 Key**，
/// 绝不按 Name —— 那样改一个中文字就会静默失去复检能力。
/// </summary>
public static class Checker
{
    /// <summary>
    /// 游戏模式轻量体检（5 项）：只关心「USB 出口在不在、有没有地址、能不能出网」，
    /// 外加 WLAN / 卡2 的只读状态（仅展示，不参与任何切换）。
    /// 目的：游戏模式下不为"已经主动停掉的东西"浪费 CPU。
    /// </summary>
    private static CheckSnapshot RunGameModeOnly(AppConfig cfg, CheckSnapshot snap)
    {
        var usb = (cfg.GameUsbAdapter ?? "").Trim();

        // ① 出口网卡存在 / Up
        var adUsb = NetProbe.ReadAdapter(usb);
        if (!adUsb.Exists)
            snap.Items.Add(CheckItem.Fail(CheckKeys.GameEgress, "游戏模式", "游戏出口",
                "未找到 USB 网卡", "插好手机 USB / 打开「USB 网络共享」"));
        else if (!adUsb.IsUp)
            snap.Items.Add(CheckItem.Warn(CheckKeys.GameEgress, "游戏模式", "游戏出口", "Down", "手机 USB 共享未连接"));
        else
            snap.Items.Add(CheckItem.Pass(CheckKeys.GameEgress, "游戏模式", "游戏出口", $"{usb} Up"));

        // ② 出口有有效 IPv4（排除 APIPA）
        var validIp = adUsb.Ipv4.Any(x => !x.StartsWith("169.254.", StringComparison.Ordinal));
        var ip = adUsb.Ipv4.FirstOrDefault() ?? "无地址";
        snap.Items.Add(validIp
            ? CheckItem.Pass(CheckKeys.GameEgressIp, "游戏模式", "出口地址", ip)
            : CheckItem.Warn(CheckKeys.GameEgressIp, "游戏模式", "出口地址", ip, "手机 USB 共享还没就绪"));

        // ③ 出口能出网（ICMP 到公共 DNS；只作参考，不影响自动退出判定）
        var canOut = validIp && IcmpProbe.PingPlain("223.5.5.5", 1200);
        snap.Items.Add(canOut
            ? CheckItem.Pass(CheckKeys.GameInternet, "游戏模式", "出口出网", "通")
            : CheckItem.Info(CheckKeys.GameInternet, "游戏模式", "出口出网", "不通/被拦", "手机可能没信号或没开数据"));

        // ④ WLAN（只读：永远是"未参与"，游戏模式不碰它）
        var wlan = NetProbe.ReadAdapter("WLAN");
        snap.Items.Add(CheckItem.Info(CheckKeys.GameWlan, "游戏模式", "WLAN",
            !wlan.Exists ? "不存在" : (wlan.IsUp ? "Up · 未参与" : "Down · 未参与")));

        // ⑤ 卡2 只读状态（方便你看校园网还在不在，不参与路由）
        var ad2 = NetProbe.ReadAdapter(cfg.PublicAdapter);
        snap.Items.Add(CheckItem.Info(CheckKeys.GamePublicRead, "游戏模式", $"卡2[{cfg.PublicAdapter}]",
            !ad2.Exists ? "不存在" : (ad2.IsUp ? "Up · 已暂停(度量9999)" : "Down")));

        snap.State = HealthState.GameMode;
        return snap;
    }

    public static CheckSnapshot Run(AppConfig cfg, bool fromEvent = false, string trigger = "")
    {
        var snap = new CheckSnapshot { FromEvent = fromEvent, Trigger = trigger };

        // ★游戏模式 + 轻量化：只查「出口是否可用」这几项就返回。
        //   其余 17 项查的都是被我们主动停掉的东西（卡1 / 卡2 / ICS / NAT / 计划任务…），
        //   查完还得改写成"已暂停"，纯烧 CPU（实测单次 0.67s → 0.05s）。
        if (cfg.GameModeLite && cfg.GameModeActive) return RunGameModeOnly(cfg, snap);

        var ad1 = NetProbe.ReadAdapter(cfg.PrivateAdapter);
        var ad2 = NetProbe.ReadAdapter(cfg.PublicAdapter);

        // ── 1/2 链路（物理层，不可修）────────────────────────────
        if (!ad1.Exists)
        {
            // 【稳定性】被"管理性禁用"的网卡会从 NetworkInterface 枚举里消失，和"物理不存在"
            // 看起来一样。若直接报"未找到该网卡"，项名/状态会随枚举波动来回翻转，
            // 导致检查项行数、计数徽章宽度抖动（界面文字跟着跳）。这里区分开。
            if (NetProbe.IsAdminDisabled(cfg.PrivateAdapter) == true)
                snap.Items.Add(CheckItem.Warn(CheckKeys.PrivateLink, "网卡", "卡1 链路",
                    "Down · 已禁用", "本程序或设备管理器禁用了该网卡"));
            else
                snap.Items.Add(CheckItem.Fail(CheckKeys.PrivateLink, "网卡", $"卡1[{cfg.PrivateAdapter}] 存在",
                    "未找到该网卡", "检查配置中的网卡名"));
        }
        else if (!ad1.IsUp)
            snap.Items.Add(CheckItem.Warn(CheckKeys.PrivateLink, "网卡", "卡1 链路", "Down", "插好网线 / 打开路由器"));
        else
            snap.Items.Add(CheckItem.Pass(CheckKeys.PrivateLink, "网卡", "卡1 链路", "Up"));

        if (!ad2.Exists)
        {
            if (NetProbe.IsAdminDisabled(cfg.PublicAdapter) == true)
                snap.Items.Add(CheckItem.Warn(CheckKeys.PublicLink, "网卡", "卡2 链路",
                    "Down · 已禁用", "本程序或设备管理器禁用了该网卡"));
            else
                snap.Items.Add(CheckItem.Fail(CheckKeys.PublicLink, "网卡", $"卡2[{cfg.PublicAdapter}] 存在",
                    "未找到该网卡", "检查配置中的网卡名"));
        }
        else if (!ad2.IsUp)
            snap.Items.Add(CheckItem.Fail(CheckKeys.PublicLink, "网卡", "卡2 链路", "Down", "插好校园网网线"));
        else
            snap.Items.Add(CheckItem.Pass(CheckKeys.PublicLink, "网卡", "卡2 链路", "Up"));

        // ── 3/4/5 地址 ───────────────────────────────────────────
        if (ad1.Ipv4.Contains(cfg.PrivateIp))
            snap.Items.Add(CheckItem.Pass(CheckKeys.PrivateIp, "地址", "卡1 主地址", cfg.PrivateIp));
        else
            snap.Items.Add(CheckItem.Fail(CheckKeys.PrivateIp, "地址", "卡1 主地址",
                $"期望 {cfg.PrivateIp}，实际 {string.Join(", ", ad1.Ipv4)}",
                "改静态 IP 且不设网关", fixable: true));

        var apipa = NetProbe.GetApipa(cfg.PrivateAdapter);
        if (apipa.Count > 0)
            snap.Items.Add(CheckItem.Warn(CheckKeys.PrivateApipa, "地址", "卡1 APIPA 残留",
                $"存在 {string.Join(", ", apipa)}",
                "EnableDHCP=0 后重启网卡", fixable: true));

        // 从属 IP 是可选的：它的历史用途是"再挂一个网段的地址，好去访问下游路由器的管理页"。
        // 现场把下游路由器改成 AP 模式后那个网段没了，这个地址就只剩副作用（多一个 /24、多一份 ARP 噪音），
        // 所以支持留空表示"不要"。留空时仍产出该检查项（Pass/未配置），避免复检时找不到 Key。
        if (string.IsNullOrWhiteSpace(cfg.AltIp))
            snap.Items.Add(CheckItem.Pass(CheckKeys.PrivateAltIp, "地址", "卡1 从属 IP", "未配置"));
        else if (ad1.Ipv4.Contains(cfg.AltIp))
            snap.Items.Add(CheckItem.Pass(CheckKeys.PrivateAltIp, "地址", "卡1 从属 IP", cfg.AltIp, "", fixable: true));
        else
            snap.Items.Add(CheckItem.Warn(CheckKeys.PrivateAltIp, "地址", "卡1 从属 IP",
                $"缺 {cfg.AltIp}",
                $"netsh interface ipv4 add address \"{cfg.PrivateAdapter}\" {cfg.AltIp} 255.255.255.0 skipassource=true",
                fixable: true));

        if (ad2.Ipv4.Count > 0)
            snap.Items.Add(CheckItem.Pass(CheckKeys.PublicIp, "地址", "卡2 地址", string.Join(", ", ad2.Ipv4)));
        else
            snap.Items.Add(CheckItem.Fail(CheckKeys.PublicIp, "地址", "卡2 地址", "未拿到地址", "校园网可能未认证"));

        // ── 6/7 默认路由 ─────────────────────────────────────────
        if (ad1.HasGateway)
            snap.Items.Add(CheckItem.Fail(CheckKeys.PrivateRoute, "路由", "卡1 不得有默认路由",
                $"出现 -> {ad1.Gateway}", "改静态且不设网关", fixable: true));
        else
            snap.Items.Add(CheckItem.Pass(CheckKeys.PrivateRoute, "路由", "卡1 无默认路由", "正确"));

        if (!ad2.HasGateway)
            snap.Items.Add(CheckItem.Fail(CheckKeys.PublicRoute, "路由", "卡2 应有默认路由", "无默认路由", "校园网未生效"));
        else
            snap.Items.Add(CheckItem.Pass(CheckKeys.PublicRoute, "路由", "卡2 默认路由", $"-> {ad2.Gateway}"));

        // ── 8/9/10/11 DHCP 与度量 ────────────────────────────────
        var en1 = NetProbe.GetEnableDhcpReg(ad1.Guid);
        if (en1 == 0) snap.Items.Add(CheckItem.Pass(CheckKeys.PrivateDhcp, "接口", "卡1 DHCP", "Disabled", fixable: true));
        else if (en1 == null) snap.Items.Add(CheckItem.Warn(CheckKeys.PrivateDhcp, "接口", "卡1 DHCP", "读不到 EnableDHCP"));
        else snap.Items.Add(CheckItem.Fail(CheckKeys.PrivateDhcp, "接口", "卡1 DHCP",
            "Enabled", "须写注册表 EnableDHCP=0", fixable: true));

        if (ad1.Metric == cfg.PrivateMetric)
            snap.Items.Add(CheckItem.Pass(CheckKeys.PrivateMetric, "接口", "卡1 度量", ad1.Metric.ToString()!, fixable: true));
        else snap.Items.Add(CheckItem.Warn(CheckKeys.PrivateMetric, "接口", "卡1 度量",
            $"={ad1.Metric?.ToString() ?? "读取失败"} ≠ {cfg.PrivateMetric}",
            $"netsh interface ipv4 set interface \"{cfg.PrivateAdapter}\" metric={cfg.PrivateMetric}", fixable: true));

        if (ad2.Metric == cfg.PublicMetric)
            snap.Items.Add(CheckItem.Pass(CheckKeys.PublicMetric, "接口", "卡2 度量", ad2.Metric.ToString()!, fixable: true));
        else snap.Items.Add(CheckItem.Warn(CheckKeys.PublicMetric, "接口", "卡2 度量",
            $"={ad2.Metric?.ToString() ?? "读取失败"} ≠ {cfg.PublicMetric}",
            $"netsh interface ipv4 set interface \"{cfg.PublicAdapter}\" metric={cfg.PublicMetric}", fixable: true));

        snap.Items.Add(en1 == 0
            ? CheckItem.Pass(CheckKeys.DhcpRegistry, "接口", "注册表 EnableDHCP", "0", fixable: true)
            : CheckItem.Warn(CheckKeys.DhcpRegistry, "接口", "注册表 EnableDHCP", $"={en1?.ToString() ?? "未知"} ≠ 0", fixable: true));

        // ── 12 网络类别 ──────────────────────────────────────────
        if (ad1.Category == "Private")
            snap.Items.Add(CheckItem.Pass(CheckKeys.PrivateCategory, "类型", "卡1 网络类别", "Private", fixable: true));
        else
            snap.Items.Add(CheckItem.Warn(CheckKeys.PrivateCategory, "类型", "卡1 网络类别",
                $"={ad1.Category ?? "未知"}",
                "Set-NetConnectionProfile -NetworkCategory Private", fixable: true));

        // ── 13/14/15 服务与驱动 ──────────────────────────────────
        var sa = NetProbe.ServiceState("SharedAccess");
        if (sa == "Running") snap.Items.Add(CheckItem.Pass(CheckKeys.SharedAccess, "服务", "SharedAccess 服务", "Running", fixable: true));
        else if (sa == "StopPending") snap.Items.Add(CheckItem.Fail(CheckKeys.SharedAccess, "服务", "SharedAccess 服务",
            "卡在 StopPending", "杀掉承载它的 svchost", fixable: true));
        else snap.Items.Add(CheckItem.Fail(CheckKeys.SharedAccess, "服务", "SharedAccess 服务", sa, "启动该服务", fixable: true));

        var nla = NetProbe.ServiceState("NlaSvc");
        snap.Items.Add(nla == "Running"
            ? CheckItem.Pass(CheckKeys.NlaSvc, "服务", "NlaSvc 服务", "Running", fixable: true)
            : CheckItem.Warn(CheckKeys.NlaSvc, "服务", "NlaSvc 服务", $"={nla}", "启动该服务", fixable: true));

        var ipnat = NetProbe.ServiceState("ipnat");
        snap.Items.Add(ipnat == "Running"
            ? CheckItem.Pass(CheckKeys.IpNat, "服务", "ipnat 驱动", "RUNNING")
            : CheckItem.Fail(CheckKeys.IpNat, "服务", "ipnat 驱动", $"={ipnat}", "ICS 共享会自动拉起", fixable: true));

        // ── 16/17 ICS 共享两端 ───────────────────────────────────
        using (var ics = new IcsShareManager())
        {
            if (!ics.Available)
            {
                snap.Items.Add(CheckItem.Fail(CheckKeys.IcsAvailable, "ICS", "ICS 共享状态", "COM 读取失败", ics.LastError));
            }
            else
            {
                var c1 = ics.Get(cfg.PrivateAdapter);
                if (c1 != null && c1.Enabled && c1.Type == 1)
                    snap.Items.Add(CheckItem.Pass(CheckKeys.IcsPrivate, "ICS", "ICS 私有端(卡1)", "En=True Type=1", fixable: true));
                else
                    snap.Items.Add(CheckItem.Fail(CheckKeys.IcsPrivate, "ICS", "ICS 私有端(卡1)",
                        c1 == null ? "未找到该连接" : $"En={c1.Enabled} Type={c1.Type}",
                        "须 En=True Type=1", fixable: true));

                var c2 = ics.Get(cfg.PublicAdapter);
                if (c2 != null && c2.Enabled && c2.Type == 0)
                    snap.Items.Add(CheckItem.Pass(CheckKeys.IcsPublic, "ICS", "ICS 公网端(卡2)", "En=True Type=0", fixable: true));
                else
                    snap.Items.Add(CheckItem.Fail(CheckKeys.IcsPublic, "ICS", "ICS 公网端(卡2)",
                        c2 == null ? "未找到该连接" : $"En={c2.Enabled} Type={c2.Type}",
                        "须 En=True Type=0", fixable: true));
            }
        }

        // ── 18 ICS DHCP 监听 ─────────────────────────────────────
        if (NetProbe.UdpListening(cfg.PrivateIp, 67))
            snap.Items.Add(CheckItem.Pass(CheckKeys.IcsDhcpListen, "ICS", "ICS DHCP 监听", $"{cfg.PrivateIp}:67", fixable: true));
        else
            snap.Items.Add(CheckItem.Fail(CheckKeys.IcsDhcpListen, "ICS", "ICS DHCP 监听",
                $"未见 {cfg.PrivateIp}:67", "下游设备拿不到 IP", fixable: true));

        // ── 18b 网段内第二台 DHCP（只在抓包看见时才出现）────────────
        // 有它在，设备就会在两套地址池之间抢，表现是"反复掉网 / 一直正在获取 IP"。
        var foreignDhcp = SegmentWatch.ForeignDhcpServer(TimeSpan.FromMinutes(30));
        if (foreignDhcp != null)
            snap.Items.Add(CheckItem.Warn(CheckKeys.ForeignDhcp, "ICS", "网段内第二台 DHCP",
                $"{foreignDhcp} 在发地址", "它会和 ICS 抢客户端，去那台设备上关掉 DHCP", fixable: false));

        // ── 18c 有设备在要地址却拿不到 ──────────────────────────────
        // 这一项必须**一直在**（不只是卡住时才出现）：复检按 Key 找项，
        // 项不在就会被判成"修完还是找不到"，反而制造假失败。
        var stuck = Monitor.DhcpWatch.StuckClients();
        if (stuck.Count > 0)
            snap.Items.Add(CheckItem.Fail(CheckKeys.IcsDhcpStuck, "ICS", "设备获取地址",
                $"{string.Join("、", stuck.Count > 3 ? stuck.GetRange(0, 3) : stuck)} 反复请求无应答",
                "重开共享（重置分配器）", fixable: true));
        else if (Monitor.DhcpWatch.Suppressed)
            snap.Items.Add(CheckItem.Pass(CheckKeys.IcsDhcpStuck, "ICS", "设备获取地址", "刚重置，观察中"));
        else
            snap.Items.Add(CheckItem.Pass(CheckKeys.IcsDhcpStuck, "ICS", "设备获取地址", "正常"));

        // ── 19 NAT 出口实测（绑定源地址，等价 ping -S）───────────
        // 只在"两端都有链路"时才测：卡1 断开时源地址 192.168.137.1 根本发不出去，
        // 测出来必然失败 → 会被当成可修故障反复重试（点断电/拔网线后就是这种噪音）。
        if (ad2.HasGateway && ad1.IsUp)
        {
            // 【为什么要重试】绑定源地址的原始 ICMP 偶发丢一个包（ICS 的 NAT 会话表在扩缩时，
            // 回包可能没被翻译回来）。日志里 09:30~09:48 反复出现「故障 → 3 秒后全部正常」，
            // 一晚上十几轮，全是这种假警报 —— 它还会顺带把"每年 1 次"的自动维修额度烧掉。
            // 现在连发 3 次，任何一次回来就算通。
            var ok = false;
            var tries = 0;
            for (var i = 0; i < 3 && !ok; i++)
            {
                if (i > 0) Thread.Sleep(250);
                tries++;
                ok = IcmpProbe.PingFrom(cfg.PrivateIp, ad2.Gateway, 900);
            }

            var retryNote = tries > 1 ? $"（第 {tries} 次才通）" : "";
            if (ok)
            {
                if (tries > 1) AppLog.Debug($"NAT 出口实测第 {tries} 次成功 —— 前一两次丢包");
                snap.Items.Add(CheckItem.Pass(CheckKeys.NatEgress, "NAT", "NAT 出口实测",
                    $"源 {cfg.PrivateIp} → {ad2.Gateway} 有回复{retryNote}", "", fixable: true));
            }
            else if (!IcmpProbe.PingPlain(ad2.Gateway, 1200))
            {
                // 本机自己都 ping 不通上游网关 → 是**上游断了**（校园网到点断电 / 未认证），
                // 不是 NAT 转发的问题。标成不可修，避免每小时做无意义的自动维修重试。
                snap.Items.Add(CheckItem.Warn(CheckKeys.NatEgress, "NAT", "NAT 出口实测",
                    $"上游 {ad2.Gateway} 不通", "本机也 ping 不通网关，等上游恢复", fixable: false));
            }
            else
            {
                snap.Items.Add(CheckItem.Fail(CheckKeys.NatEgress, "NAT", "NAT 出口实测",
                    $"源 {cfg.PrivateIp} → {ad2.Gateway} 连续 3 次无回复", "转发异常", fixable: true));
            }
        }

        // ── 20 下游设备 ──────────────────────────────────────────
        // 【注意这里不是"租约"】数据源是 ARP 邻居表，不是 ICS 的 DHCP 租约库。
        // 同一个设备换过 IP 会留下多条记录（实测 4 个地址共用一个 MAC，
        // 全是 static、全都不响应），按条数报"5 个租约"纯属吓人。
        // 现在：按 MAC 去重、只把真正应答的算"在线设备"，一条都没有就老实说没有。
        var prefix = string.Join(".", cfg.PrivateIp.Split('.').Take(3));
        var devs = NetProbe.GetDevices(cfg.PrivateAdapter, prefix + ".");
        if (devs.Count > 0)
        {
            // 并行探测 —— 串行会拖垮体检周期（6 台 × 1.2s = 7s > 5s 周期）
            // 超时给足 1200ms：手机 WiFi 省电状态下对 600ms 的 ICMP 常常不应答
            var tasks = devs.Select(d => Task.Run(() => d.Online = IcmpProbe.PingPlain(d.Ip, 1200))).ToArray();
            try { Task.WaitAll(tasks, 3000); } catch (Exception __ex) { AppLog.Swallow("Checker.cs:217", __ex); }

            // 【ping 不通 ≠ 离线】手机休眠时对 ICMP 100% 不应答，但流量还在跑。
            // 所以"在线"改成：ping 得通 **或** 最近 2 分钟有过流量（账本里有记录）。
            foreach (var d in devs)
                if (!d.Online && Monitor.TrafficLedger.Current?.HostActiveWithin(d.Ip, 120) == true)
                    d.Online = true;

            var live = devs.Where(d => d.Online)
                           .GroupBy(d => d.Mac, StringComparer.OrdinalIgnoreCase)
                           .Select(g => g.First())
                           .Select(d => d.Ip + (d.IsRandomMac ? "(手机?)" : ""))
                           .ToList();
            if (live.Count > 0)
            {
                // 只报"谁在线"。ARP 里的历史残渣条数不可操作，不占界面
                snap.Items.Add(CheckItem.Pass(CheckKeys.Downstream, "设备", "ICS 客户端",
                    $"{live.Count} 台在线: {string.Join(", ", live)}"));
            }
            else
            {
                // 一台都不在线是**正常状态**（没人连 WiFi 而已），不该报故障
                snap.Items.Add(CheckItem.Pass(CheckKeys.Downstream, "设备", "ICS 客户端",
                    "在线 0 台"));
            }

            // 僵尸租约：ping 不通 + 最近 10 分钟没流量 + 不是本机。
            // 只报数（INFO），不算故障 —— 但条目越攒越多会耗尽地址池，所以给个可点的提示。
            var zombie = devs.Count(d => !d.Online && d.Ip != cfg.PrivateIp);
            if (zombie > 0)
                snap.Items.Add(CheckItem.Info(CheckKeys.ZombieLease, "设备", "僵尸租约",
                    $"{zombie} 条（无响应且无流量）", "一键维修会清理"));
            else
                snap.Items.Add(CheckItem.Pass(CheckKeys.ZombieLease, "设备", "僵尸租约", "无"));
        }
        else
        {
            snap.Items.Add(CheckItem.Pass(CheckKeys.Downstream, "设备", "ICS 客户端", "未发现下游设备"));
        }

        // ── 21/22 计划任务 ───────────────────────────────────────
        // 只把"本次开机之后 AND 3 小时以内"的失败当实时告警；
        // 更早的失败属历史记录（如旧脚本留下的 LastTaskResult=1）。
        var lastBoot = DateTime.Now.AddMilliseconds(-Environment.TickCount64);
        var recentWindow = TimeSpan.FromHours(3);
        // 任务名以"动作指向本 exe"为准解析，配置里的名字只作为线索
        // （现场配置还写着 PowerShell 时代的 ICS Boot Fix / ICS Net Fix Watch，两者都 Disabled，
        //   真正在跑的 IcsWatch / IcsWatch Watchdog 反而没被监测）
        var mainTask = TaskWatcher.Resolve(cfg.BootFixTaskName, watchdog: false);
        var watchTask = TaskWatcher.Resolve(cfg.WatchTaskName, watchdog: true);
        foreach (var (key, tn) in new[] { (CheckKeys.TaskBootFix, mainTask),
                                          (CheckKeys.TaskWatch,   watchTask) })
        {
            var ts = TaskWatcher.Get(tn);
            // 分组列已经写着「任务」，名字前面再挂一遍"计划任务"只会把列挤爆
            var label = tn;

            if (!ts.Found)
                snap.Items.Add(CheckItem.Info(key, "任务", label, "未部署"));
            else if (string.Equals(ts.State, "Disabled", StringComparison.OrdinalIgnoreCase))
                snap.Items.Add(CheckItem.Info(key, "任务", label, "Disabled"));
            else if (ts.LastResult == 0)
                snap.Items.Add(CheckItem.Pass(key, "任务", label, "LastResult=0"));
            // 0x41301：任务正在运行。看门狗每 2 分钟拉起一次，跑的这几秒里
            // LastResult 就是它，落到下面的失败分支就会每 2 分钟闪一次"降级"。
            else if (ts.LastResult == 267009 || string.Equals(ts.State, "Running", StringComparison.OrdinalIgnoreCase))
                snap.Items.Add(CheckItem.Pass(key, "任务", label, "运行中"));
            else if (ts.LastResult == 267011)
                snap.Items.Add(CheckItem.Info(key, "任务", label, "从未运行过"));
            else if (ts.LastRun == null || ts.LastRun.Value < lastBoot)
                snap.Items.Add(CheckItem.Info(key, "任务", label,
                    $"上次失败在本次开机前 {ts.LastRun:MM-dd HH:mm}"));
            else if (DateTime.Now - ts.LastRun.Value > recentWindow)
                snap.Items.Add(CheckItem.Info(key, "任务", label,
                    $"上次失败于 {ts.LastRun:MM-dd HH:mm}，{(DateTime.Now - ts.LastRun.Value).TotalHours:0} 小时前"));
            else
                snap.Items.Add(CheckItem.Warn(key, "任务", label,
                    $"本次开机后失败 LastResult={ts.LastResult}", "查看该任务日志"));
        }

        // ── 汇总状态 ─────────────────────────────────────────────
        snap.State = DeriveState(snap);

        // ── 游戏模式联动 ─────────────────────────────────────────
        // 游戏模式下卡1/卡2/ICS/NAT 是**故意停掉**的，不能报成故障 ——
        // 否则总览一片红、检查项全是 FAIL，看起来像坏了。
        if (cfg.GameModeActive) ApplyGameModeView(cfg, snap);
        // ── 私有端无链路时降噪 ───────────────────────────────────
        // 卡1 一旦 Down（路由器断电 / 网线没插），主地址/从属IP/度量/类别/ICS/转发
        // **全都设不了**，标成"可修"只会让自动维修每半小时白跑一次（日志实测刷屏）。
        else if (!ad1.Exists || !ad1.IsUp) DowngradeWhenPrivateDown(snap, cfg.PrivateAdapter);

        // 公网端网卡整个不在（拔了 / USB 供电断了）→ 地址、默认路由、度量都无从判断，
        // 留着只会刷"读取失败"并触发无效重试
        if (!ad2.Exists && !cfg.GameModeActive) DowngradeWhenPublicAbsent(snap, cfg.PublicAdapter);

        return snap;
    }

    /// <summary>
    /// 游戏模式视图：把"故意停掉"的项从 WARN/FAIL 改成 INFO「游戏模式 · 已暂停」，
    /// 并追加一条「游戏出口」说明当前真正在用的链路；整体状态置为 GameMode。
    /// </summary>
    private static void ApplyGameModeView(AppConfig cfg, CheckSnapshot snap)
    {
        for (int i = 0; i < snap.Items.Count; i++)
        {
            var it = snap.Items[i];
            if (it.Status != CheckStatus.Warn && it.Status != CheckStatus.Fail) continue;
            if (!CheckKeys.PausedInGameMode.Contains(it.Key)) continue;
            // 换成新的 INFO 项：AutoFixable=false，顺带避免被自动维修盯上
            snap.Items[i] = CheckItem.Info(it.Key, it.Group, it.Name, "游戏模式 · 已暂停", "退出游戏模式后自动恢复");
        }

        var usb = (cfg.GameUsbAdapter ?? "").Trim();
        if (usb.Length > 0)
        {
            var ad = NetProbe.ReadAdapter(usb);
            var ip = ad.Ipv4.FirstOrDefault() ?? "无地址";
            snap.Items.Insert(0, CheckItem.Info(CheckKeys.GameEgress, "游戏模式", "游戏出口",
                $"{usb} · {ip}", "只保留 USB 共享出网"));
        }

        snap.State = HealthState.GameMode;
    }

    /// <summary>
    /// 私有端（卡1）没有链路时的降噪：把依赖它的项从 WARN/FAIL 改成 INFO「跳过」，
    /// 并且换成 AutoFixable=false —— 否则自动维修会一直尝试去设置一个已断开的网卡。
    /// 「卡1 链路」本身保留（那是真实告警，提醒用户路由器断电/网线没插）。
    /// </summary>
    private static void DowngradeWhenPrivateDown(CheckSnapshot snap, string privateName)
    {
        for (int i = 0; i < snap.Items.Count; i++)
        {
            var it = snap.Items[i];
            if (it.Key == CheckKeys.PrivateLink) continue;
            if (it.Status != CheckStatus.Warn && it.Status != CheckStatus.Fail) continue;
            if (!CheckKeys.PrivateDependent.Contains(it.Key)) continue;
            snap.Items[i] = CheckItem.Info(it.Key, it.Group, it.Name, "跳过 · 卡1 无链路",
                $"下游线路 {privateName} 未连接（路由器断电 / 网线未插）");
        }
    }

    /// <summary>
    /// 公网端网卡整个不在时的降噪（拔了网卡 / USB 供电断了）：
    /// 地址、默认路由、度量都无从判断，留着只会刷"读取失败"并触发无效重试。
    /// 保留「卡2[X] 存在：未找到该网卡」这一条 —— 那正是"网卡不在"的关键告警。
    /// </summary>
    private static void DowngradeWhenPublicAbsent(CheckSnapshot snap, string publicName)
    {
        for (int i = 0; i < snap.Items.Count; i++)
        {
            var it = snap.Items[i];
            if (it.Status != CheckStatus.Warn && it.Status != CheckStatus.Fail) continue;
            if (!CheckKeys.PublicDependent.Contains(it.Key)) continue;
            snap.Items[i] = CheckItem.Info(it.Key, it.Group, it.Name, "跳过 · 卡2 未接入",
                $"上游网卡 {publicName} 不在（未插 / 断电），接入后自动恢复");
        }
    }

    public static HealthState DeriveState(CheckSnapshot snap)
    {
        if (snap.Count(CheckStatus.Fail) > 0) return HealthState.Broken;
        if (snap.Count(CheckStatus.Warn) > 0) return HealthState.Degraded;
        return HealthState.Healthy;
    }

    /// <summary>状态文案（只有 WARN 时说"有警告"，不说"功能受损"）</summary>
    public static string StateText(HealthState s) => s switch
    {
        HealthState.Healthy => "全部正常",
        HealthState.Degraded => "降级",
        HealthState.Broken => "故障",
        HealthState.Repairing => "正在维修…",
        HealthState.Cooling => "冷却中",
        HealthState.GameMode => "游戏模式",
        _ => "未知",
    };
}
