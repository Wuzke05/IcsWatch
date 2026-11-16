using System.Net.NetworkInformation;
using IcsWatch.Config;
using IcsWatch.Monitor;

namespace IcsWatch.Core;

/// <summary>
/// 后台引擎：定时体检 + 流量采样 + 事件驱动即时检查 + 自动维修。
/// 与界面完全解耦 —— 关掉主窗口后它照常工作（"挂后台"）。
/// </summary>
public sealed class Engine : IDisposable
{
    private readonly AppConfig _cfg;
    private System.Threading.Timer? _probeTimer;
    private System.Threading.Timer? _trafficTimer;
    private int _probing;                 // 防重入
    private bool _disposed;

    private DateTime _lastRepairAt = DateTime.MinValue;
    private readonly Queue<DateTime> _repairTimes = new();
    private DateTime _startedAt = DateTime.Now;

    private System.Threading.Timer? _bootTimer;
    private volatile bool _bootRebuildPending;
    /// <summary>
    /// 临时抑制自动维修。用于"游戏模式进入/退出"这类**直接调 Repairer**、
    /// 不走 IsRepairing 的长操作 —— 否则期间 5 秒体检会看到中间状态并插一脚。
    /// </summary>
    public volatile bool SuppressAutoRepair;
    private DateTime _bootRebuildAt = DateTime.MinValue;

    // ── 游戏模式自动切换 ────────────────────────────────────────
    private DateTime _autoUsbOkSince = DateTime.MinValue;      // USB 持续可用起点
    private DateTime _autoUsbBadSince = DateTime.MinValue;     // USB 持续不可用起点
    private DateTime _autoCooldownUntil = DateTime.MinValue;   // 切换后冷却（只限制"进入"）
    private volatile bool _autoBusy;
    private bool _autoLatchOff;                                // 手动退出后锁住自动进入
    private bool _lastGameActive;                              // 上一轮 gameModeActive（检测外部改动）
    private bool? _liteApplied;                                // 当前是否已应用"轻量化"配置
    private DateTime _cfgSyncAt = DateTime.MinValue;
    private DateTime _cfgFileStamp = DateTime.MinValue;
    public bool IsBootRebuildPending => _bootRebuildPending;

    public event Action<CheckSnapshot>? SnapshotReady;
    public event Action<TrafficSample>? TrafficReady;
    public event Action<HealthState, string>? StateChanged;
    public event Action<RepairRecord>? RepairFinished;
    public event Action<string>? RepairProgress;
    public event Action? RepairStarted;

    public CheckSnapshot? Latest { get; private set; }
    public HealthState State { get; private set; } = HealthState.Unknown;
    public bool IsRepairing { get; private set; }
    public TrafficSampler Sampler { get; }
    /// <summary>用量账本：总量（出口网卡计数）+ 按设备（私有端抓包）</summary>
    public TrafficLedger Traffic { get; }
    /// <summary>每设备速率曲线（给流量页的多条线用）</summary>
    public DeviceSeries Devices { get; }
    public List<RepairRecord> Repairs { get; } = new();
    public bool InGracePeriod => (DateTime.Now - _startedAt).TotalSeconds < _cfg.StartupGraceSec;

    public Engine(AppConfig cfg)
    {
        _cfg = cfg;
        Sampler = new TrafficSampler(cfg.PrivateAdapter, cfg.HistoryMinutes, cfg.TrafficIntervalSec);
        Traffic = new TrafficLedger(cfg);
        Devices = new DeviceSeries();
    }

    public void Start()
    {
        AppLog.Info($"引擎启动: 体检 {_cfg.ProbeIntervalSec}s / 流量 {_cfg.TrafficIntervalSec}s / 维修模式 {_cfg.RepairMode}");

        // 电源事件订阅：休眠/唤醒期间不动 ICS（0x8007030F 的来源，见 PowerStateWatcher）
        PowerStateWatcher.Start(_cfg.PowerSettleSec);

        // 【启动自检】配置说"不在游戏模式"，但网卡还是被管理性禁用 —— 那是上次游戏模式
        // 留下的残留（Windows 会跨重启保留"设备已禁用"）。自动解除，
        // 避免用户开机发现"网卡2 莫名被禁用"却查不到是谁干的。
        if (!_cfg.GameModeActive)
            GameMode.HealOrphanDisabled(_cfg, s => AppLog.Info("[自检] " + s));

        _probeTimer = new System.Threading.Timer(_ => ProbeTick("定时"), null, 800, _cfg.ProbeIntervalSec * 1000);
        _trafficTimer = new System.Threading.Timer(_ => TrafficTick(), null, 300, _cfg.TrafficIntervalSec * 1000);

        // 用量账本：读历史 → 起抓包线程。失败只记日志，绝不能拖住引擎启动。
        try { Traffic.Start(); }
        catch (Exception ex) { AppLog.Warn("用量统计启动失败: " + AppLog.Ex(ex)); }

        // ── 启动后自动全量重建（可选）─────────────────────────────
        // 原来这一步是外部计划任务「ICS Boot Fix」跑的；现在可以交给 app 自己做，
        // 好处是只有一套实现、不会两套逻辑互抢，而且延迟秒数可自定义。
        // 只在"真的刚开机"时做启动全量重建。
        // 否则用户每重启一次程序就断一次网（曾经因为看门狗被任务时限杀掉、
        // 每 6 分钟重启一次，导致全量重建每 6 分钟跑一遍 —— 见 fix 记录）。
        var upMin = TimeSpan.FromMilliseconds(Environment.TickCount64).TotalMinutes;
        if (_cfg.GameModeActive && _cfg.GameExitOnBoot && upMin <= 20)
        {
            // 开机自动退出游戏模式：关机时在游戏模式会让"设备已禁用"跨重启保留下来，
            // 开机后卡1/卡2 仍处于禁用状态。这里在启动 5 秒后自动恢复。
            // 退出流程内部自带一次全量重建，所以不再单独跑启动重建。
            const int delay = 5;
            _bootRebuildPending = true;
            _bootRebuildAt = DateTime.Now.AddSeconds(delay);
            AppLog.Info($"游戏模式：已启用「开机自动退出」，{delay} 秒后恢复网络");
            _bootTimer = new System.Threading.Timer(_ => BootExitGameModeTick(), null, delay * 1000, Timeout.Infinite);
        }
        else if (_cfg.BootRebuildEnabled && _cfg.GameModeActive)
        {
            AppLog.Info("游戏模式已开启，跳过启动全量重建");
        }
        else if (_cfg.BootRebuildEnabled && upMin <= 20)
        {
            var delay = Math.Clamp(_cfg.BootRebuildDelaySec, 5, 3600);
            _bootRebuildPending = true;
            _bootRebuildAt = DateTime.Now.AddSeconds(delay);
            AppLog.Info($"已安排启动后 {delay} 秒自动执行一次全量重建");
            _bootTimer = new System.Threading.Timer(_ => BootRebuildTick(), null, delay * 1000, Timeout.Infinite);
        }
        else if (_cfg.BootRebuildEnabled)
        {
            AppLog.Info($"开机已 {upMin:0} 分钟，判定为程序重启而非开机，跳过启动全量重建");
        }

        // 事件驱动：IP 变化立即复查（毫秒级响应，不用等定时器）
        try
        {
            NetworkChange.NetworkAddressChanged += (_, _) => ProbeTick("网络地址变化");
            NetworkChange.NetworkAvailabilityChanged += (_, _) => ProbeTick("网络可用性变化");
        }
        catch (Exception ex) { AppLog.Warn("订阅网络事件失败: " + AppLog.Ex(ex)); }
    }

    /// <summary>
    /// 游戏模式 + 轻量化时：体检/采样定时器降频、进程优先级降级。
    /// 退出游戏模式后自动恢复。只在状态真的变化时才动（避免每轮都改）。
    /// </summary>
    private void SyncLiteMode()
    {
        var want = _cfg.GameModeLite && _cfg.GameModeActive;
        if (_liteApplied == want) return;
        _liteApplied = want;

        var probeSec = Math.Max(2, want ? _cfg.GameProbeIntervalSec : _cfg.ProbeIntervalSec);
        var trafficSec = Math.Max(1, want ? _cfg.GameTrafficIntervalSec : _cfg.TrafficIntervalSec);

        try { _probeTimer?.Change(probeSec * 1000, probeSec * 1000); }
        catch (Exception ex) { AppLog.Swallow("Engine.SyncLiteMode(probe)", ex); }
        try { _trafficTimer?.Change(trafficSec * 1000, trafficSec * 1000); }
        catch (Exception ex) { AppLog.Swallow("Engine.SyncLiteMode(traffic)", ex); }

        try
        {
            var p = System.Diagnostics.Process.GetCurrentProcess();
            var wantPriority = System.Diagnostics.ProcessPriorityClass.Normal;
            if (want &&
                Enum.TryParse<System.Diagnostics.ProcessPriorityClass>(_cfg.GameProcessPriority, true, out var pc) &&
                pc != System.Diagnostics.ProcessPriorityClass.RealTime &&
                pc != System.Diagnostics.ProcessPriorityClass.High)
                wantPriority = pc;

            if (p.PriorityClass != wantPriority)
            {
                p.PriorityClass = wantPriority;
                AppLog.Info("[轻量] 进程优先级 -> " + wantPriority);
            }
        }
        catch (Exception ex) { AppLog.Swallow("Engine.SyncLiteMode(priority)", ex); }

        AppLog.Info(want
            ? $"[轻量] 游戏模式轻量化已启用：体检 {probeSec}s / 采样 {trafficSec}s / 跳过设备探测"
            : $"[轻量] 已恢复全功能：体检 {probeSec}s / 采样 {trafficSec}s");
    }

    public void Stop()
    {
        _probeTimer?.Dispose(); _probeTimer = null;
        _trafficTimer?.Dispose(); _trafficTimer = null;
        _bootTimer?.Dispose(); _bootTimer = null;
        try { Traffic.Stop(); } catch (Exception ex) { AppLog.Swallow("Engine.Stop(stats)", ex); }
    }

    /// <summary>
    /// 开机自动退出游戏模式（真开机 20 分钟内才触发）。
    /// 退出流程内部会：启用被禁用的网卡 → 确保公网端拿回 DHCP → 全量重建 ICS。
    /// </summary>
    private void BootExitGameModeTick()
    {
        _bootTimer?.Dispose();
        _bootTimer = null;
        // 整个"退出 + 全量重建"期间都抑制轻量自愈（Exit 不走 IsRepairing）
        SuppressAutoRepair = true;
        try
        {
            AppLog.Info("===== 开机自动退出游戏模式开始 =====");
            // 回调传空：SetEnabled 内部已把关键动作写进日志文件（[游戏模式] 前缀），
            // 这里再传一个会重复记两遍
            var r = GameMode.Exit(_cfg, _ => { });
            AppLog.Info("===== 开机自动退出游戏模式结束: " + r.msg + " =====");
            if (r.ok) _startedAt = DateTime.Now.AddSeconds(-_cfg.StartupGraceSec - 1);
        }
        catch (Exception ex)
        {
            AppLog.Error("开机自动退出游戏模式失败: " + AppLog.Ex(ex));
        }
        finally
        {
            SuppressAutoRepair = false;
            _bootRebuildPending = false;
            ProbeTick("开机退出游戏模式后复检");
        }
    }

    private void BootRebuildTick()
    {
        _bootRebuildPending = false;
        _bootTimer?.Dispose();
        _bootTimer = null;
        try
        {
            AppLog.Info("===== 启动自动全量重建开始 =====");
            var rec = RunRepair(full: true, "启动自动全量重建");
            AppLog.Info("===== 启动自动全量重建结束: " + rec.Summary + " =====");

            // 重建成功就把"开机宽容期"提前结束，让后续轻量自愈立刻接管
            if (rec.Success) _startedAt = DateTime.Now.AddSeconds(-_cfg.StartupGraceSec - 1);
        }
        catch (Exception ex)
        {
            AppLog.Error("启动自动全量重建失败: " + ex);
        }
    }

    /// <summary>
    /// 从磁盘同步"游戏模式"相关标志（最多 3 秒一次）。
    /// 用途：CLI 执行 --game-on/--game-off 时会改写 config.json，但已经在跑的 GUI
    /// 手里是启动时读的旧 AppConfig —— 不同步的话 GUI 会拿旧状态去自动维修，
    /// 把刚被禁用的网卡又"修"回来。
    /// </summary>
    private void SyncGameModeFromDisk()
    {
        if ((DateTime.Now - _cfgSyncAt).TotalSeconds < 3) return;
        _cfgSyncAt = DateTime.Now;
        try
        {
            // 只在 config.json 真的被改过时才读盘（原来每 3 秒读一次文件）
            var path = _cfg.ConfigPath;
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (stamp == _cfgFileStamp) return;
            _cfgFileStamp = stamp;

            var fresh = AppConfig.Load();
            _cfg.GameModeActive = fresh.GameModeActive;
            _cfg.GameUsbAdapter = fresh.GameUsbAdapter;
            _cfg.GamePauseAdapters = fresh.GamePauseAdapters;
            _cfg.GameDisabledAdapters = fresh.GameDisabledAdapters;
        }
        catch (Exception __ex) { AppLog.Swallow("Engine.cs:187", __ex); }
    }

    private void TrafficTick()
    {
        try
        {
            var s = Sampler.Sample();
            if (s != null) TrafficReady?.Invoke(s);
        }
        catch (Exception ex) { AppLog.Debug("流量采样异常: " + AppLog.Ex(ex)); }

        // 用量账本每拍都要走一遍：采出口计数、跨天滚动、重启死掉的抓包线程
        try { Traffic.Pump(); }
        catch (Exception ex) { AppLog.Debug("用量账本异常: " + AppLog.Ex(ex)); }

        // 每设备曲线：拿本秒的累计值算差值（和上面同一拍，时钟一致）
        try { Devices.Pump(Traffic.SnapshotHosts()); }
        catch (Exception ex) { AppLog.Debug("设备曲线异常: " + AppLog.Ex(ex)); }
    }

    /// <summary>立即做一次体检（界面按钮 / 事件触发 / 托盘菜单都用这个）</summary>
    public void ProbeTick(string trigger) => Probe(trigger, false);

    public void Probe(string trigger, bool force)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _probing, 1) == 1) return;   // 上一轮还没跑完
        try
        {
            if (IsRepairing && !force) return;

            // CLI（--game-on/--game-off）改了配置文件时，GUI 手里的 AppConfig 还是旧的 ——
            // 必须每轮同步一次游戏模式标志，否则 GUI 会以为"没进游戏模式"，
            // 跑去自动维修刚被主动禁用的网卡（实际踩过：卡1/卡2 被 CLI 禁用后又被 GUI 修回来）。
            SyncGameModeFromDisk();

            var snap = Checker.Run(_cfg, fromEvent: trigger != "定时", trigger: trigger);
            Latest = snap;

            var newState = IsRepairing ? HealthState.Repairing : snap.State;
            // 游戏模式：其余网卡是被我们主动禁用的，别报故障、别自动维修
            if (_cfg.GameModeActive && !IsRepairing) newState = HealthState.GameMode;
            var stateChanged = newState != State;
            State = newState;

            SnapshotReady?.Invoke(snap);
            if (stateChanged) OnStateChanged(newState, snap);

            // 自动进出游戏模式（仅 cfg.GameModeAuto = true 时生效）
            AutoGameModeTick();

            // 游戏模式轻量化：定时器降频 / 优先级（状态变化时才动）
            SyncLiteMode();

            // ── 自动维修判定 ─────────────────────────────────────
            // 【重要修复】旧逻辑是"状态跳变成 Broken 才修一次"，有致命缺陷：
            //   22:55 那次自动维修时网卡还在"正在识别网络"→ 类别修不了 →
            //   状态随即回落成"降级"（不是故障）→ 从此**再也不会重试**，Public 一直挂着。
            // 改成：只要存在"可自动修复且仍非 PASS"的项，就按 冷却期/每小时上限 持续尝试。
            var fixable = snap.FixableProblems;
            if (_cfg.GameModeActive) { AutoRepairNote = "游戏模式 · 仅 USB 出网"; return; }
            if (fixable.Count > 0) MaybeAutoRepair(snap, fixable);
            else AutoRepairNote = "";
        }
        catch (Exception ex)
        {
            AppLog.Error("体检异常: " + ex);
        }
        finally
        {
            Interlocked.Exchange(ref _probing, 0);
        }
    }

    /// <summary>
    /// 自动进出游戏模式（只有 cfg.GameModeAuto = true 时才工作）。
    ///
    /// 判据：配置的 USB 共享网卡「存在 + Up + 有非 169.254 的 IPv4」→ 视为可用。
    /// 防抖：可用/不可用都要**持续**达到配置的秒数才动作（AutoEnterDebounceSec / AutoExitDebounceSec），
    ///       避免插拔瞬间和手机锁屏导致来回切换（每次退出都要重建 ICS，很贵）。
    /// 冷却：一次切换后 AutoModeCooldownSec 内不再自动"进入"；**退出永远允许**（安全优先）。
    /// 锁定：界面/CLI 手动退出游戏模式后，自动进入被锁住，直到 USB 真的消失一次。
    /// </summary>
    private void AutoGameModeTick()
    {
        var active = _cfg.GameModeActive;
        var wasActive = _lastGameActive;
        _lastGameActive = active;

        if (!_cfg.GameModeAuto)
        {
            _autoUsbOkSince = DateTime.MinValue; _autoUsbBadSince = DateTime.MinValue;
            _autoLatchOff = false;
            return;
        }

        // 外部（界面按钮 / CLI --game-off）把游戏模式关了 → 锁住自动进入
        if (wasActive && !active && !_autoBusy)
        {
            _autoLatchOff = true;
            _autoCooldownUntil = DateTime.Now.AddSeconds(Math.Max(10, _cfg.AutoModeCooldownSec));
            AppLog.Info("[自动] 检测到手动退出游戏模式 · 暂停自动进入（USB 拔掉后解除）");
        }

        if (_autoBusy || SuppressAutoRepair || IsRepairing || _bootRebuildPending) return;

        var usb = (_cfg.GameUsbAdapter ?? "").Trim();
        if (usb.Length == 0) return;

        var ad = NetProbe.ReadAdapter(usb);
        var usable = ad.Exists && ad.IsUp &&
                     ad.Ipv4.Any(ip => !ip.StartsWith("169.254.", StringComparison.Ordinal));

        if (!usable && _autoLatchOff)
        {
            _autoLatchOff = false;                    // USB 真的没了 → 解除锁定
            AppLog.Info("[自动] USB 已消失 · 解除自动进入锁定");
        }

        var now = DateTime.Now;
        if (!active && usable)
        {
            _autoUsbBadSince = DateTime.MinValue;
            if (_autoLatchOff || now < _autoCooldownUntil) { _autoUsbOkSince = DateTime.MinValue; return; }

            if (_autoUsbOkSince == DateTime.MinValue)
            {
                _autoUsbOkSince = now;
                AppLog.Info($"[自动] {usb} 可用 · {_cfg.AutoEnterDebounceSec} 秒后进入游戏模式");
            }
            else if ((now - _autoUsbOkSince).TotalSeconds >= _cfg.AutoEnterDebounceSec)
            {
                _autoUsbOkSince = DateTime.MinValue;
                RunAutoTransition(enter: true);
            }
        }
        else if (active && !usable)
        {
            _autoUsbOkSince = DateTime.MinValue;
            if (_autoUsbBadSince == DateTime.MinValue)
            {
                _autoUsbBadSince = now;
                AppLog.Info($"[自动] {usb} 不可用 · {_cfg.AutoExitDebounceSec} 秒后退出游戏模式");
            }
            else if ((now - _autoUsbBadSince).TotalSeconds >= _cfg.AutoExitDebounceSec)
            {
                _autoUsbBadSince = DateTime.MinValue;
                RunAutoTransition(enter: false);
            }
        }
        else
        {
            _autoUsbOkSince = DateTime.MinValue;
            _autoUsbBadSince = DateTime.MinValue;
        }
    }

    /// <summary>执行一次自动切换（后台线程；期间抑制自动维修，结束后复检）</summary>
    private void RunAutoTransition(bool enter)
    {
        if (_autoBusy) return;
        _autoBusy = true;
        SuppressAutoRepair = true;
        AppLog.Info(enter ? "===== 自动进入游戏模式开始 =====" : "===== 自动退出游戏模式开始 =====");

        Task.Run(() =>
        {
            try
            {
                // 回调传空：GameMode 内部已把关键动作写进日志（[游戏模式] 前缀）
                var r = enter ? GameMode.Enter(_cfg, _ => { }) : GameMode.Exit(_cfg, _ => { });
                AppLog.Info($"===== 自动{(enter ? "进入" : "退出")}游戏模式结束: {r.msg} =====");
            }
            catch (Exception ex)
            {
                AppLog.Error("自动切换游戏模式失败: " + AppLog.Ex(ex));
            }
            finally
            {
                _autoCooldownUntil = DateTime.Now.AddSeconds(Math.Max(10, _cfg.AutoModeCooldownSec));
                _lastGameActive = _cfg.GameModeActive;
                _autoBusy = false;
                SuppressAutoRepair = false;
                ProbeTick("自动切换后复检");
            }
        });
    }

    private void OnStateChanged(HealthState s, CheckSnapshot snap)
    {
        var fails = snap.Items.Where(i => i.Status == CheckStatus.Fail).Select(i => i.Name).ToArray();
        var msg = s switch
        {
            // 只放"是哪些项" —— 严重程度由调用方加前缀（日志/托盘标题），避免出现"故障: 故障: xxx"
            HealthState.Broken => string.Join(" / ", fails) +
                                  (InGracePeriod ? " · 宽容期内不自动修" : ""),
            HealthState.Degraded =>
                string.Join(" / ", snap.Items.Where(i => i.Status == CheckStatus.Warn).Take(3).Select(i => i.Name)),
            HealthState.Healthy => "全部正常",
            HealthState.GameMode => "仅 USB 共享出网",
            _ => Checker.StateText(s),
        };
        AppLog.Info($"状态变化 -> {Checker.StateText(s)}: {msg}");
        StateChanged?.Invoke(s, msg);
    }

    private bool _autoRepairInFlight;
    private bool _capWarned;

    /// <summary>
    /// 断网级故障：这些项一挂，下游设备就是"没有网"或者"拿不到地址"。
    /// 它们不受「每小时最多自动维修 N 次」和「维修冷却」限制 —— 限额是防抖动的，不是防断网的。
    /// </summary>
    private static readonly string[] HardFailureKeys =
    {
        CheckKeys.PrivateLink, CheckKeys.PublicLink,
        CheckKeys.SharedAccess, CheckKeys.IpNat, CheckKeys.IcsDhcpListen,
        CheckKeys.IcsPrivate, CheckKeys.IcsPublic, CheckKeys.NatEgress,
        // 注意：CheckKeys.IcsDhcpStuck（"设备要不到地址"）**故意不在这里**。
        // 它不是"已经断了"，而是"有人在重试"——设备自己每几秒就会重发一次 DISCOVER，
        // 而每次重置共享要全断 4 秒，等于为了救一个还没断的设备去踢掉所有已连上的设备。
        // 它按普通故障走限额与冷却，宁可少修，也不要自己制造断网。
    };

    /// <summary>自动维修为什么没在跑（冷却中 / 宽容期 / 已达上限），界面直接显示给用户看</summary>
    public string AutoRepairNote { get; private set; } = "";

    private void MaybeAutoRepair(CheckSnapshot snap, List<CheckItem> fixable)
    {
        if (_cfg.RepairMode.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            AutoRepairNote = "维修模式 off · 只告警";
            return;
        }
        if (SuppressAutoRepair)
        {
            // 游戏模式进入/退出正在进行（内部直接调 Repairer，不走 IsRepairing）
            AutoRepairNote = "游戏模式切换中 · 暂停自愈";
            return;
        }
        if (_bootRebuildPending)
        {
            var left = (int)Math.Max(0, (_bootRebuildAt - DateTime.Now).TotalSeconds);
            AutoRepairNote = $"等待启动重建 · {left}s";
            return;
        }
        if (InGracePeriod)
        {
            var left = (int)Math.Max(0, _cfg.StartupGraceSec - (DateTime.Now - _startedAt).TotalSeconds);
            AutoRepairNote = $"宽容期内 · {left}s 后开始自愈";
            return;
        }
        // 刚唤醒：网络栈 / ICS 还没收敛，这时候修必然撞 0x8007030F 并连带 7 项失败
        if (PowerStateWatcher.InSettleWindow)
        {
            AutoRepairNote = $"唤醒后稳定期 · {PowerStateWatcher.SettleRemainingSec}s 后自愈";
            return;
        }
        if (_autoRepairInFlight)
        {
            AutoRepairNote = "维修进行中…";
            return;
        }

        var now = DateTime.Now;

        // 硬故障（断网级）单独一批：它们的限额与冷却都要让路。
        // 【为什么】每小时 1 次的上限本意是防"反复小抖动引发维修风暴"，不是防"彻底没网"。
        // 现场教训：11:29 修过一次之后，12:11 ICS 引擎死掉时就被锁成"只告警"，
        // 手机卡在"正在获取 IP"十几分钟没人管 —— 限额管错了对象。
        var hard = fixable.Where(i => HardFailureKeys.Contains(i.Key)).ToList();

        var elapsed = (now - _lastRepairAt).TotalSeconds;
        if (elapsed < _cfg.CooldownSec && hard.Count == 0)
        {
            AutoRepairNote = $"冷却中 · {(int)(_cfg.CooldownSec - elapsed)}s 后重试";
            return;
        }

        while (_repairTimes.Count > 0 && (now - _repairTimes.Peek()).TotalHours >= 1) _repairTimes.Dequeue();
        if (_repairTimes.Count < _cfg.MaxRepairPerHour) _capWarned = false;

        if (_repairTimes.Count >= _cfg.MaxRepairPerHour && hard.Count == 0)
        {
            AutoRepairNote = $"已触发 {_repairTimes.Count} 次/小时，达上限，锁定为只告警";
            if (!_capWarned)   // 只告警一次，否则每 5 秒刷屏
            {
                _capWarned = true;
                AppLog.Warn($"自动维修已达 {_cfg.MaxRepairPerHour} 次/小时上限，锁定为只告警；" +
                            $"仍未修好: {string.Join("、", fixable.Select(i => i.Name))}");
            }
            return;
        }

        if (hard.Count > 0 && _repairTimes.Count >= _cfg.MaxRepairPerHour)
            AppLog.Warn($"硬故障不受每小时上限约束，继续维修: {string.Join("、", hard.Select(i => i.Name))}");

        var reason = "自动维修: " + string.Join(" / ",
            fixable.Select(i => i.Name).Take(3));
        AutoRepairNote = "即将自动维修: " + string.Join("、", fixable.Select(i => i.Name).Take(3));

        _autoRepairInFlight = true;
        Task.Run(() =>
        {
            try
            {
                var full = _cfg.RepairMode.Equals("full", StringComparison.OrdinalIgnoreCase);
                RunRepair(full, reason);
            }
            finally { _autoRepairInFlight = false; }
        });
    }

    /// <summary>手动/自动统一入口</summary>
    /// <param name="oldPrivate">角色对调前的私有端名（仅对调时传，用于撤掉旧网卡上的共享/静态地址）</param>
    /// <param name="oldPublic">角色对调前的公网端名</param>
    public RepairRecord RunRepair(bool full, string reason, bool rolesChanged = false,
                                  string? oldPrivate = null, string? oldPublic = null)
    {
        if (IsRepairing)
        {
            var busy = new RepairRecord { Reason = reason, Detail = "已有维修在进行中" };
            busy.Failed.Add("已有维修在进行中");
            return busy;
        }

        IsRepairing = true;
        _lastRepairAt = DateTime.Now;
        _repairTimes.Enqueue(_lastRepairAt);
        RepairStarted?.Invoke();

        var prev = State;
        State = HealthState.Repairing;
        StateChanged?.Invoke(State, "正在维修…");

        RepairRecord rec;
        try
        {
            rec = full
                ? Repairer.RunFull(_cfg, reason, s => RepairProgress?.Invoke(s),
                                   rolesChanged, oldPrivate, oldPublic)
                : Repairer.RunLight(_cfg, reason, s => RepairProgress?.Invoke(s));
        }
        catch (Exception ex)
        {
            rec = new RepairRecord { Reason = reason, Detail = AppLog.Ex(ex) };
            rec.Failed.Add("维修执行异常");
        }
        finally { IsRepairing = false; }

        lock (Repairs) { Repairs.Insert(0, rec); if (Repairs.Count > 100) Repairs.RemoveAt(Repairs.Count - 1); }
        AppLog.Info("维修结果: " + rec.Summary +
                    (string.IsNullOrEmpty(rec.Detail) ? "" : " | " + rec.Detail));
        RepairFinished?.Invoke(rec);

        // 维修完立刻复检
        Thread.Sleep(800);
        Probe("维修后复检", true);

        if (State == HealthState.Repairing) State = prev;
        return rec;
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        PowerStateWatcher.Stop();
    }
}
