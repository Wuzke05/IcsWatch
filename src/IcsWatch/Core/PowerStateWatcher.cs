using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace IcsWatch.Core;

/// <summary>
/// 电源状态感知 —— 休眠 / 唤醒期间"不要动 ICS"。
///
/// 【为什么需要】网络栈还没重新收敛时，HNetCfg 的 EnableSharing / DisableSharing
/// 会抛 COMException 0x8007030F（"系统电源状态正从 … 转换，但可能进入 …"）。
/// 2026-10-18 一天出现 5 次，全部落在唤醒后几十秒内的自动维修里，
/// 而且**一次失败会连带后面 7 项一起失败**（服务已停、静态地址没写、共享没开…），
/// 那天的日志全是"部分失败 7 项"。
///
/// 【三层防护】
///  ① 唤醒后进入"稳定期"（默认 25 秒，可配）：Engine 不发起自动维修；
///  ② 维修入口 <see cref="WaitStable"/>：已经在跑的维修先等稳定期结束再动手；
///  ③ IcsShareManager 里对 0x8007030F 做退避重试（见 <see cref="IsPowerStateError"/>）。
/// 任何一层单独都能挡住大部分情况；叠起来是为了"正在修的"也能安全收场。
///
/// 【只在 GUI 进程订阅】SystemEvents 会起自己的隐藏窗口线程，CLI 单次执行没必要付这个代价；
/// 所以 <see cref="Start"/> 只由 Engine 调用，<see cref="WaitStable"/> 在未订阅时直接返回
/// （CLI 场景由第 ③ 层兜底）。
/// </summary>
public static class PowerStateWatcher
{
    /// <summary>ERROR 0x030F —— 电源状态转换期间调用网络配置 API 的经典失败码</summary>
    private const int PowerStateHResult = unchecked((int)0x8007030F);

    private static readonly object Gate = new();
    private static bool _started;
    private static int _settleSec = 25;
    private static DateTime _settleUntil = DateTime.MinValue;
    private static DateTime _lastResume = DateTime.MinValue;
    private static int _resumeCount;

    /// <summary>是否已订阅系统电源事件（= 当前是 GUI 进程）</summary>
    public static bool Attached { get { lock (Gate) return _started; } }

    /// <summary>上次唤醒时间（未唤醒过为 null）</summary>
    public static DateTime? LastResumeAt
    {
        get { lock (Gate) return _lastResume == DateTime.MinValue ? null : _lastResume; }
    }

    /// <summary>累计唤醒次数（用于日志/排障）</summary>
    public static int ResumeCount { get { lock (Gate) return _resumeCount; } }

    /// <summary>稳定期还剩几秒（0 = 可以动网络）</summary>
    public static int SettleRemainingSec
    {
        get
        {
            lock (Gate)
            {
                var left = (_settleUntil - DateTime.Now).TotalSeconds;
                return left <= 0 ? 0 : (int)Math.Ceiling(left);
            }
        }
    }

    /// <summary>当前是否处在"刚唤醒、先别动"的稳定期</summary>
    public static bool InSettleWindow => SettleRemainingSec > 0;

    /// <summary>开始监听（幂等）。settleSec = 唤醒后多少秒内不自动维修。</summary>
    public static void Start(int settleSec)
    {
        lock (Gate)
        {
            _settleSec = Math.Clamp(settleSec, 0, 600);
            if (_started) return;
            _started = true;
        }

        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            AppLog.Info($"[电源] 已订阅休眠/唤醒事件（唤醒后稳定期 {_settleSec}s）");
        }
        catch (Exception ex)
        {
            AppLog.Warn("[电源] 订阅电源事件失败（将只依赖重试兜底）: " + AppLog.Ex(ex));
            lock (Gate) _started = false;
        }
    }

    /// <summary>停止监听（退出时调用，避免残留订阅）</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            if (!_started) return;
            _started = false;
        }
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
    }

    /// <summary>界面上改了"唤醒后稳定期"立即生效</summary>
    public static void SetSettleSeconds(int sec) => _settleSec = Math.Clamp(sec, 0, 600);

    /// <summary>
    /// 等到"可以安全动网络"为止。返回 false = 等满了 maxWaitMs 仍在稳定期
    /// （调用方自己决定继续还是放弃；目前维修流程选择继续，只记一条日志）。
    /// </summary>
    public static bool WaitStable(int maxWaitMs, Action<string>? log = null)
    {
        if (!Attached) return true;                  // CLI 进程不订阅，也就没有稳定期概念
        var deadline = DateTime.Now.AddMilliseconds(Math.Max(0, maxWaitMs));
        var announced = false;

        while (true)
        {
            var left = SettleRemainingSec;
            if (left <= 0) return true;

            if (!announced)
            {
                announced = true;
                log?.Invoke($"电源状态刚切换 · 等 {left}s 稳定后再动网络（避免 0x8007030F）");
                AppLog.Info($"[电源] 维修暂缓：稳定期剩 {left}s");
            }

            if (DateTime.Now >= deadline)
            {
                log?.Invoke($"稳定期未结束（剩 {SettleRemainingSec}s），仍继续执行");
                return false;
            }
            Thread.Sleep(400);
        }
    }

    /// <summary>异常链里是否含"电源状态转换"失败码（TargetInvocationException 会包一层）</summary>
    public static bool IsPowerStateError(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e.HResult == PowerStateHResult) return true;
            if (e is COMException com && com.HResult == PowerStateHResult) return true;
        }
        return false;
    }

    private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                // 休眠前的转换窗口：这段时间起就不要启动新的网络操作。
                // 给 10 秒而不是"一直到醒"—— 休眠可能被别的程序否决，不能用超长阻塞。
                lock (Gate) _settleUntil = DateTime.Now.AddSeconds(10);
                AppLog.Info("[电源] 系统即将休眠 · 暂停网络操作");
                break;

            case PowerModes.Resume:
                int sec;
                lock (Gate)
                {
                    sec = _settleSec;
                    _lastResume = DateTime.Now;
                    _resumeCount++;
                    _settleUntil = DateTime.Now.AddSeconds(sec);
                }
                AppLog.Info($"[电源] 系统已唤醒（第 {ResumeCount} 次）· {sec}s 稳定期内不自动维修" +
                            "（唤醒后 ICS / 路由常需重新收敛）");
                break;
        }
    }
}
