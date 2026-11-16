using IcsWatch.Config;
using IcsWatch.Monitor;

namespace IcsWatch.Core;

/// <summary>
/// 清理"僵尸租约" —— ICS 写进 ARP 表的 Permanent 条目。
///
/// 【问题】ICS 每次给下游设备分配地址，都会在 ARP 表里留一条 **Permanent/static** 条目。
/// 这类条目不像动态条目那样会被老化掉：同一台设备每重连一次就多一条，实测 7 小时累积 18 条。
/// 地址池只有 253 个，照这个速度约 8 天耗尽，之后谁都拿不到地址（表现为"卡在获取 IP"）。
///
/// 【判定】同时满足才算僵尸：
///   ① 这台设备现在 ping 不通；
///   ② 最近 10 分钟内没有任何流量（账本里查）；
///   ③ 不是本机地址、不是广播。
/// 只删这一类的风险很低：设备要用时自己会重新 ARP，ARP 表几秒就能重建。
/// </summary>
internal static class NeighborJanitor
{
    internal readonly record struct Row(string Ip, string Mac, int IfIndex, int Type);

    public static List<Row> Find(AppConfig cfg, int idleSeconds = 600)
    {
        var list = new List<Row>();
        var a = NetProbe.ReadAdapter(cfg.PrivateAdapter);
        if (a.IfIndex == null) return list;

        var prefix = string.Join(".", cfg.PrivateIp.Split('.').Take(3)) + ".";
        foreach (var (ip, mac, idx, type) in NetProbe.GetArpTable())
        {
            if (idx != a.IfIndex) continue;
            if (!ip.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (ip == cfg.PrivateIp) continue;
            if (mac == "00-00-00-00-00-00" || mac == "FF-FF-FF-FF-FF-FF") continue;
            if (TrafficLedger.Current?.HostActiveWithin(ip, idleSeconds) == true) continue;   // 还在传数据
            if (IcmpProbe.PingPlain(ip, 400)) continue;                                      // 还活着
            list.Add(new Row(ip, mac, idx, type));
        }
        return list;
    }

    /// <summary>删掉可清理的条目，返回删除条数。</summary>
    public static int Cleanup(AppConfig cfg, Action<string> log)
    {
        try
        {
            var dead = Find(cfg);
            if (dead.Count == 0) { log("没有僵尸租约"); return 0; }

            var n = 0;
            foreach (var r in dead)
                if (NetProbe.DeleteNeighbor(cfg.PrivateAdapter, r.Ip)) n++;

            log(n > 0
                ? $"已清理 {n} 条僵尸租约（ping 不通且 10 分钟无流量）"
                : $"{dead.Count} 条僵尸租约删除失败");
            return n;
        }
        catch (Exception ex)
        {
            log("清理僵尸租约出错: " + ex.Message);
            return 0;
        }
    }
}
