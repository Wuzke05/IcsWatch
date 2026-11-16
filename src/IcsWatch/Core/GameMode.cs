using System.Net.NetworkInformation;
using IcsWatch.Config;

namespace IcsWatch.Core;

/// <summary>
/// 游戏模式：只保留手机 USB 共享出网，其余网卡**只改路由度量**（不 disable、不 release）。
///
/// 【为什么需要】校园网抖动大，打游戏时要手动拔两根网线太麻烦；而且只要多个出口
/// 同时存在，Windows 就会按度量/路由在它们之间挑，容易出现丢包、短时断流。
///
/// 【实现变更留档】早期版本是"禁用网卡"（逻辑拔线）。2026-10-23 因此把 USB 网卡
/// 弄掉了 USB 总线（ERROR_NO_MORE_ITEMS，扫都扫不回来，只能物理拔插）。
/// 现在只调度量：度量是路由优先级，内核毫秒级生效，链路和 DHCP 租约都不动，
/// 进入 2~3 秒、退出 1~2 秒，PC 几乎无感。
/// 完整"禁用/启用"的代码（SetEnabled）只保留给**历史遗留状态**的恢复使用
/// （退出游戏模式 / 开机自检 HealOrphanDisabled），新流程不会再主动禁用任何网卡。
///
/// 【安全约束】
///  ① 永不 disable/enable USB 共享网卡（含手机 RNDIS）；永不碰虚拟网卡
///  ② 进入前各网卡的度量记进 config.GameModifiedMetrics，退出时按记录精确还原
///  ③ 退出时先走"快路径"（关键 6 项复检），不过再回退全量重建
/// </summary>
public static class GameMode
{
    private const string NewLine = "\r\n";

    // ── 网卡枚举与特征判断 ──────────────────────────────────────

    /// <summary>
    /// 可被「暂停」的候选网卡：**只列物理以太网**。
    /// 无线网卡（电脑自带 WLAN）一律不列 —— 它跟 ICS 无关，
    /// 而且用户跑游戏模式时还要靠它，绝不能动。
    /// </summary>
    public static List<string> Candidates()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetProbe.Interfaces())
            {
                var t = ni.NetworkInterfaceType;
                if (t != NetworkInterfaceType.Ethernet &&
                    t != NetworkInterfaceType.GigabitEthernet) continue;
                if (IsVirtual(ni.Description ?? "", ni.Name)) continue;
                if (!list.Contains(ni.Name)) list.Add(ni.Name);
            }
        }
        catch (Exception __ex) { AppLog.Swallow("GameMode.cs:44", __ex); }
        return list;
    }

    /// <summary>虚拟 / 蓝牙 / 隧道类网卡 —— 绝不参与禁停</summary>
    public static bool IsVirtual(string desc, string name)
    {
        string[] bad = { "VMware", "Virtual", "Bluetooth", "vEthernet", "TAP-", "Loopback", "Hyper-V", "Npcap" };
        foreach (var b in bad)
        {
            if (desc.Contains(b, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains(b, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 手机 USB 共享的网卡特征。注意别用裸 "USB" 判断 ——
    /// 「Realtek USB GbE」是 USB 网卡转换器（也就是卡2 校园网），不是手机共享。
    /// </summary>
    public static bool IsUsbTethering(string desc)
    {
        string[] keys = { "Remote NDIS", "RNDIS", "Apple Mobile Device", "Internet Sharing", "iPhone", "Android" };
        foreach (var k in keys)
            if (desc.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>自动猜一个 USB 共享网卡（猜不到返回空串）</summary>
    public static string GuessUsbAdapter()
    {
        try
        {
            string fallback = "";
            foreach (var ni in NetProbe.Interfaces())
            {
                var desc = ni.Description ?? "";
                if (!IsUsbTethering(desc)) continue;
                if (IsVirtual(desc, ni.Name)) continue;        // 排除 VMware / Hyper-V / 蓝牙
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                // 有有效 IPv4（非 169.254 APIPA）才算真的在用
                var hasReal = false;
                try
                {
                    hasReal = ni.GetIPProperties().UnicastAddresses.Any(a =>
                        a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !a.Address.ToString().StartsWith("169.254."));
                }
                catch (Exception ex) { AppLog.Swallow("GameMode.GuessUsbAdapter", ex); }

                if (hasReal) return ni.Name;
                if (fallback.Length == 0) fallback = ni.Name;
            }
            return fallback;
        }
        catch (Exception __ex) { AppLog.Swallow("GameMode.cs:80", __ex); }
        return "";
    }

    private static NetworkInterface? FindNi(string name)
    {
        try { return NetProbe.Interfaces().FirstOrDefault(n => n.Name == name); }
        catch { return null; }
    }

    // ── 启用 / 禁用网卡 ────────────────────────────────────────

    /// <summary>
    /// 只改路由度量（**不 disable、不 release**）—— 无感切换的核心手段。
    /// 度量只是路由优先级，内核毫秒级生效，链路和 DHCP 租约都不受影响；
    /// 也不会碰 USB 网卡导致掉出总线（Bug 15）。
    /// </summary>
    private static bool SetMetric(string name, int metric, Action<string> log)
    {
        var cur = NetProbe.GetInterfaceMetric(name);
        if (cur == metric)
        {
            log($"{name} 度量已是 {metric}");
            return true;
        }

        ProcessRunner.Run("netsh.exe", $"interface ipv4 set interface \"{name}\" metric={metric}", 15_000);
        Thread.Sleep(400);

        var now = NetProbe.GetInterfaceMetric(name);
        if (now == metric)
        {
            var txt = $"度量 {name}: {cur?.ToString() ?? "?"} -> {metric}";
            log(txt);
            AppLog.Info("[游戏模式] " + txt);
            return true;
        }

        var fail = $"设置 {name} 度量失败（当前 {now?.ToString() ?? "未知"}）";
        log(fail);
        AppLog.Warn("[游戏模式] " + fail);
        return false;
    }

    /// <summary>
    /// 确认公网端有有效 IPv4。有 → 秒级返回；没有 → 温和 renew 一次再轮询最多 6 秒。
    /// 用轮询代替固定 sleep，健康时能省掉 2~3 秒。
    /// </summary>
    private static bool EnsurePublicAddress(AppConfig cfg, Action<string> log)
    {
        if (HasValidV4(cfg.PublicAdapter))
        {
            log($"公网端 {cfg.PublicAdapter} 地址正常");
            return true;
        }

        log($"公网端 {cfg.PublicAdapter} 暂无地址，renew 一次…");
        ProcessRunner.Run("ipconfig.exe", $"/renew \"{cfg.PublicAdapter}\"", 30_000);

        var deadline = DateTime.Now.AddSeconds(6);
        while (DateTime.Now < deadline)
        {
            Thread.Sleep(500);
            if (HasValidV4(cfg.PublicAdapter))
            {
                log($"公网端 {cfg.PublicAdapter} 已拿到地址");
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 让 USB 网卡"逻辑下线"（**不 disable**）：释放 DHCP 地址 + 度量压到最低。
    /// 释放后没有 IPv4 地址 → 不承载流量、不抢默认路由；设备仍在 USB 总线上，随时可恢复。
    /// 【为什么不 disable】USB 网卡被禁用后可能整台设备掉出 USB 总线，
    /// 之后 `netsh admin=enabled` 报 ERROR_NO_MORE_ITEMS，只能物理拔插（实测）。
    ///
    /// 【已停用 · 保留作参考】现在进入游戏模式只用 SetMetric 调度量（更轻、更安全），
    /// 不再 release 地址 —— release 会让 USB 网卡短暂失去地址，退出时又要重新等租约。
    /// 这段代码留在这里是因为它记录了"为什么不能禁用 USB 网卡"这个结论。
    /// </summary>
    private static bool ReleaseAdapter(string name, Action<string> log)
    {
        var r = ProcessRunner.Run("ipconfig.exe", $"/release \"{name}\"", 20_000);
        ProcessRunner.Run("netsh.exe", $"interface ipv4 set interface \"{name}\" metric=9999", 15_000);
        Thread.Sleep(700);

        var metric = NetProbe.GetInterfaceMetric(name);
        if (metric == 9999)
        {
            var note = r.Ok ? "" : "（release 未成功，靠度量降权）";
            log($"已暂停 {name}：USB 网卡已释放地址{note}");
            AppLog.Info($"[游戏模式] 已暂停 {name}（USB 软暂停，release={(r.Ok ? "ok" : "fail")}，度量=9999）");
            return true;
        }
        log($"暂停 {name} 未生效：度量={metric?.ToString() ?? "未知"}");
        return false;
    }

    /// <summary>启用/禁用网卡（禁用 = 逻辑拔线）。结果会落盘，便于追查"谁动了网卡"。</summary>
    public static bool SetEnabled(string name, bool enable, Action<string> log)
    {
        void Emit(string m) { AppLog.Info("[游戏模式] " + m); log(m); }

        var r = ProcessRunner.Run("netsh.exe",
            $"interface set interface name=\"{name}\" admin={(enable ? "enabled" : "disabled")}", 25_000);
        Thread.Sleep(700);
        if (!r.Ok)
        {
            Emit($"{(enable ? "启用" : "禁用")} {name} 失败: {TextUtil.Trunc(r.All, 140)}");
            return false;
        }

        // 用注册表的管理状态复核（OperationalStatus 不可靠，见 NetProbe.IsAdminDisabled 注释）
        var disabled = NetProbe.IsAdminDisabled(name);
        if (enable ? disabled == true : disabled != true)
        {
            var st = disabled == null ? "未知" : (disabled.Value ? "Disabled" : "Enabled");
            Emit($"{(enable ? "启用" : "禁用")} {name} 未生效（管理状态={st}）");
            return false;
        }
        Emit($"已{(enable ? "启用" : "禁用")} {name}");
        return true;
    }

    // ── 进入 / 退出 ────────────────────────────────────────────

    /// <summary>进入游戏模式：只留 USB 出网</summary>
    public static (bool ok, string msg) Enter(AppConfig cfg, Action<string> log)
    {
        AppLog.Info("[游戏模式] ===== 进入游戏模式开始 =====");
        var usb = (cfg.GameUsbAdapter ?? "").Trim();
        if (usb.Length == 0) usb = GuessUsbAdapter();
        if (usb.Length == 0)
            return (false, "没识别到 USB 共享网卡 —— 先在手机上打开「USB 网络共享」再试");

        // 【护栏】虚拟网卡绝不能当出口：实测猜错过一次，把 "VMware Network Adapter VMnet8"
        // 当成了 USB 共享网卡 → 报"仅 VMnet8 出网"，而它根本不通外网，等于把出口全关了。
        if (IsVirtual(FindNi(usb)?.Description ?? "", usb))
            return (false, $"「{usb}」是虚拟网卡，不能作为游戏模式出口 —— 请先在界面选择手机 USB 共享网卡");

        var adUsb = NetProbe.ReadAdapter(usb);
        if (!adUsb.Exists) return (false, $"找不到网卡「{usb}」");
        if (!adUsb.IsUp) return (false, $"「{usb}」没连上 —— 先在手机上打开 USB 网络共享");

        cfg.GameUsbAdapter = usb;

        // ⓪ 【先验证 USB 真能出网，再动别的网卡】
        //    否则会出现最坏情况：校园网已经关了，USB 又没网 —— 这台机器就没网了。
        //    注意：手机共享用的是 RNDIS 网卡，**绝不能禁用/启用它** ——
        //    Android 会因此终止 USB 共享会话（实测：一禁用，共享直接断，网卡变无地址）。
        if (!WaitForValidLease(usb, log))
            return (false, $"「{usb}」没有有效 IP —— 手机 USB 共享还没就绪，未做任何改动。请重新打开手机 USB 网络共享后再试");

        // 【第一步就落盘置位，再做破坏性动作】
        // 后面"关 ICS 共享 / 禁用网卡"会让 GUI 的体检看到一堆 FAIL。如果标志等到最后才写，
        // GUI 在过渡期（实测约 15 秒）仍拿旧值 false → 抢着自动维修，跟游戏模式对打
        // （日志实证：进入时各触发一次无效维修，"未修好: 卡1 主地址 / ICS 私有端…"）。
        cfg.GameModeActive = true;
        cfg.Save();

        // ① 先关 ICS 共享，避免私有端还在转发造成双出口
        //    轻量化模式（无感优先）下**不关 ICS**：
        //    · 关共享要走两次 COM（每端约 2 秒）→ 进入慢、退出还得再开回来
        //    · 用户不关心下游 → 让共享继续存在没有副作用，PC 自己由度量决定走 USB
        //    结果：进入/退出都只剩毫秒级的路由切换，真正"无感"。
        if (cfg.GameModeLite)
        {
            log("轻量化：保留 ICS 共享（仅改路由度量）");
        }
        else
        try
        {
            using var ics = new IcsShareManager();
            if (ics.Available)
            {
                ics.Disable(cfg.PrivateAdapter);
                ics.Disable(cfg.PublicAdapter);
                log("已关闭 ICS 共享");
            }
            else log("ICS COM 不可用，跳过共享关闭: " + ics.LastError);
        }
        catch (Exception ex) { log("关闭 ICS 共享失败: " + ex.Message); }

        // ② 逐块禁用。
        // 【重要】ICS 两端（卡1 = 分享出去的 WiFi、卡2 = 校园网）**永远强制暂停** ——
        // 游戏模式的定义就是"只留 USB 一路"，不能因为界面勾选漏了就把校园网留着
        // （校园网抖动正是要绕开的东西）。勾选框只用于"额外再加"。
        var mustPause = new List<string>();
        foreach (var n in new[] { cfg.PrivateAdapter, cfg.PublicAdapter })
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            if (string.Equals(n, usb, StringComparison.OrdinalIgnoreCase)) continue;
            if (!mustPause.Contains(n)) mustPause.Add(n);
        }
        foreach (var n in cfg.GamePauseAdapters ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(n) && !mustPause.Contains(n)) mustPause.Add(n);

        var done = new List<string>();
        var skipped = new List<string>();
        var disabledOnes = new List<string>();     // 真正处于"禁用"状态的（退出时需启用）
        cfg.GameModifiedMetrics ??= new Dictionary<string, int>();

        foreach (var name in mustPause)
        {
            if (string.Equals(name, usb, StringComparison.OrdinalIgnoreCase)) { skipped.Add($"{name}(USB)"); continue; }

            // 已经是禁用状态（上个版本或手工造成）→ 记下来，退出时恢复
            if (NetProbe.IsAdminDisabled(name) == true)
            {
                done.Add(name); disabledOnes.Add(name); skipped.Add($"{name}(已禁用)");
                continue;
            }

            var ni = FindNi(name);
            if (ni != null && IsVirtual(ni.Description ?? "", ni.Name)) { skipped.Add($"{name}(虚拟)"); continue; }

            // ★只改度量：不 disable、不 release。
            //   度量只是路由优先级 → 内核毫秒级生效，链路和 DHCP 租约都不动，切换几乎无感；
            //   而且完全不碰 USB 网卡，彻底消除"禁用后掉出总线"的风险（Bug 15）。
            var cur = NetProbe.GetInterfaceMetric(name);
            if (cur != null && !cfg.GameModifiedMetrics.ContainsKey(name))
                cfg.GameModifiedMetrics[name] = cur.Value;

            if (SetMetric(name, 9999, log)) { done.Add(name); }
        }

        cfg.GameDisabledAdapters = disabledOnes;   // 只有真被禁用的才需要"启用"恢复
        cfg.GameModeActive = true;
        cfg.Save();

        // ③ 让 USB 成为最优先出口
        ProcessRunner.Run("netsh.exe",
            $"interface ipv4 set interface \"{usb}\" metric={cfg.GameUsbMetric}", 15_000);
        Thread.Sleep(800);

        // ④ 复核：确认真的下线了
        //    已禁用的看管理状态，只改度量的看度量是否为 9999
        var stillUp = done.Where(n => NetProbe.IsAdminDisabled(n) == true
            ? false
            : NetProbe.GetInterfaceMetric(n) != 9999).ToList();

        var msg = $"游戏模式已开启：仅 {usb} 出网";
        if (done.Count > 0) msg += $"；已暂停 {string.Join("、", done)}";
        if (skipped.Count > 0) msg += $"；跳过 {string.Join("、", skipped)}";
        if (stillUp.Count > 0) msg += $"　⚠ 未能禁用: {string.Join("、", stillUp)}";
        if (done.Count == 0) msg += "　⚠ 没有禁用任何网卡，请在上面勾选要暂停的网卡";

        log(msg);
        return (done.Count > 0, msg);
    }

    /// <summary>
    /// 退出游戏模式：恢复度量 → 确认公网端有地址 → 快路径只重启 ICS → 关键项复检。
    /// 任何一环不过 → 自动回退到全量重建（安全网）。
    /// </summary>
    public static (bool ok, string msg) Exit(AppConfig cfg, Action<string> log)
    {
        AppLog.Info("[游戏模式] ===== 退出游戏模式开始 =====");

        // ── ① 收集要恢复的东西 ──────────────────────────────────
        var restoreDisabled = (cfg.GameDisabledAdapters ?? new List<string>()).ToList();
        // 补上"实际被禁用但没被记录"的网卡（记录丢失 / 上次异常中断）
        foreach (var n in new[] { cfg.PrivateAdapter, cfg.PublicAdapter })
        {
            if (string.IsNullOrWhiteSpace(n) || restoreDisabled.Contains(n)) continue;
            if (NetProbe.IsAdminDisabled(n) == true) restoreDisabled.Add(n);
        }
        var restoreMetrics = new Dictionary<string, int>(cfg.GameModifiedMetrics ?? new Dictionary<string, int>());

        cfg.GameDisabledAdapters = new List<string>();
        cfg.GameModifiedMetrics = new Dictionary<string, int>();
        cfg.GameModeActive = false;
        cfg.Save();

        // ── ② 先还原度量（毫秒级生效，出口优先级立刻理顺）────────
        //     顺序很重要：先处理 USB 网卡（它可能被临时提权/顶替），
        //     再按"进入前记录"精确覆盖一遍，保证公网端一定回到 10 这类原值。

        // USB 网卡必须回到"不抢路由"；若 USB 恰好就是公网端，则按公网端度量来
        var usb = (cfg.GameUsbAdapter ?? "").Trim();
        if (usb.Length > 0)
        {
            var want = string.Equals(usb, cfg.PublicAdapter, StringComparison.OrdinalIgnoreCase)
                ? cfg.PublicMetric : cfg.GameUsbMetricNormal;
            SetMetric(usb, want, log);
        }

        // ── ②b 再按进入前的记录精确还原（记录优先，避免上面把公网端度量覆盖错）
        foreach (var kv in restoreMetrics) SetMetric(kv.Key, kv.Value, log);

        // ── ③ 启用残留被禁用的网卡（上个版本或手工造成）────────────
        foreach (var name in restoreDisabled) SetEnabled(name, true, log);

        // ── ④ 公网端必须有地址；拿不到就直接全量重建兜底 ───────────
        if (!EnsurePublicAddress(cfg, log))
        {
            log("公网端拿不到地址 → 走全量重建兜底");
            var rec0 = Repairer.RunFull(cfg, "退出游戏模式（兜底）", log);
            var m0 = rec0.Success
                ? "已退出游戏模式，网络已恢复"
                : "已退出游戏模式，但有项未恢复: " + string.Join("、", rec0.Failed);
            return (rec0.Success, m0);
        }

        // ── ⑤ 快路径：只重启 ICS 共享 + 关键项复检 ────────────────
        if (cfg.GameFastSwitch)
        {
            // 轻量化模式下进入时没关 ICS → 这里也不用再开，直接复检即可（省 4~6 秒）
            if (!cfg.GameModeLite)
            {
            try
            {
                using var ics = new IcsShareManager();
                if (ics.Available)
                {
                    ics.Disable(cfg.PrivateAdapter);
                    ics.Disable(cfg.PublicAdapter);
                    ics.Enable(cfg.PublicAdapter, 0);    // 0 = 公网端
                    ics.Enable(cfg.PrivateAdapter, 1);   // 1 = 私有端
                    log("ICS 共享已切回（快路径）");
                }
                else log("ICS COM 不可用: " + ics.LastError);
            }
            catch (Exception ex) { log("快路径启用 ICS 异常: " + ex.Message); }
            }
            else log("轻量化：ICS 共享未动，直接复检");

            var bad = QuickVerify(cfg);
            if (bad.Count == 0)
            {
                log("快路径复检通过（ICS 双端 / DHCP 监听 / 卡1 地址 / NAT 出口）");
                return (true, "已退出游戏模式（快路径）: 网络已恢复");
            }
            log("快路径复检未通过: " + string.Join("、", bad) + " → 回退全量重建");
        }

        // ── ⑥ 兜底：全量重建 ──────────────────────────────────────
        var rec = Repairer.RunFull(cfg, "退出游戏模式", log);
        var msg = rec.Success
            ? "已退出游戏模式，网络已恢复"
            : "已退出游戏模式，但有项未恢复: " + string.Join("、", rec.Failed);
        return (rec.Success, msg);
    }

    /// <summary>
    /// 快路径的关键项复检：跑一次体检，只看"必须通过"的那几项。
    /// 返回未通过的项名（空 = 全部通过）。
    /// </summary>
    private static List<string> QuickVerify(AppConfig cfg)
    {
        // 按 Key 精确匹配（原来用 Name.Contains("ICS 私有端") 模糊匹配 —— 一旦项名改动，
        // 这里会静默变成"缺项"，而"缺项"又会让退出流程回退到全量重建，代价 20 秒）
        string[] must =
        {
            CheckKeys.IcsPrivate, CheckKeys.IcsPublic, CheckKeys.IcsDhcpListen,
            CheckKeys.PrivateIp, CheckKeys.PrivateAltIp, CheckKeys.NatEgress,
        };
        var bad = new List<string>();
        try
        {
            var snap = Checker.Run(cfg, fromEvent: false, trigger: "退出游戏模式复检");
            foreach (var key in must)
            {
                var it = snap.Find(key);
                if (it == null) { bad.Add($"未产出该项({key})"); continue; }
                if (it.Status != CheckStatus.Pass) bad.Add(it.Name);
            }
        }
        catch (Exception ex) { bad.Add("复检异常: " + ex.Message); }
        return bad;
    }


    /// <summary>
    /// 启动自检：配置里说"不在游戏模式"，但网卡实际处于"管理性禁用"状态。
    /// 这是上次游戏模式留下的残留 —— Windows 会把"设备已禁用"跨重启保留下来，
    /// 于是用户开机后发现网卡莫名被禁用。这里自动解除并写进日志。
    /// （游戏模式开着时不执行，那是正常状态。）
    /// </summary>
    public static void HealOrphanDisabled(AppConfig cfg, Action<string> log)
    {
        foreach (var name in new[] { cfg.PrivateAdapter, cfg.PublicAdapter })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (NetProbe.IsAdminDisabled(name) != true) continue;
            log($"检测到残留禁用: {name}（当前不在游戏模式），自动解除");
            SetEnabled(name, true, log);
        }
    }

    /// <summary>有有效 IPv4（排除 APIPA 169.254.x.x）</summary>
    private static bool HasValidV4(string adapter)
    {
        var ad = NetProbe.ReadAdapter(adapter);
        return ad.Ipv4.Count > 0 && !ad.Ipv4.Any(ip => ip.StartsWith("169.254."));
    }

    /// <summary>
    /// 等网卡拿到有效 DHCP 租约。
    /// 【只用温和手段】ipconfig /renew（带超时）+ 轮询；
    /// **绝不 disable/enable** —— 手机共享的 RNDIS 网卡一被禁用，共享会话就终止了。
    /// </summary>
    private static bool WaitForValidLease(string adapter, Action<string> log, int seconds = 18)
    {
        if (HasValidV4(adapter)) return true;

        // 【自愈】USB 共享网卡的 DHCP 开关可能被置成 Disabled（注册表 EnableDHCP=0）——
        // 那样它永远只能拿 APIPA，怎么 renew 都没用（实测踩到）。
        // 用注册表改回 1 + netsh 切回 DHCP：不重启适配器，所以不会断开手机共享。
        var ni = FindNi(adapter);
        if (ni != null && NetProbe.GetEnableDhcpReg(ni.Id) == 0)
        {
            log($"{adapter} 的 DHCP 开关被关掉了（EnableDHCP=0），正在改回 DHCP…");
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + ni.Id, writable: true);
                k?.SetValue("EnableDHCP", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch (Exception ex) { log("写注册表失败: " + ex.Message); }
            ProcessRunner.Run("netsh.exe", $"interface ipv4 set address name=\"{adapter}\" source=dhcp", 15_000);
            Thread.Sleep(3000);
            if (HasValidV4(adapter))
            {
                var got = NetProbe.ReadAdapter(adapter);
                log($"{adapter} 已拿到地址: {string.Join(", ", got.Ipv4)}");
                return true;
            }
        }
        try { ProcessRunner.Run("ipconfig.exe", $"/renew \"{adapter}\"", 12_000); } catch (Exception __ex) { AppLog.Swallow("GameMode.cs:318", __ex); }

        var deadline = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < deadline)
        {
            Thread.Sleep(1500);
            if (!HasValidV4(adapter)) continue;
            var now = NetProbe.ReadAdapter(adapter);
            log($"{adapter} 已拿到地址: {string.Join(", ", now.Ipv4)}");
            return true;
        }
        return false;
    }

    /// <summary>
    /// 确保公网端拿到有效 DHCP 租约。
    /// 禁用→启用网卡后租约常丢（停在 APIPA：Get-NetAdapter 显示 Up 但没网关、不通），
    /// 这时 `ipconfig /renew` 常挂住，**重启一次适配器强制重新发现**最可靠（实测有效）；
    /// 但如果它是 RNDIS（手机共享）就只等待、不重启，免得把共享会话弄断。
    ///
    /// 【已停用 · 保留作参考】退出流程现在走 EnsurePublicAddress()（温和 renew + 轮询），
    /// 不再重启适配器 —— 重启会短暂断链，与"无感切换"的目标冲突。
    /// </summary>
    private static void EnsurePublicLease(AppConfig cfg, Action<string> log)
    {
        var pub = cfg.PublicAdapter;
        if (string.IsNullOrWhiteSpace(pub)) return;
        if (HasValidV4(pub)) { log($"公网端 {pub} 地址正常"); return; }

        if (WaitForValidLease(pub, log, 8)) return;

        var ni = FindNi(pub);
        if (ni != null && IsUsbTethering(ni.Description ?? ""))
        {
            log($"公网端 {pub} 是手机共享网卡，不重启（避免断开共享）");
            return;
        }

        log($"重启 {pub} 强制重新发现 DHCP…");
        ProcessRunner.Run("netsh.exe", $"interface set interface name=\"{pub}\" admin=disabled", 20_000);
        Thread.Sleep(1500);
        ProcessRunner.Run("netsh.exe", $"interface set interface name=\"{pub}\" admin=enabled", 20_000);

        if (WaitForValidLease(pub, log, 25)) return;
        log($"公网端 {pub} 仍无有效地址，请检查网线/校园网认证");
    }

    /// <summary>当前出口摘要（给界面显示用）</summary>
    public static string Describe(AppConfig cfg)
    {
        if (cfg.GameModeActive)
        {
            var usb = (cfg.GameUsbAdapter ?? "").Trim();
            var ad = NetProbe.ReadAdapter(usb);
            var ip = ad.Ipv4.Count > 0 ? string.Join(", ", ad.Ipv4) : "无地址";
            return $"{usb}（{ip}）" + NewLine +
                   $"已暂停: {(cfg.GameDisabledAdapters?.Count > 0 ? string.Join("、", cfg.GameDisabledAdapters) : "无")}";
        }

        var pub = NetProbe.ReadAdapter(cfg.PublicAdapter);
        var ip2 = pub.Ipv4.Count > 0 ? string.Join(", ", pub.Ipv4) : "无地址";
        return $"{cfg.PublicAdapter}（{ip2}）";
    }
}
