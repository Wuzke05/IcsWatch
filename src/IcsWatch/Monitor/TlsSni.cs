using System.Text;

namespace IcsWatch.Monitor;

/// <summary>
/// 从 TLS 握手里读 SNI（服务器名字）。
///
/// 【为什么还要这个】DNS 那条路只在设备老老实实问本地 DNS 时有效；手机上很多
/// 流量走 DoH/DoT 或直接用 IP，域名读不出来。而 HTTPS 握手里的 server_name
/// 扩展是明文（除非服务器上了 ECH），能补上这一块。
///
/// 只认 ClientHello（0x16 / 0x01），别的握手类型直接放弃 —— server 端
/// EncryptedExtensions 之后的都加密了，没有意义。
/// </summary>
internal static class TlsSni
{
    public static bool TryGetServerName(ReadOnlySpan<byte> p, out string host)
    {
        host = "";
        if (p.Length < 6) return false;

        // TLS record: type(1) version(2) length(2)
        if (p[0] != 0x16 || p[1] != 0x03) return false;

        var off = 5;
        if (off + 4 > p.Length) return false;
        if (p[off] != 0x01) return false;                 // ClientHello

        int hsLen = (p[off + 1] << 16) | (p[off + 2] << 8) | p[off + 3];
        off += 4;
        int end = (int)Math.Min(p.Length, (long)off + hsLen);

        // client_version(2) + random(32)
        if (off + 34 > end) return false;
        off += 34;

        // session_id
        if (off >= end) return false;
        off += 1 + p[off];
        if (off + 2 > end) return false;

        // cipher_suites
        int csLen = (p[off] << 8) | p[off + 1];
        off += 2 + csLen;
        if (off >= end) return false;

        // compression_methods
        off += 1 + p[off];
        if (off + 2 > end) return false;

        // extensions
        int extTotal = (p[off] << 8) | p[off + 1];
        off += 2;
        int extEnd = (int)Math.Min(end, (long)off + extTotal);

        while (off + 4 <= extEnd)
        {
            int type = (p[off] << 8) | p[off + 1];
            int len = (p[off + 2] << 8) | p[off + 3];
            off += 4;
            if (off + len > extEnd) return false;

            if (type == 0x0000)                            // server_name
            {
                var o = off;
                if (o + 2 > off + len) return false;
                o += 2;                                    // server_name_list 长度
                if (o + 3 > off + len) return false;
                int nameType = p[o];
                int nameLen = (p[o + 1] << 8) | p[o + 2];
                o += 3;
                if (nameType != 0 || o + nameLen > off + len) return false;

                host = Encoding.ASCII.GetString(p.Slice(o, nameLen)).ToLowerInvariant();
                return host.Length > 0 && host.Length < 256;
            }
            off += len;
        }
        return false;
    }
}
