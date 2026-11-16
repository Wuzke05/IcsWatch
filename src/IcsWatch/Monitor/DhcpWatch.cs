using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace IcsWatch.Monitor;

/// <summary>
/// 「设备在要地址，但没人回」的现场记录。
///
/// 【为什么需要】"手机卡在正在获取 IP 地址"这件事，从网卡计数器、ARP 表、事件日志上都看不出来：
/// 链路是 Up 的、包计数器在涨，只是那台设备的 DHCP DISCOVER 一直没人应答。
/// 只有把抓到的 DHCP 报文按客户端 MAC 归组，才能区分"它在要"和"服务器给了"。
///
/// 判据：同一客户端 MAC 在 <see cref="WindowMs"/> 内发了 ≥ <see cref="StuckThreshold"/> 个请求，
/// 且第一个请求之后没有任何应答（OFFER/ACK）—— 就是"要不到地址"。
/// 这时重开一次 ICS 共享（会让 DHCP 分配器重新初始化）通常立刻恢复。
/// </summary>
internal static class DhcpWatch
{
    private const long WindowMs = 45_000;      // 观察窗口
    private const int StuckThreshold = 4;      // 窗口内至少发这么多次才算卡住

    private sealed class Client
    {
        public long FirstAt;
        public long LastAt;
        public int Requests;
        public long LastReplyAt;
        public string Mac = "";
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Client> Map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 抓包层每收到一个 DHCP 报文就调一次。<paramref name="bootp"/> 指向 BOOTP 载荷（UDP 头之后）。
    /// </summary>
    public static void OnDatagram(bool fromServer, ReadOnlySpan<byte> bootp)
    {
        if (bootp.Length < 240) return;
        var op = bootp[0];                       // 1=客户端请求 2=服务器应答
        if (fromServer != (op == 2)) return;

        var mac = MacText(bootp.Slice(28, 6));   // chaddr
        if (mac.Length == 0) return;

        var mt = MessageType(bootp);             // option 53
        var now = Environment.TickCount64;

        lock (Sync)
        {
            if (!Map.TryGetValue(mac, out var c))
            {
                c = new Client { Mac = mac };
                Map[mac] = c;
            }

            if (op == 1 && (mt == 1 || mt == 3))   // DISCOVER / REQUEST
            {
                if (now - c.LastAt > WindowMs) { c.FirstAt = now; c.Requests = 0; }
                c.LastAt = now;
                c.Requests++;
            }
            else if (op == 2)                      // OFFER / ACK
            {
                c.LastReplyAt = now;
                c.Requests = 0;                    // 有应答就不再算卡住
            }
        }
    }

    private static long _suppressUntil;      // 刚重置过分配器，这段时间内不再判"卡住"

    /// <summary>
    /// 重置分配器之后调用：接下来若干秒内不再判"卡住"。
    /// 【为什么】以前是直接 <see cref="Reset"/> 清表，于是复检必然通过 —— 那是"自证成功"。
    /// 现在改成"限时免报"：既不假装修好了，也避免刚重置完又立刻判卡住、触发下一次重置。
    /// </summary>
    public static void SuppressAfterReset(int seconds = 300)
    {
        lock (Sync) _suppressUntil = Environment.TickCount64 + seconds * 1000L;
    }

    /// <summary>是否处于"刚重置过、观察中"的免报窗口。</summary>
    public static bool Suppressed
    {
        get { lock (Sync) return Environment.TickCount64 < _suppressUntil; }
    }

    /// <summary>当前"在要地址但没人回"的客户端 MAC 列表。</summary>
    public static List<string> StuckClients()
    {
        var list = new List<string>();
        var now = Environment.TickCount64;
        lock (Sync)
        {
            if (now < _suppressUntil) return list;

            List<string>? dead = null;
            foreach (var kv in Map)
            {
                var c = kv.Value;
                if (now - c.LastAt > WindowMs * 2) { (dead ??= new List<string>()).Add(kv.Key); continue; }
                if (c.Requests < StuckThreshold) continue;
                if (now - c.FirstAt > WindowMs) continue;          // 老账不算
                if (c.LastReplyAt > c.FirstAt) continue;           // 期间给过应答
                list.Add(c.Mac);
            }
            if (dead != null) foreach (var k in dead) Map.Remove(k);
        }
        return list;
    }

    public static void Reset()
    {
        lock (Sync) { Map.Clear(); _suppressUntil = 0; }
    }

    private static int MessageType(ReadOnlySpan<byte> b)
    {
        var i = 240;                                        // 选项起点（magic cookie 之后）
        if (b.Length < 244) return 0;
        if (!(b[236] == 0x63 && b[237] == 0x82 && b[238] == 0x53 && b[239] == 0x63)) return 0;
        while (i < b.Length)
        {
            var code = b[i];
            if (code == 255) return 0;                      // END
            if (code == 0) { i++; continue; }               // PAD
            if (i + 1 >= b.Length) return 0;
            int len = b[i + 1];
            if (i + 2 + len > b.Length) return 0;
            if (code == 53 && len >= 1) return b[i + 2];
            i += 2 + len;
        }
        return 0;
    }

    private static string MacText(ReadOnlySpan<byte> m)
    {
        bool allZero = true, allFf = true;
        for (var i = 0; i < 6; i++)
        {
            if (m[i] != 0x00) allZero = false;
            if (m[i] != 0xFF) allFf = false;
        }
        if (allZero || allFf) return "";
        var sb = new StringBuilder(17);
        for (var i = 0; i < 6; i++)
        {
            if (i > 0) sb.Append('-');
            sb.Append(m[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
