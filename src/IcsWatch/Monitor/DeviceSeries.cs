namespace IcsWatch.Monitor;

/// <summary>
/// 每台下游设备一条速率曲线。
///
/// 数据源是 <see cref="TrafficLedger"/> 的按设备累计字节（抓包得来），每秒取一次差值。
/// 两级保留策略，目的是让 24 小时窗口也不至于每个点都按秒存：
///   · 秒级：保留 1 小时（3600 点）—— 60 秒 / 10 分钟 / 1 小时窗口用它；
///   · 分钟级：保留 24 小时（1440 点）—— 24 小时窗口用它。
/// 每点 24 字节，单设备约 120 KB，十台设备 1.2 MB，可以接受。
/// </summary>
public sealed class DeviceSeries
{
    public sealed record Point(DateTime Time, double RxMbps, double TxMbps);

    public sealed class Series
    {
        public string Ip { get; init; } = "";
        public string Label { get; set; } = "";
        /// <summary>调色板下标 —— 设备第一次出现时分配，之后不变，颜色才不会跳</summary>
        public int Slot { get; init; }
        public double RxMbps { get; internal set; }
        public double TxMbps { get; internal set; }
        public DateTime LastActive { get; internal set; }

        internal readonly List<Point> Sec = new();
        internal readonly List<Point> Min = new();
        internal long LastRx, LastTx;
        internal DateTime LastTime;
        internal long AccRx, AccTx;
        internal DateTime AccFrom;

        public IReadOnlyList<Point> Points { get; internal set; } = Array.Empty<Point>();
    }

    private const int SecCapacity = 3600;      // 1 秒一点，留 1 小时
    private const int MinCapacity = 1440;      // 1 分钟一点，留 24 小时
    private const double MinBucketSec = 60;

    /// <summary>多久没动静就不再给它记 0 速率（否则一天里所有历史 IP 都会挂一条平线在图例里）</summary>
    private const int ActiveWindowSec = 600;
    /// <summary>闲置这么久就把这条线彻底丢掉，图例跟着变短</summary>
    private const int DropAfterSec = 1200;

    private readonly Dictionary<string, Series> _map = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private int _nextSlot;
    private DateTime _lastPurge = DateTime.MinValue;

    public bool Paused { get; set; }

    /// <summary>每秒调一次。hosts 用账本快照（SessionRx/SessionTx 作为累计基准，跨天也不会回绕）。</summary>
    public void Pump(IReadOnlyList<HostUsage> hosts)
    {
        if (Paused) return;
        var now = DateTime.Now;

        lock (_sync)
        {
            var activeFrom = now.AddSeconds(-ActiveWindowSec);

            foreach (var h in hosts)
            {
                // 只有"最近还在说话"的设备才配一条线。
                // 账本里会一直留着今天见过的所有地址（换 IP 的旧记录、已走的设备），
                // 全给它们画线的话图例会被十几个 0 速率的 IP 挤满。
                if (h.LastSeen < activeFrom && !_map.ContainsKey(h.Ip)) continue;
                if (h.LastSeen < activeFrom) continue;

                if (!_map.TryGetValue(h.Ip, out var s))
                {
                    s = new Series { Ip = h.Ip, Label = h.Label, Slot = _nextSlot++ };
                    s.LastRx = h.SessionRx;
                    s.LastTx = h.SessionTx;
                    s.LastTime = now;
                    s.AccFrom = now;
                    _map[h.Ip] = s;
                    continue;
                }

                if (!string.IsNullOrEmpty(h.Label) && s.Label != h.Label) s.Label = h.Label;
                s.LastActive = h.LastSeen;

                var dt = (now - s.LastTime).TotalSeconds;
                if (dt < 0.2) continue;

                // 计数器只会增长（仅在程序重启时归零），负数说明刚重启，按 0 算
                var drx = Math.Max(0, h.SessionRx - s.LastRx);
                var dtx = Math.Max(0, h.SessionTx - s.LastTx);
                s.LastRx = h.SessionRx;
                s.LastTx = h.SessionTx;
                s.LastTime = now;

                s.RxMbps = drx * 8.0 / 1_000_000.0 / dt;
                s.TxMbps = dtx * 8.0 / 1_000_000.0 / dt;

                s.Sec.Add(new Point(now, s.RxMbps, s.TxMbps));
                if (s.Sec.Count > SecCapacity) s.Sec.RemoveRange(0, s.Sec.Count - SecCapacity);

                // 分钟聚合：把这一分钟里收到的字节摊到整分钟，得到平均速率
                s.AccRx += drx;
                s.AccTx += dtx;
                var bucket = (now - s.AccFrom).TotalSeconds;
                if (bucket >= MinBucketSec)
                {
                    s.Min.Add(new Point(now,
                        s.AccRx * 8.0 / 1_000_000.0 / bucket,
                        s.AccTx * 8.0 / 1_000_000.0 / bucket));
                    if (s.Min.Count > MinCapacity) s.Min.RemoveRange(0, s.Min.Count - MinCapacity);
                    s.AccRx = 0;
                    s.AccTx = 0;
                    s.AccFrom = now;
                }
            }

            // 定期清掉长期没动静的线（30 秒看一眼就够）
            if ((now - _lastPurge).TotalSeconds >= 30)
            {
                _lastPurge = now;
                foreach (var dead in _map.Where(kv => (now - kv.Value.LastTime).TotalSeconds > DropAfterSec)
                                         .Select(kv => kv.Key).ToList())
                    _map.Remove(dead);
            }
        }
    }

    /// <summary>取某个时间窗内所有设备的曲线（按当前速率从高到低，图例顺序与曲线颜色一致）</summary>
    public List<Series> Window(int seconds)
    {
        var from = DateTime.Now.AddSeconds(-seconds);
        var useMinute = seconds > SecCapacity;

        List<Series> list;
        lock (_sync)
        {
            foreach (var s in _map.Values)
            {
                var src = useMinute ? s.Min : s.Sec;
                s.Points = src.Count == 0
                    ? Array.Empty<Point>()
                    : src.Where(p => p.Time >= from).ToArray();
            }

            list = _map.Values
                .Where(s => s.Points.Count >= 1)
                .OrderByDescending(s => Math.Max(s.RxMbps, s.TxMbps))
                .ThenBy(s => s.Ip, StringComparer.Ordinal)
                .ToList();
        }
        return list;
    }

    /// <summary>清空（"清零全部"时调用）</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _map.Clear();
            _nextSlot = 0;
        }
    }
}
