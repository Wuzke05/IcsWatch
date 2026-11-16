namespace IcsWatch.Core;

/// <summary>单项检查结果状态（文案与 ics-healthcheck.ps1 保持一致）</summary>
public enum CheckStatus { Pass, Warn, Fail, Info }

/// <summary>整体健康状态</summary>
public enum HealthState
{
    Unknown,     // 尚未开始
    Healthy,     // 全部 PASS
    Degraded,    // 有 WARN（基本可用，但有隐患/降级项）
    Broken,      // 有 FAIL（网络实际不通）
    Repairing,   // 修复进行中
    Cooling,     // 修复后冷却期
    GameMode,    // 游戏模式（主动只保留 USB 共享出网，其余网卡被禁用）
}

/// <summary>一项检查</summary>
public sealed class CheckItem
{
    /// <summary>
    /// 稳定标识（见 <see cref="CheckKeys"/>）。程序内部按它查找，<see cref="Name"/> 只用于显示。
    /// 这是"体检项 ↔ 维修动作 ↔ 界面取值"之间唯一的契约。
    /// </summary>
    public string Key { get; init; } = "";

    public string Group { get; init; } = "";
    public string Name  { get; init; } = "";
    public CheckStatus Status { get; set; } = CheckStatus.Info;
    public string Detail { get; set; } = "";
    /// <summary>定位/修法提示，UI 上作为第二行小字显示</summary>
    public string Hint { get; init; } = "";

    /// <summary>
    /// 本项是否属于"程序自己能修好"的范畴。
    /// 自动维修的触发依据就是它 —— 只要存在 AutoFixable 且非 PASS 的项就尝试修，
    /// **不再依赖"状态跳变成故障"**（那个判定有致命缺陷：修失败一次后
    /// 状态回落成"降级"，就再也不会重试，异常会永远挂着）。
    /// 物理层/上游导致的问题（网线没插、校园网没认证）标记为 false，避免无意义反复修。
    /// </summary>
    public bool AutoFixable { get; init; }

    public static CheckItem Pass(string key, string g, string n, string d = "", string h = "", bool fixable = false)
        => new() { Key = key, Group = g, Name = n, Status = CheckStatus.Pass, Detail = d, Hint = h, AutoFixable = fixable };
    public static CheckItem Warn(string key, string g, string n, string d = "", string h = "", bool fixable = false)
        => new() { Key = key, Group = g, Name = n, Status = CheckStatus.Warn, Detail = d, Hint = h, AutoFixable = fixable };
    public static CheckItem Fail(string key, string g, string n, string d = "", string h = "", bool fixable = false)
        => new() { Key = key, Group = g, Name = n, Status = CheckStatus.Fail, Detail = d, Hint = h, AutoFixable = fixable };
    public static CheckItem Info(string key, string g, string n, string d = "", string h = "")
        => new() { Key = key, Group = g, Name = n, Status = CheckStatus.Info, Detail = d, Hint = h };
}

/// <summary>一次完整体检的快照</summary>
public sealed class CheckSnapshot
{
    public DateTime Time { get; init; } = DateTime.Now;
    public List<CheckItem> Items { get; init; } = new();
    public HealthState State { get; set; } = HealthState.Unknown;
    public bool FromEvent { get; init; }        // 是否由网络事件触发
    public string Trigger { get; init; } = "";  // 触发原因描述

    public int Count(CheckStatus s) => Items.Count(i => i.Status == s);
    public string Summary => $"{Count(CheckStatus.Pass)}/{Items.Count} PASS";

    /// <summary>按稳定标识取项（找不到返回 null）。**程序内部一律用这个，不要按 Name 找。**</summary>
    public CheckItem? Find(string key) => Items.FirstOrDefault(i => i.Key == key);

    /// <summary>当前"可自动修复但仍不正常"的项 —— 自动维修的判定依据</summary>
    public List<CheckItem> FixableProblems =>
        Items.Where(i => i.AutoFixable && i.Status != CheckStatus.Pass).ToList();

    /// <summary>全部异常项（含不可修的，仅用于展示）</summary>
    public List<CheckItem> Problems =>
        Items.Where(i => i.Status == CheckStatus.Fail || i.Status == CheckStatus.Warn).ToList();
}

/// <summary>下游设备（ICS 客户端）</summary>
public sealed class DownstreamDevice
{
    public string Ip { get; set; } = "";
    public string Mac { get; set; } = "";
    public bool Online { get; set; }

    /// <summary>
    /// 本地管理地址（locally administered）：首字节 bit1 = 1。手机 WiFi 随机化 MAC 属这一类，
    /// 所以拿它当"这大概是手机"的启发式判据。
    ///
    /// 【曾经是个 bug】原来写的是 `Mac[..1]` —— 只取**一个**十六进制字符：
    /// "AA-BB-CC-…" 被当成 0xA，`0xA & 0x02 == 0` → 永远判为"非随机"，
    /// 于是流量页"类型"列对手机永远是空的。正确的是取前两个字符 `Mac[..2]`。
    /// </summary>
    public bool IsRandomMac
    {
        get
        {
            if (Mac.Length < 2) return false;
            try { return (Convert.ToInt32(Mac[..2], 16) & 0x02) != 0; }
            catch { return false; }
        }
    }

    public string Label => IsRandomMac ? "手机(随机MAC)" : "";
}
