using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IcsWatch.Core;

/// <summary>
/// 绑定指定源地址的 ICMP 探测 —— 等价于 PowerShell 的 `ping -S 192.168.137.1 &lt;网关&gt;`。
///
/// 为什么必须自己发包：.NET 的 Ping 类不支持指定源地址。
/// 而"私有侧能否出网"恰恰需要**从 192.168.137.1 这个源地址**发出去才测得准
/// （默认走默认路由，测的是出口而不是 NAT 转发）。
///
/// 原始套接字需要管理员权限 —— 本程序正是以管理员运行的。
/// </summary>
public static class IcmpProbe
{
    /// <summary>从 sourceIp 发 ICMP Echo 到 targetIp，收到 Echo Reply 返回 true。</summary>
    public static bool PingFrom(string sourceIp, string targetIp, int timeoutMs = 1500)
    {
        try
        {
            var src = IPAddress.Parse(sourceIp);
            var dst = IPAddress.Parse(targetIp);

            // 【先确认源地址在本机】ICS 刚重启 / 程序刚启动时，192.168.137.1 可能还没下发到
            // 网卡上，此时 bind 必然抛 SocketException("地址无效")。这是**预期中的过渡状态**，
            // 不是故障 —— 原来它每 5 秒写一条 WARN，把日志刷成噪声（10-24 11:06 连刷 6 条）。
            if (!IsLocalAddress(src))
            {
                AppLog.Debug($"ICMP 源绑定跳过: {sourceIp} 尚未配置在本机");
                return false;
            }

            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
            sock.Bind(new IPEndPoint(src, 0));
            sock.ReceiveTimeout = timeoutMs;

            var packet = BuildEcho(0x1A2B, 1, 32);
            sock.SendTo(packet, new IPEndPoint(dst, 0));

            var buf = new byte[1500];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            var deadline = Environment.TickCount64 + timeoutMs;

            while (Environment.TickCount64 < deadline)
            {
                int n;
                try { n = sock.ReceiveFrom(buf, ref from); }
                catch (SocketException) { return false; }   // 超时或不可达

                if (n < 28) continue;
                int ihl = (buf[0] & 0x0F) * 4;
                if (n <= ihl) continue;
                var fromIp = ((IPEndPoint)from).Address;
                if (!fromIp.Equals(dst)) continue;          // 只认目标回包
                if (buf[ihl] == 0) return true;             // type 0 = Echo Reply
                if (buf[ihl] == 3 || buf[ihl] == 11) return false; // 不可达 / 超时
            }
            return false;
        }
        catch (Exception ex)
        {
            // 探测类失败降到 DEBUG：结论由「NAT 出口实测」那一项统一呈现，
            // 这里再记一条 WARN 只会让同一件事在日志里出现两次。
            AppLog.Debug("ICMP 源绑定探测失败: " + AppLog.Ex(ex));
            return false;
        }
    }

    /// <summary>
    /// 该地址当前是否配置在本机某块 Up 的网卡上。
    /// 用 NetProbe.Interfaces()（3 秒缓存）而不是自己枚举 —— 后者单次 103 毫秒，
    /// 而这段代码每个体检周期（5 秒）都会走到。
    /// </summary>
    private static bool IsLocalAddress(IPAddress ip)
    {
        try
        {
            foreach (var ni in NetProbe.Interfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    if (u.Address.Equals(ip)) return true;
            }
        }
        catch (Exception ex) { AppLog.Debug("检查本地地址失败: " + AppLog.Ex(ex)); }
        return false;
    }

    /// <summary>不绑定源地址的普通 ICMP（无管理员权限时也可用，作为降级路径）</summary>
    public static bool PingPlain(string targetIp, int timeoutMs = 1500)
    {
        try
        {
            using var p = new Ping();
            var r = p.Send(targetIp, timeoutMs);
            return r != null && r.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    private static byte[] BuildEcho(ushort id, ushort seq, int payloadLen)
    {
        var b = new byte[8 + payloadLen];
        b[0] = 8;                                   // type = Echo Request
        b[1] = 0;                                   // code
        b[4] = (byte)(id >> 8);
        b[5] = (byte)(id & 0xFF);
        b[6] = (byte)(seq >> 8);
        b[7] = (byte)(seq & 0xFF);
        for (int i = 8; i < b.Length; i++) b[i] = (byte)(i & 0xFF);
        ushort sum = Checksum(b);
        b[2] = (byte)(sum >> 8);
        b[3] = (byte)(sum & 0xFF);
        return b;
    }

    private static ushort Checksum(byte[] data)
    {
        long sum = 0;
        for (int i = 0; i + 1 < data.Length; i += 2) sum += (ushort)((data[i] << 8) | data[i + 1]);
        if ((data.Length & 1) == 1) sum += (ushort)(data[^1] << 8);
        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }
}
