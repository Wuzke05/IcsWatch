namespace IcsWatch.Core;

/// <summary>
/// 检查项的**稳定标识**。
///
/// 【为什么需要这一层】原来程序内部靠"项名字符串"互相查找：
/// 体检产出「卡1 主地址」，维修就去 `FirstOrDefault(i => i.Name == "卡1 主地址")`
/// 找回来复检。于是**改一个中文字**（哪怕只是加个空格）就会静默失去复检能力 ——
/// 编译通过、运行不报错、结论却是假的。这类隐式契约散落在 Checker / Repairer /
/// GameMode / MainForm 四处，是这轮重构要消灭的东西。
///
/// 现在的约定：
///  · <see cref="CheckItem.Key"/> 只在程序内部使用，永不显示给用户；
///  · 展示给用户的是 <see cref="CheckItem.Name"/>（中文、可以随便改）；
///  · 任何"按项查找"一律用 Key，查找处若拿不到会**明确**报出来，不会静默变成 "—"。
///
/// Key 命名规则：`域.对象`，全小写 ASCII，用点分层。新增检查项时在这里加一条。
/// </summary>
public static class CheckKeys
{
    // ── 网卡链路（物理层）───────────────────────────────────────
    public const string PrivateLink = "nic.private.link";
    public const string PublicLink  = "nic.public.link";

    // ── 地址 ────────────────────────────────────────────────────
    public const string PrivateIp    = "addr.private.ip";
    public const string PrivateApipa = "addr.private.apipa";
    public const string PrivateAltIp = "addr.private.altip";
    public const string PublicIp     = "addr.public.ip";

    // ── 路由 ────────────────────────────────────────────────────
    public const string PrivateRoute = "route.private.gateway";
    public const string PublicRoute  = "route.public.gateway";

    // ── 接口参数（DHCP / 度量 / 类别）───────────────────────────
    public const string PrivateDhcp     = "if.private.dhcp";
    public const string PrivateMetric   = "if.private.metric";
    public const string PublicMetric    = "if.public.metric";
    public const string DhcpRegistry    = "if.private.reg.dhcp";
    public const string PrivateCategory = "if.private.category";

    // ── 服务与驱动 ──────────────────────────────────────────────
    public const string SharedAccess = "svc.sharedaccess";
    public const string NlaSvc       = "svc.nlasvc";
    public const string IpNat        = "svc.ipnat";

    // ── ICS 共享 ────────────────────────────────────────────────
    public const string IcsAvailable  = "ics.com";
    public const string ForeignDhcp   = "ics.foreign.dhcp";
    public const string IcsDhcpStuck  = "ics.dhcp.stuck";
    public const string IcsPrivate    = "ics.private";
    public const string IcsPublic     = "ics.public";
    public const string IcsDhcpListen = "ics.dhcplisten";

    // ── NAT 出口实测 ────────────────────────────────────────────
    public const string NatEgress = "nat.egress";

    // ── 下游设备 ────────────────────────────────────────────────
    public const string Downstream = "dev.downstream";

    /// <summary>ARP 表里 ICS 留下的永久条目（同一设备的历史地址），可清理。</summary>
    public const string ZombieLease = "dev.zombie";

    // ── 计划任务 ────────────────────────────────────────────────
    public const string TaskBootFix = "task.bootfix";
    public const string TaskWatch   = "task.watch";

    // ── 游戏模式（轻量体检）─────────────────────────────────────
    public const string GameEgress     = "game.egress";
    public const string GameEgressIp   = "game.egress.ip";
    public const string GameInternet   = "game.egress.internet";
    public const string GameWlan       = "game.wlan";
    public const string GamePublicRead = "game.public.readonly";

    /// <summary>游戏模式下"被我们主动停掉"的项 —— 这些项出 WARN/FAIL 要改写成 INFO「已暂停」。</summary>
    public static readonly string[] PausedInGameMode =
    {
        PrivateLink, PublicLink,
        PrivateIp, PrivateApipa, PrivateAltIp, PublicIp,
        PrivateRoute, PublicRoute,
        PrivateDhcp, PrivateMetric, PublicMetric, DhcpRegistry, PrivateCategory,
        SharedAccess, NlaSvc, IpNat,
        IcsAvailable, IcsPrivate, IcsPublic, IcsDhcpListen,
        NatEgress, Downstream,
    };

    /// <summary>依赖私有端（卡1）链路的项 —— 卡1 没链路时这些一律"跳过"。</summary>
    public static readonly string[] PrivateDependent =
    {
        PrivateIp, PrivateApipa, PrivateAltIp, PrivateRoute,
        PrivateDhcp, PrivateMetric, DhcpRegistry, PrivateCategory,
        IpNat,
        IcsPrivate, IcsPublic, IcsDhcpListen,
        NatEgress, Downstream,
    };

    /// <summary>依赖公网端（卡2）的项 —— 卡2 整块不在时这些一律"跳过"。</summary>
    public static readonly string[] PublicDependent =
    {
        PublicIp, PublicRoute, PublicMetric, IcsPublic,
    };
}
