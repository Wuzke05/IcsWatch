using System.Text;

namespace IcsWatch.Monitor;

/// <summary>
/// DNS 报文解析（只做两件事：读出问题段的域名、把应答里的 A 记录对到域名上）。
///
/// 【为什么只认 A 记录】下游设备的 DNS 查询走的是 ICS 自带的 DNS 代理（SharedAccess
/// 在 0.0.0.0:53 监听），查询和应答都是明文，所以域名是能直接读出来的 —— 比从
/// TLS SNI 猜准得多。AAAA/CNAME 不单独建表：CNAME 的目标名对用户没意义，
/// 而一条应答里所有 A 记录都归属于问题段问的那个域名，这样就够用了。
/// </summary>
internal static class DnsParser
{
    public static bool IsDns(ushort sport, ushort dport) => sport == 53 || dport == 53;

    /// <summary>读问题段的域名（大小写统一成小写）。questionEnd 是 QTYPE/QCLASS 之后的偏移。</summary>
    public static bool TryReadQuestion(ReadOnlySpan<byte> p, out string name, out int questionEnd)
    {
        name = "";
        questionEnd = 0;
        if (p.Length < 12) return false;

        int qd = (p[4] << 8) | p[5];
        if (qd < 1) return false;

        var sb = new StringBuilder(64);
        var off = 12;
        while (true)
        {
            if (off >= p.Length) return false;
            int len = p[off];
            if (len == 0) { off++; break; }
            if ((len & 0xC0) != 0) return false;          // 问题段不该有压缩指针
            if (len > 63 || off + 1 + len > p.Length) return false;
            if (sb.Length > 0) sb.Append('.');
            for (var i = 0; i < len; i++) sb.Append((char)p[off + 1 + i]);
            off += 1 + len;
            if (sb.Length > 253) return false;
        }

        if (off + 4 > p.Length) return false;             // QTYPE + QCLASS
        questionEnd = off + 4;
        name = sb.ToString().ToLowerInvariant();
        return name.Length > 0;
    }

    /// <summary>遍历应答段：把每个 A 记录的 IP 归到 qname 上。返回对上的条数。</summary>
    public static int ReadAnswers(ReadOnlySpan<byte> p, string qname, Action<string, uint> onA)
    {
        if (p.Length < 12 || qname.Length == 0) return 0;
        int an = (p[6] << 8) | p[7];
        if (an <= 0) return 0;

        if (!TryReadQuestion(p, out _, out var off)) return 0;

        var hits = 0;
        for (var i = 0; i < an; i++)
        {
            if (!TrySkipName(p, ref off)) break;
            if (off + 10 > p.Length) break;

            int type = (p[off] << 8) | p[off + 1];
            int rdlen = (p[off + 8] << 8) | p[off + 9];
            off += 10;

            if (off + rdlen > p.Length) break;

            if (type == 1 && rdlen == 4)
            {
                var ip = ((uint)p[off] << 24) | ((uint)p[off + 1] << 16) | ((uint)p[off + 2] << 8) | p[off + 3];
                onA(qname, ip);
                hits++;
            }
            off += rdlen;
        }
        return hits;
    }

    /// <summary>跳过一条域名（含压缩指针）。</summary>
    private static bool TrySkipName(ReadOnlySpan<byte> p, ref int off)
    {
        var guard = 0;
        while (off < p.Length && guard++ < 256)
        {
            int len = p[off];
            if (len == 0) { off++; return true; }
            if ((len & 0xC0) == 0xC0) { off += 2; return off <= p.Length; }
            if (len > 63) return false;
            off += 1 + len;
        }
        return false;
    }

    /// <summary>把 IPv4 的 uint 写法转回点分十进制（抓包侧全程用 uint 比较，省字符串分配）</summary>
    public static string IpText(uint ip)
        => $"{ip >> 24}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";
}
