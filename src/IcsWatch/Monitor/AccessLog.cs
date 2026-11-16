using System.Text.Json;
using IcsWatch.Config;

namespace IcsWatch.Monitor;

/// <summary>一条访问记录（给界面看的形状）</summary>
public sealed record AccessEntry(
    string Device,
    string Domain,
    string Peer,
    int Port,
    string Proto,
    long Rx,
    long Tx,
    DateTime First,
    DateTime Last);

/// <summary>
/// 访问记录：按(设备, 对端, 端口, 协议)聚合每个连接，并从 DNS 应答和 TLS SNI 里补域名。
///
/// 只记"连到了哪里"，不记 URL 路径和内容 —— HTTPS 之后是加密的，拿不到；
/// 这也决定了这张表能回答的问题："这个设备今天连了哪些站"，而不是"看了什么页面"。
/// </summary>
public sealed class AccessLog
{
    private readonly AppConfig _cfg;
    private readonly object _sync = new();

    private readonly Dictionary<Key, Entry> _map = new();
    private readonly Dictionary<uint, NameHit> _ipName = new();
    private readonly HashSet<uint> _selfIps = new();

    /// <summary>
    /// 下游侧最近出现过的 (对端, 端口, 协议)。ICS 会做 NAT —— 手机访问 1.2.3.4:443
    /// 在公网端看起来就是"本机访问 1.2.3.4:443"，不排除的话同一份流量会在
    /// "设备"和"本机"两张表里各记一遍。
    /// </summary>
    private readonly Dictionary<(uint Peer, ushort Port, byte Proto), long> _downstream = new();
    private static readonly TimeSpan ForwardWindow = TimeSpan.FromSeconds(60);

    private readonly DateTime _started = DateTime.Now;
    private long _lastPersistTicks;
    private bool _dirty;
    private string _lastError = "";

    private const int MaxEntries = 20_000;
    private const int MaxNames = 8_192;
    private static readonly TimeSpan NameTtl = TimeSpan.FromMinutes(30);

    public AccessLog(AppConfig cfg)
    {
        _cfg = cfg;
        FilePath = Path.Combine(cfg.DataPath, "access.json");
        Load();
    }

    public string FilePath { get; }
    public string LastError => _lastError;
    public int Count { get { lock (_sync) return _map.Count; } }
    public long SeenPackets { get { lock (_sync) return _seen; } }
    private long _seen;

    // ════════════════════════════════════════════════════════════
    //  收包
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// device/peer 已经由调用方归一化过：device 永远是"我们关心的这一侧"。
    /// fromDevice=true 表示这个包是 device 发出去的。
    /// </summary>
    public void OnFlow(uint device, uint peer, ushort devicePort, ushort peerPort,
                       byte proto, int bytes, bool fromDevice, bool selfSide, ReadOnlySpan<byte> payload)
    {
        var now = DateTime.UtcNow;
        var key = new Key(device, peer, peerPort, proto);
        var sideKey = (peer, peerPort, proto);

        Entry e;
        lock (_sync)
        {
            if (selfSide)
            {
                // ICS 转发过来的连接也走公网端，账算在下游设备那笔上，这里不重复记
                if (_downstream.TryGetValue(sideKey, out var tick)
                    && now.Ticks - tick < ForwardWindow.Ticks) return;
            }
            else
            {
                _downstream[sideKey] = now.Ticks;
            }

            _seen++;
            if (!_map.TryGetValue(key, out e!))
            {
                // 别名过多时先清一批过期的，避免慢速泄漏
                if (_map.Count >= MaxEntries) Prune(now);

                e = new Entry
                {
                    Device = device, Peer = peer, Port = peerPort, Proto = proto,
                    FirstTicks = now.Ticks, LastTicks = now.Ticks,
                };
                _map[key] = e;
            }

            if (fromDevice) e.Tx += bytes; else e.Rx += bytes;
            e.LastTicks = now.Ticks;
            _dirty = true;
        }

        // 以下两条只处理"从 device 出去的第 4 层首包"，不做全量解析
        if (payload.Length < 12) return;

        if (proto == 6 && peerPort == 443 && fromDevice && payload[0] == 0x16)
        {
            if (TlsSni.TryGetServerName(payload, out var host))
                lock (_sync) { e.Domain = host; _dirty = true; }
            return;
        }

        // DNS 应答：把 A 记录里的 IP 归到问题段的域名上（对端端口 53）
        if (proto == 17 && peerPort == 53 && !fromDevice)
        {
            if (!DnsParser.TryReadQuestion(payload, out var qname, out _)) return;
            DnsParser.ReadAnswers(payload, qname, (name, ip) => RememberName(ip, name, now));
        }
    }

    private void RememberName(uint ip, string name, DateTime now)
    {
        if (name.Length == 0 || name.Length > 253) return;
        lock (_sync)
        {
            if (_ipName.Count >= MaxNames) ForgetExpired(now);
            if (_ipName.Count >= MaxNames) return;
            _ipName[ip] = new NameHit(name, now.Add(NameTtl).Ticks);
        }
    }

    private void ForgetExpired(DateTime now)
    {
        var t = now.Ticks;
        var dead = new List<uint>();
        foreach (var kv in _ipName)
            if (kv.Value.ExpiresTicks < t) dead.Add(kv.Key);
        foreach (var k in dead) _ipName.Remove(k);
    }

    // ════════════════════════════════════════════════════════════
    //  查询 / 维护
    // ════════════════════════════════════════════════════════════

    /// <summary>清空全部记录（界面上的"清空记录"）</summary>
    public IReadOnlyList<AccessEntry> Snapshot(int max = 3000)
    {
        var now = DateTime.UtcNow;
        lock (_sync)
        {
            var list = new List<AccessEntry>(Math.Min(_map.Count, max));
            foreach (var e in _map.Values)
            {
                var name = e.Domain;
                if (name.Length == 0 && _ipName.TryGetValue(e.Peer, out var hit) && hit.ExpiresTicks > now.Ticks)
                    name = hit.Name;

                list.Add(new AccessEntry(
                    DeviceLabel(e.Device),
                    name,
                    DnsParser.IpText(e.Peer),
                    e.Port,
                    e.Proto == 6 ? "TCP" : "UDP",
                    e.Rx, e.Tx,
                    new DateTime(e.FirstTicks, DateTimeKind.Utc).ToLocalTime(),
                    new DateTime(e.LastTicks, DateTimeKind.Utc).ToLocalTime()));
            }

            // 最近活跃的排前面
            list.Sort((a, b) => b.Last.CompareTo(a.Last));
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }
    }

    /// <summary>告诉它哪些地址是本机自己的（用于把"本机"和"下游设备"分开显示）</summary>
    public void SetSelfIps(IEnumerable<uint> ips)
    {
        lock (_sync)
        {
            _selfIps.Clear();
            foreach (var ip in ips) _selfIps.Add(ip);
        }
    }

    private string DeviceLabel(uint ip)
    {
        lock (_sync)
        {
            var text = DnsParser.IpText(ip);
            return _selfIps.Contains(ip) ? $"本机({text})" : text;
        }
    }

    public void Clear()
    {
        lock (_sync) { _map.Clear(); _ipName.Clear(); _downstream.Clear(); _dirty = true; }
        Save(force: true);
    }

    /// <summary>每秒调一次：定期落盘 + 清理过期条目</summary>
    public void Pump()
    {
        var now = Environment.TickCount64;
        if (now - _lastPersistTicks < 60_000) return;
        _lastPersistTicks = now;
        PumpCore();
    }

    private void PumpCore()
    {
        try
        {
            lock (_sync) Prune(DateTime.UtcNow);
            if (_dirty) Save(force: false);
        }
        catch (Exception ex) { _lastError = ex.Message; }
    }

    private void Prune(DateTime nowUtc)
    {
        var days = _cfg.AccessLogRetentionDays <= 0 ? 30 : _cfg.AccessLogRetentionDays;
        var cutoff = nowUtc.AddDays(-days).Ticks;

        var dead = new List<Key>();
        foreach (var kv in _map)
            if (kv.Value.LastTicks < cutoff) dead.Add(kv.Key);
        foreach (var k in dead) _map.Remove(k);

        // 还是太多时按最后活跃时间砍掉最旧的一半
        if (_map.Count > MaxEntries)
        {
            var ordered = _map.OrderBy(kv => kv.Value.LastTicks).Take(_map.Count - MaxEntries / 2).ToList();
            foreach (var kv in ordered) _map.Remove(kv.Key);
        }

        ForgetExpired(nowUtc);

        // 下游侧记录只用于"这条是不是转发来的"，留 10 分钟足够
        var keep = nowUtc.AddMinutes(-10).Ticks;
        var stale = new List<(uint, ushort, byte)>();
        foreach (var kv in _downstream)
            if (kv.Value < keep) stale.Add(kv.Key);
        foreach (var k in stale) _downstream.Remove(k);
    }

    // ════════════════════════════════════════════════════════════
    //  持久化
    // ════════════════════════════════════════════════════════════

    private sealed class Dto
    {
        public int version { get; set; } = 1;
        public List<DtoEntry> entries { get; set; } = new();
    }

    private sealed class DtoEntry
    {
        public string device { get; set; } = "";
        public string peer { get; set; } = "";
        public int port { get; set; }
        public string proto { get; set; } = "";
        public string domain { get; set; } = "";
        public long rx { get; set; }
        public long tx { get; set; }
        public string first { get; set; } = "";
        public string last { get; set; } = "";
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath));
            if (dto?.entries == null) return;

            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-(_cfg.AccessLogRetentionDays <= 0 ? 30 : _cfg.AccessLogRetentionDays)).Ticks;

            foreach (var d in dto.entries)
            {
                if (!TryParseIp(d.device.StartsWith("本机") ? Mid(d.device) : d.device, out var dev)) continue;
                if (!TryParseIp(d.peer, out var peer)) continue;
                if (!DateTime.TryParse(d.last, out var last)) continue;

                if (last.ToUniversalTime().Ticks < cutoff) continue;

                var proto = d.proto == "TCP" ? (byte)6 : (byte)17;
                var k = new Key(dev, peer, (ushort)d.port, proto);
                _map[k] = new Entry
                {
                    Device = dev, Peer = peer, Port = (ushort)d.port, Proto = proto,
                    Domain = d.domain,
                    Rx = d.rx, Tx = d.tx,
                    FirstTicks = DateTime.TryParse(d.first, out var f) ? f.ToUniversalTime().Ticks : last.ToUniversalTime().Ticks,
                    LastTicks = last.ToUniversalTime().Ticks,
                };
                if (d.domain.Length > 0) _ipName[peer] = new NameHit(d.domain, now.Add(NameTtl).Ticks);
            }
        }
        catch (Exception ex) { _lastError = "读取访问记录失败: " + ex.Message; }
    }

    private static string Mid(string s)
    {
        var a = s.IndexOf('(');
        var b = s.IndexOf(')', a + 1);
        return a >= 0 && b > a ? s[(a + 1)..b] : s;
    }

    private static bool TryParseIp(string s, out uint ip)
    {
        ip = 0;
        var parts = s.Split('.');
        if (parts.Length != 4) return false;
        for (var i = 0; i < 4; i++)
        {
            if (!byte.TryParse(parts[i], out var b)) return false;
            ip = (ip << 8) | b;
        }
        return true;
    }

    public void Save(bool force)
    {
        Dto dto;
        lock (_sync)
        {
            if (!_dirty && !force) return;
            dto = new Dto();
            foreach (var e in _map.Values)
            {
                dto.entries.Add(new DtoEntry
                {
                    device = DeviceLabel(e.Device),
                    peer = DnsParser.IpText(e.Peer),
                    port = e.Port,
                    proto = e.Proto == 6 ? "TCP" : "UDP",
                    domain = e.Domain,
                    rx = e.Rx, tx = e.Tx,
                    first = new DateTime(e.FirstTicks, DateTimeKind.Utc).ToLocalTime().ToString("o"),
                    last = new DateTime(e.LastTicks, DateTimeKind.Utc).ToLocalTime().ToString("o"),
                });
            }
            _dirty = false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { _lastError = "保存访问记录失败: " + ex.Message; }
    }

    // ════════════════════════════════════════════════════════════
    //  内部类型
    // ════════════════════════════════════════════════════════════

    private readonly struct Key : IEquatable<Key>
    {
        public readonly uint Device, Peer;
        public readonly ushort Port;
        public readonly byte Proto;

        public Key(uint device, uint peer, ushort port, byte proto)
        {
            Device = device; Peer = peer; Port = port; Proto = proto;
        }

        public bool Equals(Key o) => Device == o.Device && Peer == o.Peer && Port == o.Port && Proto == o.Proto;
        public override bool Equals(object? o) => o is Key k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(Device, Peer, Port, Proto);
    }

    private sealed class Entry
    {
        public uint Device, Peer;
        public ushort Port;
        public byte Proto;
        public string Domain = "";
        public long Rx, Tx;
        public long FirstTicks, LastTicks;
    }

    private readonly record struct NameHit(string Name, long ExpiresTicks);
}
