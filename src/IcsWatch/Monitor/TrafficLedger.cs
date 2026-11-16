using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using IcsWatch.Config;
using IcsWatch.Core;

namespace IcsWatch.Monitor;

/// <summary>单个下游设备的用量（字节）</summary>
public sealed record HostUsage(
    string Ip,
    string Mac,
    string Label,
    long TodayRx,
    long TodayTx,
    long SessionRx,
    long SessionTx,
    long TotalRx,
    long TotalTx,
    DateTime LastSeen)
{
    public long TodayTotal => TodayRx + TodayTx;
    public long SessionTotal => SessionRx + SessionTx;
    public long TotalTotal => TotalRx + TotalTx;
}

/// <summary>给界面用的一帧快照（调用时点实时值）</summary>
public sealed record TrafficStatsSnapshot(
    bool CaptureRunning,
    string CaptureAdapter,
    string CaptureError,
    string CaptureWarning,
    long CapturedPackets,
    string UplinkAdapter,
    string UplinkError,
    string Day,
    long TodayRx,
    long TodayTx,
    long TotalRx,
    long TotalTx,
    long SessionRx,
    long SessionTx,
    IReadOnlyList<HostUsage> Hosts);

/// <summary>
/// 用量账本：把"这台机器一共用了多少"和"每台下游设备用了多少"记下来。
///
/// 两个数据源，各管一件事：
///   · <b>总量</b> —— 出口网卡（卡2 / 游戏模式的手机网卡）的接口计数器，精确、零开销。
///     包含本机自己的流量，所以它 ≥ 下面各设备之和。
///   · <b>按设备</b> —— 在私有端抓包，按"这个包属于哪个下游 IP"累加。
///     只有抓包能做到这件事（ICS 的 ipnat 驱动不暴露任何按会话的计数）。
///
/// 【口径要说清楚，不能让界面含糊】
/// 抓包在高负载下会丢包 → 单设备数字是"下限"；总量是精确值。
/// 所以界面上两者并列时，必须写明来源，否则用户会以为"设备加起来不到总量"是 bug。
///
/// 持久化：data\traffic.json，按天滚动（跨天只清"今日"，"累计"继续累）。
/// 每 60 秒落一次盘 + 退出时落一次，避免每秒写文件。
/// </summary>
public sealed class TrafficLedger : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly object _sync = new();

    // ── 累计值 ─────────────────────────────────────────────────
    private sealed class HostStat
    {
        public string Ip = "";
        public string Mac = "";
        public string Label = "";
        public long TodayRx, TodayTx, SessionRx, SessionTx, TotalRx, TotalTx;
        public DateTime LastSeen = DateTime.MinValue;
        public long LastTick;
    }

    private readonly Dictionary<string, HostStat> _hosts = new(StringComparer.Ordinal);
    private long _todayRx, _todayTx, _totalRx, _totalTx, _sessionRx, _sessionTx;
    private DateTime _day = DateTime.Today;

    // ── 出口网卡计数 ───────────────────────────────────────────
    private string _uplinkName = "";
    private NetworkInterface? _uplinkNi;
    private bool _uplinkPrimed;
    private long _upRx, _upTx;
    private string _uplinkError = "";

    // ── 抓包 ───────────────────────────────────────────────────
    private PacketCapture? _cap;
    private PacketCapture? _capSelf;
    private string _capSelfError = "";
    private string _capAdapter = "";
    private string _capError = "";
    private string _capWarning = "";
    private DateTime _capRetryAt = DateTime.MinValue;
    private uint _subnet, _subnetMask;
    private uint _selfIp;
    private bool _captureWanted;
    private readonly HashSet<uint> _localIps = new();

    /// <summary>访问记录（哪个设备连了哪些地址）</summary>
    public AccessLog Access { get; }

    // ── 设备标签（ARP 表，30 秒一次）────────────────────────────
    private DateTime _lastDeviceScan = DateTime.MinValue;

    // ── 落盘 ───────────────────────────────────────────────────
    private DateTime _lastSave = DateTime.MinValue;
    private bool _dirty;
    private readonly DateTime _since = DateTime.Now;

    public string FilePath => Path.Combine(_cfg.DataPath, "traffic.json");

    public TrafficLedger(AppConfig cfg)
    {
        _cfg = cfg;
        Access = new AccessLog(cfg);
        Current = this;
    }

    // ════════════════════════════════════════════════════════════
    //  生命周期
    // ════════════════════════════════════════════════════════════
    public void Start()
    {
        try { Load(); }
        catch (Exception ex) { AppLog.Warn("用量账本读取失败，从零开始: " + ex.Message); }

        _captureWanted = _cfg.TrafficStatsEnabled;
        if (!_captureWanted)
        {
            _capError = "已在配置中关闭（trafficStatsEnabled=false）";
            AppLog.Info("用量统计：按设备抓包已关闭（只统计总量）");
            return;
        }
        TryStartCapture(initial: true);
    }

    public void Stop()
    {
        try { Save(force: true); } catch { }
        try { _cap?.Stop(); _cap?.Dispose(); } catch { }
        _cap = null;
    }

    public void Dispose() => Stop();

    /// <summary>每秒调一次：采出口计数、跟新设备标签、必要时重启抓包、定期落盘</summary>
    public void Pump()
    {
        try
        {
            SampleUplink();
            RollDayIfNeeded();
            RefreshDeviceLabels();

            if (_captureWanted && _cap is { } c && !c.Running && !string.IsNullOrEmpty(c.LastError))
            {
                // 抓包线程死了（网卡拔了/被禁用）→ 30 秒后重试
                _capError = c.LastError;
                if (DateTime.Now >= _capRetryAt)
                {
                    _capRetryAt = DateTime.Now.AddSeconds(30);
                    AppLog.Warn("用量统计：抓包已停（" + _capError + "），30 秒后重试");
                    StopCapture();
                    TryStartCapture(initial: false);
                }
            }

            Access.Pump();

            if (_dirty && (DateTime.Now - _lastSave).TotalSeconds >= 60) Save(force: false);
        }
        catch (Exception ex) { AppLog.Debug("用量统计 Pump 异常: " + AppLog.Ex(ex)); }
    }

    // ════════════════════════════════════════════════════════════
    //  出口网卡计数（精确总量）
    // ════════════════════════════════════════════════════════════
    private string ResolveUplinkName()
    {
        // 游戏模式：卡2 被停用，真正的出口是手机 USB 共享网卡
        if (_cfg.GameModeActive && !string.IsNullOrWhiteSpace(_cfg.GameUsbAdapter)) return _cfg.GameUsbAdapter;
        return _cfg.PublicAdapter;
    }

    private void SampleUplink()
    {
        var name = ResolveUplinkName();
        if (!string.Equals(name, _uplinkName, StringComparison.OrdinalIgnoreCase))
        {
            _uplinkName = name;
            _uplinkNi = null;
            _uplinkPrimed = false;
        }

        // 【必须缓存】GetAllNetworkInterfaces 单次 ~103ms，每秒枚举一次就是 10% CPU
        if (_uplinkNi == null || !string.Equals(_uplinkNi.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _uplinkNi = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            catch { _uplinkNi = null; }
        }

        if (_uplinkNi == null)
        {
            _uplinkError = $"找不到出口网卡 {name}";
            _uplinkPrimed = false;
            return;
        }

        long rx, tx;
        try
        {
            var st = _uplinkNi.GetIPv4Statistics();
            rx = st.BytesReceived;
            tx = st.BytesSent;
            _uplinkError = "";
        }
        catch (Exception ex)
        {
            _uplinkError = "读不到出口计数器: " + ex.Message;
            _uplinkNi = null;        // 网卡可能已重建，下次重新枚举
            _uplinkPrimed = false;
            return;
        }

        if (!_uplinkPrimed)
        {
            _uplinkPrimed = true;
            _upRx = rx; _upTx = tx;
            return;
        }

        var dRx = rx - _upRx;
        var dTx = tx - _upTx;
        // 计数器归零（网卡禁用再启用 / 驱动重载）→ 直接以当前值为增量，不能记负数
        if (dRx < 0) dRx = Math.Max(0, rx);
        if (dTx < 0) dTx = Math.Max(0, tx);
        _upRx = rx; _upTx = tx;

        if (dRx == 0 && dTx == 0) return;

        lock (_sync)
        {
            _todayRx += dRx; _todayTx += dTx;
            _totalRx += dRx; _totalTx += dTx;
            _sessionRx += dRx; _sessionTx += dTx;
            _dirty = true;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  抓包（按设备）
    // ════════════════════════════════════════════════════════════
    private string ResolveCaptureAdapterName()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.TrafficCaptureAdapter)) return _cfg.TrafficCaptureAdapter;
        if (_cfg.GameModeActive && !string.IsNullOrWhiteSpace(_cfg.GameUsbAdapter)) return _cfg.GameUsbAdapter;
        return _cfg.PrivateAdapter;
    }

    private void TryStartCapture(bool initial)
    {
        StopCapture();

        var name = ResolveCaptureAdapterName();
        _capAdapter = name;

        AdapterInfo info;
        try { info = NetProbe.ReadAdapter(name); }
        catch (Exception ex)
        {
            _capError = "读取网卡信息失败: " + ex.Message;
            return;
        }

        if (!info.Exists)
        {
            _capError = $"网卡「{name}」不存在";
            return;
        }

        // 网段：优先用配置里的私有地址（ICS 私有端固定 192.168.137.x），
        // 拿不到再退回网卡自己的第一个 IPv4。
        if (!TryResolveSubnet(info, out _subnet, out _subnetMask, out _selfIp))
        {
            _capError = $"网卡「{name}」没有可用 IPv4 地址";
            return;
        }

        var guid = info.Guid;
        if (string.IsNullOrEmpty(guid))
        {
            _capError = $"拿不到网卡「{name}」的 GUID";
            return;
        }

        var flowOn = _cfg.AccessLogEnabled;
        var cap = new PacketCapture(
            PacketCapture.DeviceNameOf(Guid.Parse(guid)),
            flowOn ? PacketCapture.SnapLenFlow : PacketCapture.SnapLenDefault);

        if (!cap.Start(OnPacket, onFlow: flowOn ? OnFlowPrivate : null))
        {
            _capError = cap.LastError;
            cap.Dispose();
            if (initial) AppLog.Warn("用量统计：按设备抓包未启动 —— " + _capError);
            return;
        }

        _cap = cap;
        _capError = "";
        _capWarning = cap.Warning;
        if (!string.IsNullOrEmpty(_capWarning)) AppLog.Warn("用量统计：" + _capWarning);

        // 本机自己的地址：私有端 + 公网端的全部 IPv4（访问记录要靠它区分"本机"和"下游设备"）
        _localIps.Clear();
        _localIps.Add(_selfIp);
        try
        {
            // 注意：这里不能用 _uplinkName —— 第一次 Pump 之前它还是空的
            foreach (var s in NetProbe.ReadAdapter(ResolveUplinkName()).Ipv4WithPrefix)
                if (TryIpToUint(s, out var u)) _localIps.Add(u);
        }
        catch { }
        Access.SetSelfIps(_localIps);

        AppLog.Info($"用量统计：已在「{name}」抓包（{IpText(_subnet)}/{PrefixOf(_subnetMask)}）"
                    + (flowOn ? "，访问记录已开启" : ""));

        if (flowOn && _cfg.AccessLogIncludeSelf) TryStartSelfCapture(initial);
    }

    /// <summary>公网端抓包：只听"发给本机/由本机发出"的帧，用来记本机自己的访问。</summary>
    private void TryStartSelfCapture(bool initial)
    {
        try { _capSelf?.Stop(); _capSelf?.Dispose(); } catch { }
        _capSelf = null;
        _capSelfError = "";

        var name = ResolveUplinkName();
        if (string.IsNullOrWhiteSpace(name)) { _capSelfError = "出口网卡未知"; return; }

        AdapterInfo info;
        try { info = NetProbe.ReadAdapter(name); }
        catch (Exception ex) { _capSelfError = "读取出口网卡失败: " + ex.Message; return; }
        if (!info.Exists || string.IsNullOrEmpty(info.Guid)) { _capSelfError = $"网卡「{name}」不可用"; return; }

        foreach (var s in info.Ipv4WithPrefix)
            if (TryIpToUint(s, out var u)) _localIps.Add(u);
        Access.SetSelfIps(_localIps);

        var cap = new PacketCapture(PacketCapture.DeviceNameOf(Guid.Parse(info.Guid)),
                                    PacketCapture.SnapLenFlow, promiscuous: false);
        if (!cap.Start((_, _, _) => { }, onFlow: OnFlowSelf))
        {
            _capSelfError = cap.LastError;
            cap.Dispose();
            if (initial) AppLog.Warn($"访问记录：本机侧抓包未启动（{name}）—— {_capSelfError}");
            return;
        }

        _capSelf = cap;
        if (initial) AppLog.Info($"访问记录：已在「{name}」记录本机自身流量");
    }

    private static bool TryIpToUint(string withPrefix, out uint ip)
    {
        ip = 0;
        var slash = withPrefix.IndexOf('/');
        var text = slash < 0 ? withPrefix : withPrefix[..slash];
        if (!System.Net.IPAddress.TryParse(text, out var addr)) return false;
        var b = addr.GetAddressBytes();
        if (b.Length != 4) return false;
        ip = ToUint(b);
        return true;
    }

    private void StopCapture()
    {
        try { _cap?.Stop(); _cap?.Dispose(); } catch { }
        _cap = null;
        try { _capSelf?.Stop(); _capSelf?.Dispose(); } catch { }
        _capSelf = null;
    }

    /// <summary>私有端抓包 → 访问记录。方向归一化成 (device, peer)。</summary>
    private void OnFlowPrivate(uint src, uint dst, byte proto, ushort sport, ushort dport, int bytes, ReadOnlySpan<byte> payload)
    {
        // 先做"网段里还有没有第二台 DHCP 服务器"的旁证，这件事和访问记录开关无关：
        // 即使关掉访问记录，只要还在抓包就能看见。判据见 SegmentWatch 的注释。
        if (proto == 17 && sport == 67 && src != _selfIp) SegmentWatch.NoteDhcpServer(src);

        // DHCP 报文按客户端 MAC 记账：谁在要地址、服务器有没有回。见 DhcpWatch 的注释。
        if (proto == 17 && (sport == 67 || sport == 68) && payload.Length > 240)
            DhcpWatch.OnDatagram(fromServer: sport == 67, payload);

        if (!_cfg.AccessLogEnabled) return;

        var srcLocal = (src & _subnetMask) == _subnet;
        var dstLocal = (dst & _subnetMask) == _subnet;

        uint dev, peer;
        ushort devPort, peerPort;
        bool fromDev;

        if (srcLocal && !dstLocal) { dev = src; peer = dst; devPort = sport; peerPort = dport; fromDev = true; }
        else if (dstLocal && !srcLocal) { dev = dst; peer = src; devPort = dport; peerPort = sport; fromDev = false; }
        else if (srcLocal && dstLocal)
        {
            // 内网互访 / 设备问本机 DNS：device 仍然是设备那一侧
            if (src == _selfIp) { dev = dst; peer = src; devPort = dport; peerPort = sport; fromDev = false; }
            else if (dst == _selfIp) { dev = src; peer = dst; devPort = sport; peerPort = dport; fromDev = true; }
            else return;                       // 设备 ↔ 设备，不经过我们转发
        }
        else return;

        if (dev == _selfIp) return;
        if (IsNoisePeer(peer)) return;
        if (peer == _selfIp) return;          // 设备连本机的 DNS/DHCP，不算"访问外网"
        Access.OnFlow(dev, peer, devPort, peerPort, proto, bytes, fromDev, selfSide: false, payload);
    }

    /// <summary>组播/广播不是"访问了某个地址"，记下来只会把表刷满</summary>
    private static bool IsNoisePeer(uint ip)
        => (ip & 0xF0000000u) == 0xE0000000u || ip == 0xFFFFFFFFu;

    /// <summary>公网端抓包 → 访问记录（只认一端是本机地址的帧）。</summary>
    private void OnFlowSelf(uint src, uint dst, byte proto, ushort sport, ushort dport, int bytes, ReadOnlySpan<byte> payload)
    {
        if (!_cfg.AccessLogEnabled) return;

        uint dev, peer;
        ushort devPort, peerPort;
        bool fromDev;

        if (_localIps.Contains(src)) { dev = src; peer = dst; devPort = sport; peerPort = dport; fromDev = true; }
        else if (_localIps.Contains(dst)) { dev = dst; peer = src; devPort = dport; peerPort = sport; fromDev = false; }
        else return;

        if (IsNoisePeer(peer)) return;
        Access.OnFlow(dev, peer, devPort, peerPort, proto, bytes, fromDev, selfSide: true, payload);
    }

    /// <summary>界面上开关"访问记录"时调用；会重开抓包（snaplen 要变）。</summary>
    public void ApplyAccessLogSettings()
    {
        if (!_captureWanted) return;
        StopCapture();
        TryStartCapture(initial: false);
    }

    private bool TryResolveSubnet(AdapterInfo info, out uint net, out uint mask, out uint self)
    {
        net = mask = self = 0;

        var candidates = new List<string>();
        if (string.Equals(_capAdapter, _cfg.PrivateAdapter, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_cfg.PrivateIp))
            candidates.Add(_cfg.PrivateIp + "/24");
        candidates.AddRange(info.Ipv4WithPrefix);

        foreach (var s in candidates)
        {
            var slash = s.IndexOf('/');
            var ipText = slash < 0 ? s : s[..slash];
            var prefix = slash < 0 ? 24 : int.TryParse(s[(slash + 1)..], out var p) ? p : 24;

            if (!System.Net.IPAddress.TryParse(ipText, out var ip)) continue;
            var b = ip.GetAddressBytes();
            if (b.Length != 4) continue;

            self = ToUint(b);
            mask = prefix <= 0 ? 0u : prefix >= 32 ? uint.MaxValue : (uint.MaxValue << (32 - prefix));
            net = self & mask;
            return true;
        }
        return false;
    }

    /// <summary>抓包线程回调：每包一次。只做网段判断 + 计数，不碰 IO。</summary>
    private void OnPacket(uint src, uint dst, int bytes)
    {
        if (bytes <= 0) return;

        var srcLocal = (src & _subnetMask) == _subnet;
        var dstLocal = (dst & _subnetMask) == _subnet;

        // 只统计"内网 ↔ 外网"的包。内网互访、广播/组播都不算设备用量。
        uint devIp;
        bool tx;
        if (srcLocal && !dstLocal)
        {
            devIp = src; tx = true;      // 设备发出
        }
        else if (dstLocal && !srcLocal)
        {
            devIp = dst; tx = false;     // 设备收到
        }
        else return;

        if (devIp == _selfIp) return;    // ICS 自己（DHCP/DNS 服务）不算下游设备

        var key = IpText(devIp);
        var now = DateTime.Now;
        var tick = Environment.TickCount64;

        lock (_sync)
        {
            if (!_hosts.TryGetValue(key, out var hs))
            {
                hs = new HostStat { Ip = key, Mac = "", Label = "" };
                _hosts[key] = hs;
            }
            if (tx) { hs.TodayTx += bytes; hs.SessionTx += bytes; hs.TotalTx += bytes; }
            else { hs.TodayRx += bytes; hs.SessionRx += bytes; hs.TotalRx += bytes; }

            // 【别每包取一次 DateTime.Now】包速率可达 8 万/秒；这里 1 秒更新一次足够
            if (tick - hs.LastTick >= 1000) { hs.LastSeen = now; hs.LastTick = tick; }
            _dirty = true;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  设备标签
    // ════════════════════════════════════════════════════════════
    private void RefreshDeviceLabels()
    {
        if ((DateTime.Now - _lastDeviceScan).TotalSeconds < 30) return;
        _lastDeviceScan = DateTime.Now;

        try
        {
            var prefix = IpText(_subnet) is var t && t.Length > 0
                ? string.Join(".", t.Split('.').Take(3)) + "."
                : "";
            if (prefix.Length == 0) return;

            var devs = NetProbe.GetDevices(_capAdapter, prefix);
            if (devs.Count == 0) return;

            lock (_sync)
            {
                foreach (var d in devs)
                {
                    if (!_hosts.TryGetValue(d.Ip, out var hs)) continue;
                    if (!string.IsNullOrEmpty(d.Mac) && hs.Mac != d.Mac) hs.Mac = d.Mac;
                    if (!string.IsNullOrEmpty(d.Label) && hs.Label != d.Label) hs.Label = d.Label;
                }
            }
        }
        catch (Exception ex) { AppLog.Debug("用量统计：读设备标签失败 " + ex.Message); }
    }

    // ════════════════════════════════════════════════════════════
    //  查询 / 清零
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 最近创建的账本实例。给检查器/维修器查"这个 IP 最近有没有在通信"用，避免到处传引用。
    /// </summary>
    public static TrafficLedger? Current { get; private set; }

    /// <summary>某个 IP 在最近 <paramref name="seconds"/> 秒内有过流量。</summary>
    /// <remarks>
    /// 【为什么用流量判在线】手机休眠状态下对 ICMP 100% 不应答（实测），只靠 ping 会把
    /// 正在传数据的设备判成离线，界面显示"0 台"——数据明明在跑。
    /// </remarks>
    public bool HostActiveWithin(string ip, int seconds)
    {
        var cutoff = DateTime.Now.AddSeconds(-seconds);
        lock (_sync)
            return _hosts.TryGetValue(ip, out var hs) && hs.LastSeen >= cutoff;
    }

    /// <summary>
    /// 只取"按设备"的那部分（给每设备曲线用）——快照里其余字段都是网卡计数器，这里用不上。
    /// 每分钟只被调用几秒一次，复制几十个 record 的开销可以忽略。
    /// </summary>
    public IReadOnlyList<HostUsage> SnapshotHosts()
    {
        lock (_sync)
        {
            return _hosts.Values
                .Select(h => new HostUsage(h.Ip, h.Mac, h.Label,
                    h.TodayRx, h.TodayTx, h.SessionRx, h.SessionTx, h.TotalRx, h.TotalTx, h.LastSeen))
                .ToList();
        }
    }

    public TrafficStatsSnapshot Snapshot()
    {
        lock (_sync)
        {
            var list = _hosts.Values
                .Select(h => new HostUsage(h.Ip, h.Mac, h.Label,
                    h.TodayRx, h.TodayTx, h.SessionRx, h.SessionTx, h.TotalRx, h.TotalTx, h.LastSeen))
                .OrderByDescending(h => h.TodayTotal)
                .ThenByDescending(h => h.TotalTotal)
                .ToList();

            return new TrafficStatsSnapshot(
                CaptureRunning: _cap is { Running: true },
                CaptureAdapter: _capAdapter,
                CaptureError: _capError,
                CaptureWarning: _capWarning,
                CapturedPackets: _cap?.PacketCount ?? 0,
                UplinkAdapter: _uplinkName,
                UplinkError: _uplinkError,
                Day: _day.ToString("yyyy-MM-dd"),
                TodayRx: _todayRx, TodayTx: _todayTx,
                TotalRx: _totalRx, TotalTx: _totalTx,
                SessionRx: _sessionRx, SessionTx: _sessionTx,
                Hosts: list);
        }
    }

    /// <summary>清零"今日"和"本次运行"（累计值保留）</summary>
    public void ResetToday()
    {
        lock (_sync)
        {
            _todayRx = _todayTx = 0;
            foreach (var h in _hosts.Values) { h.TodayRx = 0; h.TodayTx = 0; }
            _dirty = true;
        }
        Save(force: true);
    }

    /// <summary>清零全部（含累计），并删除历史文件内容</summary>
    public void ResetAll()
    {
        lock (_sync)
        {
            _todayRx = _todayTx = _totalRx = _totalTx = 0;
            _sessionRx = _sessionTx = 0;
            _hosts.Clear();
            _dirty = true;
        }
        Save(force: true);
    }

    /// <summary>开关抓包（界面上那个「暂停统计」）</summary>
    public void SetCaptureEnabled(bool on)
    {
        _captureWanted = on;
        if (on)
        {
            _capError = "";
            TryStartCapture(initial: false);
        }
        else
        {
            StopCapture();
            _capError = "已暂停";
            _capWarning = "";
        }
    }

    public bool CaptureEnabled => _captureWanted;

    // ════════════════════════════════════════════════════════════
    //  跨天 / 持久化
    // ════════════════════════════════════════════════════════════
    private void RollDayIfNeeded()
    {
        var today = DateTime.Today;
        if (today == _day) return;

        lock (_sync)
        {
            if (today == _day) return;
            AppLog.Info($"用量统计：跨天（{_day:MM-dd} → {today:MM-dd}），今日清零、累计保留");
            _day = today;
            _todayRx = _todayTx = 0;
            foreach (var h in _hosts.Values) { h.TodayRx = 0; h.TodayTx = 0; }
            _dirty = true;
        }
        Save(force: true);
    }

    private sealed class Persisted
    {
        public int Version { get; set; } = 1;
        public string Day { get; set; } = "";
        public long TodayRx { get; set; }
        public long TodayTx { get; set; }
        public long TotalRx { get; set; }
        public long TotalTx { get; set; }
        public List<PersistedHost> Hosts { get; set; } = new();
    }

    private sealed class PersistedHost
    {
        public string Ip { get; set; } = "";
        public string Mac { get; set; } = "";
        public string Label { get; set; } = "";
        public long TodayRx { get; set; }
        public long TodayTx { get; set; }
        public long TotalRx { get; set; }
        public long TotalTx { get; set; }
        public DateTime Last { get; set; }
    }

    private void Load()
    {
        var path = FilePath;
        if (!File.Exists(path)) return;

        var json = File.ReadAllText(path);
        var p = JsonSerializer.Deserialize<Persisted>(json, AppConfig.JsonOpts);
        if (p == null) return;

        var today = DateTime.Today;
        var sameDay = p.Day == today.ToString("yyyy-MM-dd");

        lock (_sync)
        {
            _day = today;
            _totalRx = Math.Max(0, p.TotalRx);
            _totalTx = Math.Max(0, p.TotalTx);
            // 跨天重启：今日计数作废，累计继续
            _todayRx = sameDay ? Math.Max(0, p.TodayRx) : 0;
            _todayTx = sameDay ? Math.Max(0, p.TodayTx) : 0;

            foreach (var h in p.Hosts ?? new List<PersistedHost>())
            {
                if (string.IsNullOrWhiteSpace(h.Ip)) continue;
                _hosts[h.Ip] = new HostStat
                {
                    Ip = h.Ip,
                    Mac = h.Mac ?? "",
                    Label = h.Label ?? "",
                    TodayRx = sameDay ? Math.Max(0, h.TodayRx) : 0,
                    TodayTx = sameDay ? Math.Max(0, h.TodayTx) : 0,
                    TotalRx = Math.Max(0, h.TotalRx),
                    TotalTx = Math.Max(0, h.TotalTx),
                    LastSeen = h.Last,
                };
            }
        }
    }

    private void Save(bool force)
    {
        Persisted p;
        lock (_sync)
        {
            if (!_dirty && !force) return;
            p = new Persisted
            {
                Day = _day.ToString("yyyy-MM-dd"),
                TodayRx = _todayRx, TodayTx = _todayTx,
                TotalRx = _totalRx, TotalTx = _totalTx,
                Hosts = _hosts.Values.Select(h => new PersistedHost
                {
                    Ip = h.Ip, Mac = h.Mac, Label = h.Label,
                    TodayRx = h.TodayRx, TodayTx = h.TodayTx,
                    TotalRx = h.TotalRx, TotalTx = h.TotalTx,
                    Last = h.LastSeen,
                }).ToList(),
            };
            _dirty = false;
        }

        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(p, AppConfig.JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);   // 原子替换，避免写一半断电留下坏文件
            _lastSave = DateTime.Now;
        }
        catch (Exception ex)
        {
            AppLog.Debug("用量账本落盘失败: " + ex.Message);
            _dirty = true;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  工具
    // ════════════════════════════════════════════════════════════
    private static uint ToUint(byte[] b) => ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    private static string IpText(uint v) => $"{v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

    private static int PrefixOf(uint mask)
    {
        var n = 0;
        for (var i = 31; i >= 0 && (mask & (1u << i)) != 0; i--) n++;
        return n;
    }
}
