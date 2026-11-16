using System;

namespace IcsWatch.Core;

/// <summary>
/// 「这条网段上还有别人在发地址」的旁证。
///
/// 【为什么需要这一条】卡1 接的是下游的 WiFi 路由器。只要那台路由器还开着 DHCP，
/// 设备每次重连就在两套地址池之间抢：抢到 ICS 池（192.168.137.x）的能上网，
/// 抢到路由器池的既没有 NAT、又被强制门户拦在登录页 —— 表现就是"反复掉网"
/// 或者手机一直卡在"正在获取 IP 地址"。这件事在日志里**完全看不见**，
/// 因为 IcsWatch 自己不参与分配，只能从抓包里侧证。
///
/// 判据：私有端抓包里出现 UDP 源端口 67、且源地址不是本机 —— 那是另一台 DHCP 服务器在应答。
/// 现场实测（2026-11-15）：下游路由器 192.168.3.1 一边发地址一边把
/// connect.rom.miui.com/generate_204 重定向到自己的登录页，手机因此显示"无法访问互联网"。
/// </summary>
internal static class SegmentWatch
{
    private static readonly object Sync = new();
    private static uint _dhcpIp;
    private static long _dhcpAtMs;

    /// <summary>抓包层每看到一次"别人发的 DHCP 应答"就调一次。</summary>
    public static void NoteDhcpServer(uint ip)
    {
        lock (Sync)
        {
            _dhcpIp = ip;
            _dhcpAtMs = Environment.TickCount64;
        }
    }

    /// <summary>最近 <paramref name="within"/> 内出现的第二台 DHCP 服务器地址；没有则 null。</summary>
    public static string? ForeignDhcpServer(TimeSpan within)
    {
        lock (Sync)
        {
            if (_dhcpIp == 0) return null;
            if (Environment.TickCount64 - _dhcpAtMs > (long)within.TotalMilliseconds) return null;
            return Format(_dhcpIp);
        }
    }

    public static void Reset()
    {
        lock (Sync) { _dhcpIp = 0; _dhcpAtMs = 0; }
    }

    private static string Format(uint ip)
        => $"{ip >> 24}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";
}
