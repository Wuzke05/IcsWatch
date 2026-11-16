using System.Net.NetworkInformation;
using IcsWatch.Core;

namespace IcsWatch.Monitor;

public sealed class TrafficSample
{
    public DateTime Time { get; init; }
    public double RxMbps { get; init; }
    public double TxMbps { get; init; }
    public long TotalRxBytes { get; init; }
    public long TotalTxBytes { get; init; }
    public long RxDiscard { get; init; }
    public long TxDiscard { get; init; }
}

/// <summary>
/// 卡1（WiFi 线路）的流量采样 —— 线路级 L1。
///
/// 采集点只有一个：卡1。因为 "卡1 -> 路由器 -> WiFi -> 手机" 是这条线路的唯一出口，
/// 卡1 的计数器就等于整条线路的总量。零依赖（NetworkInterface），1 秒粒度开销可忽略。
/// </summary>
public sealed class TrafficSampler
{
    private string _adapterName;
    /// <summary>当前采样的网卡名（游戏模式下会切到手机 USB 共享）</summary>
    public string AdapterName => _adapterName;

    /// <summary>
    /// 切换采样网卡。游戏模式下卡1 被禁用、计数器恒为 0，
    /// 必须改采手机 USB 网卡，流量页才有意义。切换会重置计数器基线。
    /// </summary>
    public void SetAdapter(string adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) return;
        if (string.Equals(_adapterName, adapterName, StringComparison.OrdinalIgnoreCase)) return;
        _adapterName = adapterName;
        _ni = null;                  // 换网卡 → 丢掉缓存对象
        Sample(reset: true);
    }

    // 【必须缓存网卡对象】实测 NetworkInterface.GetAllNetworkInterfaces() 单次约 103 毫秒，
    // 而拿缓存对象读 GetIPv4Statistics() 只要 0.1 毫秒。
    // 每秒枚举一次 = 白烧 10% 的 CPU（这是之前"空闲 38% 一个核"的最大来源）。
    private NetworkInterface? _ni;

    private NetworkInterface? Resolve()
    {
        if (_ni != null && string.Equals(_ni.Name, _adapterName, StringComparison.OrdinalIgnoreCase)) return _ni;
        _ni = NetworkInterface.GetAllNetworkInterfaces()
                 .FirstOrDefault(n => string.Equals(n.Name, _adapterName, StringComparison.OrdinalIgnoreCase));
        return _ni;
    }
    private readonly int _capacity;

    private long _lastRx, _lastTx;
    private long _lastDiscardRx, _lastDiscardTx;
    private DateTime _lastTime = DateTime.MinValue;
    private bool _primed;

    private readonly LinkedList<TrafficSample> _history = new();

    public TrafficSample? Latest { get; private set; }
    public string LastError { get; private set; } = "";

    public TrafficSampler(string adapterName, int historyMinutes, int intervalSec)
    {
        _adapterName = adapterName;
        var points = Math.Max(60, (historyMinutes * 60) / Math.Max(1, intervalSec));
        _capacity = Math.Min(points, 200_000);
    }

    public IReadOnlyCollection<TrafficSample> History => _history;

    /// <summary>采一次样。返回本次增量样本；数据不可用时返回 null。</summary>
    public TrafficSample? Sample(bool reset = false)
    {
        try
        {
            var ni = Resolve();
            if (ni == null)
            {
                LastError = $"找不到网卡 {_adapterName}";
                return null;
            }

            var st = ni.GetIPv4Statistics();
            var now = DateTime.Now;

            var rx = st.BytesReceived;
            var tx = st.BytesSent;
            var drx = st.IncomingPacketsDiscarded;
            var dtx = st.OutgoingPacketsDiscarded;

            if (!_primed || reset)
            {
                _lastRx = rx; _lastTx = tx;
                _lastDiscardRx = drx; _lastDiscardTx = dtx;
                _lastTime = now;
                _primed = true;
                var idle = new TrafficSample
                {
                    Time = now, RxMbps = 0, TxMbps = 0,
                    TotalRxBytes = rx, TotalTxBytes = tx,
                    RxDiscard = drx, TxDiscard = dtx,
                };
                Latest = idle;
                Push(idle);
                return idle;
            }

            var dt = (now - _lastTime).TotalSeconds;
            if (dt <= 0.05) return Latest;

            var dRx = Math.Max(0, rx - _lastRx);
            var dTx = Math.Max(0, tx - _lastTx);

            var s = new TrafficSample
            {
                Time = now,
                RxMbps = dRx * 8.0 / 1_000_000.0 / dt,
                TxMbps = dTx * 8.0 / 1_000_000.0 / dt,
                TotalRxBytes = rx,
                TotalTxBytes = tx,
                RxDiscard = drx,
                TxDiscard = dtx,
            };

            _lastRx = rx; _lastTx = tx;
            _lastDiscardRx = drx; _lastDiscardTx = dtx;
            _lastTime = now;

            Latest = s;
            Push(s);
            return s;
        }
        catch (Exception ex)
        {
            LastError = AppLog.Ex(ex);
            _ni = null;              // 缓存对象可能已失效（网卡重建），下次重新枚举
            return null;
        }
    }

    /// <summary>取最近 n 秒的历史（用于不同时间窗的曲线）</summary>
    public List<TrafficSample> Window(int seconds)
    {
        var from = DateTime.Now.AddSeconds(-seconds);
        return _history.Where(s => s.Time >= from).ToList();
    }

    private void Push(TrafficSample s)
    {
        _history.AddLast(s);
        while (_history.Count > _capacity) _history.RemoveFirst();
    }
}
