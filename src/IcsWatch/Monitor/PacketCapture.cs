using System.Runtime.InteropServices;

namespace IcsWatch.Monitor;

/// <summary>
/// 基于 Npcap 的 <b>Packet32</b> 接口（Packet.dll）的轻量抓包器。
///
/// 【为什么不用 wpcap.dll】
/// 本机只装了 32 位的 wpcap（SysWOW64\wpcap.dll，WinPcap 4.1.3），64 位版本不存在；
/// 而 System32\Packet.dll（Npcap 1.60 / x64）是在的 —— 它属于 Npcap 的 Packet32 接口，
/// 不需要 pcap_* 那套函数。代价是 BPF 过滤没法用，只能把 IPv4 解析放在用户态。
/// 对于"按 IP 统计字节数"这个用途，用户态过滤完全够。
///
/// 【缓冲区格式】实测 Npcap 1.60 / x64：一次 PacketReceivePacket 取回一批包，
/// 每个包前面是 bpf_hdr（时间戳 8B、caplen@8、datalen@12、hdrlen@16(ushort)），
/// 以太网帧起点 = 包起点 + hdrlen；包与包之间按 4 字节对齐。
/// 一批里有几个包由 PACKET 结构的 ulBytesReceived 决定（x64 偏移 52，已实测）。
///
/// 【代价】抓包会占一个线程 + 每批一次内存拷贝。为了把开销压到最低：
///   · 只用 96 字节 snaplen（只留二层/三层头，不拷负载）—— 拿 IP 头里的总长度算字节数
///   · 内核缓冲 16MB，读超时 250ms（一次读回一批，而不是一包一次系统调用）
/// </summary>
internal sealed class PacketCapture : IDisposable
{
    // ── Packet32 API ───────────────────────────────────────────
    private const string Dll = "Packet.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr PacketOpenAdapter([MarshalAs(UnmanagedType.LPStr)] string adapterName);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PacketCloseAdapter(IntPtr adapter);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool PacketSetBuff(IntPtr adapter, int dim);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool PacketSetReadTimeout(IntPtr adapter, int timeout);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool PacketSetSnapLen(IntPtr adapter, int snaplen);

    // 混杂模式在 Packet32 里叫 SetHwFilter + NDIS_PACKET_TYPE_PROMISCUOUS，
    // 没有 PacketSetPromiscuousMode 这个导出（实测 EntryPointNotFound）。
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool PacketSetHwFilter(IntPtr adapter, uint filter);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool PacketSetMinToCopy(IntPtr adapter, int dim);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr PacketAllocatePacket();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PacketFreePacket(IntPtr lpPacket);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void PacketInitPacket(IntPtr lpPacket, IntPtr buffer, uint length);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool PacketReceivePacket(IntPtr adapter, IntPtr lpPacket, bool sync);

    // ── 常量 ───────────────────────────────────────────────────
    private const int KernelBuffer = 16 * 1024 * 1024;   // 内核环形缓冲
    private const int UserBuffer = 2 * 1024 * 1024;      // 用户缓冲
    private const int ReadTimeoutMs = 250;
    public const int SnapLenDefault = 96;                // 只看 IP 头算字节时够用
    public const int SnapLenFlow = 384;                  // 要读 DNS 名字和 TLS SNI 时用这个
    private const int UsedOffset = 52;                   // x64: PACKET.ulBytesReceived（实测）
    private const uint NdisPromiscuous = 0x0020;         // NDIS_PACKET_TYPE_PROMISCUOUS
    private const uint NdisDirected = 0x0001;            // NDIS_PACKET_TYPE_DIRECTED

    private IntPtr _adapter;
    private IntPtr _pkt;
    private IntPtr _buf;
    private byte[] _managed = Array.Empty<byte>();
    private Thread? _thread;
    private volatile bool _stop;

    private Action<uint, uint, int>? _onPacket;
    private FlowCallback? _onFlow;
    private Action? _onBatch;

    /// <summary>
    /// 带载荷的流回调：proto = 6(TCP) / 17(UDP)，payload 只到 snaplen 为止
    /// （所以可能是截断的 —— 解析 DNS/SNI 时按实际长度处理，别假设收全了）。
    /// </summary>
    public delegate void FlowCallback(uint src, uint dst, byte proto, ushort sport, ushort dport, int bytes, ReadOnlySpan<byte> payload);

    public string DeviceName { get; }
    public int SnapLen { get; }
    public string LastError { get; private set; } = "";
    /// <summary>能跑但打了折扣时的说明（例如不支持混杂模式），不影响 Running</summary>
    public string Warning { get; private set; } = "";
    public bool Running => _thread is { IsAlive: true };

    /// <summary>收到的包数（诊断用，UI 的"抓包状态"里显示）</summary>
    public long PacketCount => Interlocked.Read(ref _packets);
    private long _packets;

    public PacketCapture(string deviceName, int snapLen = SnapLenDefault, bool promiscuous = true)
    {
        DeviceName = deviceName;
        SnapLen = snapLen;
        Promiscuous = promiscuous;
    }

    public bool Promiscuous { get; }

    /// <summary>打开网卡并启动接收线程。onPacket(源IP, 目的IP, IP总长字节数)。</summary>
    public bool Start(Action<uint, uint, int> onPacket, Action? onBatch = null, FlowCallback? onFlow = null)
    {
        if (Running) return true;
        _onPacket = onPacket;
        _onBatch = onBatch;
        _onFlow = onFlow;
        LastError = "";

        try
        {
            _adapter = PacketOpenAdapter(DeviceName);
            if (_adapter == IntPtr.Zero)
            {
                LastError = $"打不开网卡 {DeviceName}（需要管理员权限，或该网卡已停用）";
                return false;
            }

            PacketSetBuff(_adapter, KernelBuffer);
            PacketSetReadTimeout(_adapter, ReadTimeoutMs);

            // 【必须】混杂模式。ICS 转发给下游设备的帧，目的 MAC 是设备而不是本机，
            // 网卡硬件默认只把"发给自己的帧"上送 → 不开混杂只能看到广播/DHCP，
            // 实测 8 秒 30 个包，而同时刻出口计数已有 5 MB，差三个数量级。
            try
            {
                // 混杂模式：私有端必须开（下游设备的单播帧目的 MAC 不是本机）；
                // 公网端只要"发给自己的"，开 DIRECTED 就够，能避开同网段别人的流量。
                var filter = Promiscuous ? NdisPromiscuous : NdisDirected;
                if (!PacketSetHwFilter(_adapter, filter))
                    Warning = "网卡不支持混杂模式，按设备统计会漏掉大部分单播流量";
            }
            catch (EntryPointNotFoundException) { Warning = "该 Packet.dll 无 SetHwFilter，按设备统计可能偏小"; }

            // 攒够 64KB 才唤醒读线程：满速时少几十倍系统调用
            try { PacketSetMinToCopy(_adapter, 64 * 1024); }
            catch (EntryPointNotFoundException) { }

            // snaplen 不是所有版本都导出；失败就退回默认值（只多拷负载，不影响正确性）
            try { PacketSetSnapLen(_adapter, SnapLen); }
            catch (EntryPointNotFoundException) { }

            _buf = Marshal.AllocHGlobal(UserBuffer);
            _managed = new byte[UserBuffer];
            _pkt = PacketAllocatePacket();
            if (_pkt == IntPtr.Zero)
            {
                LastError = "PacketAllocatePacket 失败";
                Cleanup();
                return false;
            }
            PacketInitPacket(_pkt, _buf, UserBuffer);

            _stop = false;
            _thread = new Thread(ReceiveLoop)
            {
                IsBackground = true,           // 不阻止进程退出
                Name = "IcsWatch.capture",
                Priority = ThreadPriority.BelowNormal,   // 【重要】别和网络栈抢 CPU
            };
            _thread.Start();
            return true;
        }
        catch (DllNotFoundException)
        {
            LastError = "找不到 Packet.dll（未安装 Npcap，64 位抓包不可用）";
            Cleanup();
            return false;
        }
        catch (BadImageFormatException)
        {
            LastError = "Packet.dll 位数不匹配（需要 64 位 Npcap）";
            Cleanup();
            return false;
        }
        catch (Exception ex)
        {
            LastError = "抓包启动失败: " + ex.Message;
            Cleanup();
            return false;
        }
    }

    public void Stop()
    {
        _stop = true;
        try { _thread?.Join(ReadTimeoutMs * 4); } catch { }
        _thread = null;
        Cleanup();
    }

    private void Cleanup()
    {
        try { if (_pkt != IntPtr.Zero) { PacketFreePacket(_pkt); _pkt = IntPtr.Zero; } } catch { }
        try { if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; } } catch { }
        try { if (_adapter != IntPtr.Zero) { PacketCloseAdapter(_adapter); _adapter = IntPtr.Zero; } } catch { }
    }

    public void Dispose() => Stop();

    // ════════════════════════════════════════════════════════════
    //  接收线程
    // ════════════════════════════════════════════════════════════
    private void ReceiveLoop()
    {
        var dead = 0;
        while (!_stop)
        {
            bool ok;
            try { ok = PacketReceivePacket(_adapter, _pkt, true); }
            catch { ok = false; }

            if (!ok)
            {
                // 读超时也返回 false —— 不能当成"网卡坏了"，否则没流量时空转。
                // 连续多次拿不到数据时，回头确认网卡还在不在（USB 网卡会整个消失）。
                if (++dead >= 40 && !AdapterStillPresent())
                {
                    LastError = "网卡已消失（抓包停止）";
                    break;
                }
                if (dead >= 40) dead = 0;
                continue;
            }
            dead = 0;

            try
            {
                int used = Marshal.ReadInt32(_pkt, UsedOffset);
                if (used <= 0 || used > UserBuffer) continue;
                Marshal.Copy(_buf, _managed, 0, Math.Min(used, UserBuffer));
                ParseBatch(_managed, Math.Min(used, UserBuffer));
                _onBatch?.Invoke();
            }
            catch (Exception ex)
            {
                LastError = "抓包解析异常: " + ex.Message;
                break;
            }
        }
    }

    private static bool AdapterStillPresent()
        => true;   // 由 TrafficLedger 用 NetworkInterface 复核，这里不重复枚举（103ms/次）

    /// <summary>解析一批数据：Ethernet → (802.1Q) → IPv4 → 回调</summary>
    private void ParseBatch(byte[] b, int used)
    {
        int off = 0;
        while (off + 20 <= used)
        {
            uint caplen = BitConverter.ToUInt32(b, off + 8);
            ushort hdrlen = BitConverter.ToUInt16(b, off + 16);

            // 缓冲区尾部是上一批的残留？不会：每次都会整批覆盖 used 之前的区域，
            // 而 used 之后的字节我们不会读到。
            if (caplen == 0 || hdrlen < 16 || hdrlen > 128) break;

            int frame = off + hdrlen;
            if (frame + 14 > used) break;

            ushort ether = (ushort)((b[frame + 12] << 8) | b[frame + 13]);
            int ipOff = frame + 14;

            // 802.1Q VLAN：ethertype 之后还有 4 字节
            if (ether == 0x8100 && ipOff + 4 <= used)
            {
                ether = (ushort)((b[ipOff + 2] << 8) | b[ipOff + 3]);
                ipOff += 4;
            }

            if (ether == 0x0800)
            {
                // 这个 IP 包实际抓到了多少字节：caplen 可能小于 wire 长度（snaplen 截断）
                int avail = Math.Min((int)caplen - (ipOff - frame), used - ipOff);
                if (avail > 0) ParseIpv4(b, ipOff, avail);
            }

            off = (frame + (int)caplen + 3) & ~3;   // BPF_WORDALIGN
        }
    }

    private void ParseIpv4(byte[] b, int ip, int avail)
    {
        if (avail < 20) return;
        if ((b[ip] >> 4) != 4) return;

        int ihl = (b[ip] & 0x0F) * 4;
        if (ihl < 20 || ihl > avail) return;

        int total = (b[ip + 2] << 8) | b[ip + 3];
        if (total < ihl) return;

        uint src = ((uint)b[ip + 12] << 24) | ((uint)b[ip + 13] << 16) | ((uint)b[ip + 14] << 8) | b[ip + 15];
        uint dst = ((uint)b[ip + 16] << 24) | ((uint)b[ip + 17] << 16) | ((uint)b[ip + 18] << 8) | b[ip + 19];

        Interlocked.Increment(ref _packets);
        _onPacket?.Invoke(src, dst, total);

        if (_onFlow == null) return;
        // 分片包（MF 置位或片偏移非 0）没有完整四层头，跳过
        if ((b[ip + 6] & 0x20) != 0 || (b[ip + 7] & 0x1F) != 0) return;

        byte proto = b[ip + 9];
        if (proto != 6 && proto != 17) return;

        int l4 = ip + ihl;
        // have = 这个包里实际能读到的 IP 载荷字节数
        int have = Math.Min(total, avail) - ihl;
        if (have <= 0) return;

        ushort sport, dport;
        int payloadOff;

        if (proto == 17)
        {
            if (have < 8) return;
            sport = (ushort)((b[l4] << 8) | b[l4 + 1]);
            dport = (ushort)((b[l4 + 2] << 8) | b[l4 + 3]);
            payloadOff = l4 + 8;
        }
        else
        {
            if (have < 20) return;
            sport = (ushort)((b[l4] << 8) | b[l4 + 1]);
            dport = (ushort)((b[l4 + 2] << 8) | b[l4 + 3]);
            int doff = (b[l4 + 12] >> 4) * 4;
            if (doff < 20 || doff > have) return;
            payloadOff = l4 + doff;
        }

        int plen = ip + have - payloadOff;
        if (plen <= 0) return;

        try { _onFlow(src, dst, proto, sport, dport, total, new ReadOnlySpan<byte>(b, payloadOff, plen)); }
        catch { /* 解析方自己负责兜异常，别让一个坏包打死接收线程 */ }
    }

    /// <summary>把 GUID 转成 Packet32 认识的设备名</summary>
    public static string DeviceNameOf(Guid guid) => $@"\Device\NPF_{guid:B}";

    /// <summary>"\Device\NPF_{...}" 里取出 GUID；解析不出返回 null</summary>
    public static Guid? GuidOf(string name)
    {
        try
        {
            var i = name.LastIndexOf('{');
            if (i < 0) return null;
            return Guid.Parse(name[(i + 1)..].TrimEnd('}'));
        }
        catch { return null; }
    }
}
