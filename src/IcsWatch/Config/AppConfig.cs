using System.Text.Json;
using System.Text.Json.Serialization;

namespace IcsWatch.Config;

/// <summary>
/// 应用配置。首次运行自动生成 config.json，后续热加载（保存时重写）。
/// JSON 采用 camelCase，方便手工编辑。
/// </summary>
public sealed class AppConfig
{
    // ── 网卡与地址基线 ──────────────────────────────────────────
    // 「网卡角色对调」就是交换下面这两行 —— 设置页有一键对调按钮，
    // 交换后必须跑一次全量重建，静态 IP / ICS 共享 / 度量 才会跟着换边。
    public string PrivateAdapter { get; set; } = "以太网";       // 卡1：ICS 私有端（接下游路由器/WiFi）
    public string PublicAdapter  { get; set; } = "以太网 2";     // 卡2：ICS 公网端（校园网）
    public string PrivateIp      { get; set; } = "192.168.137.1";
    public string AltIp          { get; set; } = ""; // 卡1 的从属 IP（可选）：用于访问下游路由器管理页，留空表示不配
    public int    PrivateMetric  { get; set; } = 9999;
    public int    PublicMetric   { get; set; } = 10;

    // ── 监控 ────────────────────────────────────────────────────
    public int  ProbeIntervalSec   { get; set; } = 5;      // 22 项体检周期
    public int  TrafficIntervalSec { get; set; } = 1;      // 流量采样周期
    public int  HistoryMinutes     { get; set; } = 1440;   // 内存保留 24 小时
    public bool DeviceTraffic      { get; set; } = false;  // P3：每设备抓包（默认关，省 CPU）
    /// <summary>用量统计：是否在私有端抓包做"按设备"统计（关掉后只统计总量，零额外开销）</summary>
    public bool TrafficStatsEnabled { get; set; } = true;
    /// <summary>用量统计：抓包网卡名。空 = 自动（平时私有端，游戏模式手机 USB）</summary>
    public string TrafficCaptureAdapter { get; set; } = "";

    /// <summary>
    /// 访问记录：是否记"哪个设备连了哪些地址"。关掉后不解析 DNS/SNI，也不抓公网端。
    /// 默认开着，页面上有开关可以随时关。
    /// </summary>
    public bool AccessLogEnabled { get; set; } = true;

    /// <summary>访问记录：是否连本机自己的流量一起记（要额外抓一块公网端网卡）</summary>
    public bool AccessLogIncludeSelf { get; set; } = true;

    /// <summary>访问记录：保留天数，超期自动清理</summary>
    public int AccessLogRetentionDays { get; set; } = 30;
    public int  StartupGraceSec    { get; set; } = 180;   // 开机宽容期，期间只记录不告警

    /// <summary>
    /// 唤醒后的稳定期（秒）：这段时间内不发起自动维修。
    /// 【为什么】休眠/唤醒的转换窗口里调 ICS 的 EnableSharing 会抛 0x8007030F，
    /// 一次失败会连带后面 7 项一起失败（2026-10-18 一天 5 次）。
    /// 唤醒后网络栈 / ICS 本身也需要几秒重新收敛。
    /// </summary>
    public int  PowerSettleSec     { get; set; } = 25;


    // ── 修复 ────────────────────────────────────────────────────
    /// <summary>off=只告警不修 | light=只修轻量项 | full=含全量重建</summary>
    public string RepairMode       { get; set; } = "light";
    public int    CooldownSec      { get; set; } = 180;
    public int    MaxRepairPerHour { get; set; } = 5;
    public bool   AutoOnStartup    { get; set; } = true;

    // ── 启动自动全量重建（可选，接管原「ICS Boot Fix」计划任务的职责）──
    /// <summary>程序启动后自动执行一次全量重建（把 ICS 拉回已知良好状态）</summary>
    public bool BootRebuildEnabled  { get; set; } = true;
    /// <summary>启动后等多少秒再执行（留出网卡识别/校园网认证的时间）</summary>
    public int  BootRebuildDelaySec { get; set; } = 30;

    // ── 界面 ────────────────────────────────────────────────────
    // ── 游戏模式（校园网抖动时，只用手机 USB 共享出网）──────────
    /// <summary>USB 共享（手机网络共享）网卡名；空 = 未选择</summary>
    public string GameUsbAdapter { get; set; } = "";
    /// <summary>进入游戏模式时要禁用的网卡列表</summary>
    public List<string> GamePauseAdapters { get; set; } = new();
    /// <summary>实际已禁用的网卡 —— 退出时按这份记录恢复（跨重启也安全）</summary>
    public List<string> GameDisabledAdapters { get; set; } = new();
    /// <summary>游戏模式是否激活</summary>
    public bool GameModeActive { get; set; } = false;
    /// <summary>游戏模式下 USB 网卡的度量（越小越优先）</summary>
    public int GameUsbMetric { get; set; } = 5;
    /// <summary>退出游戏模式后把 USB 网卡的度量恢复成这个值（大到不抢默认路由）</summary>
    public int GameUsbMetricNormal { get; set; } = 9999;

    /// <summary>
    /// 开机后自动退出游戏模式（默认开）。
    /// 原因：Windows 会把"设备已禁用"跨重启保留，关机时在游戏模式 →
    /// 下次开机会发现卡1/卡2 仍是禁用状态，容易误解成"被偷偷禁用了"。
    /// 打开本项后，真开机时会自动恢复网卡并重建 ICS。
    /// </summary>
    public bool GameExitOnBoot { get; set; } = true;

    // ── 游戏模式：自动切换 ──────────────────────────────────────
    /// <summary>
    /// 自动模式：检测到手机 USB 共享网卡可用就自动进入游戏模式，
    /// 它消失（拔线/共享关闭/无有效 IP）就自动退出、回归正常链路。
    /// false = 纯手动（只能点按钮 / CLI）。
    /// </summary>
    public bool GameModeAuto { get; set; } = false;
    /// <summary>进入游戏模式前，USB 需持续可用的秒数（防抖，避免插上瞬间就切）</summary>
    public int AutoEnterDebounceSec { get; set; } = 15;
    /// <summary>退出游戏模式前，USB 需持续不可用的秒数（防抖，避免插拔抖动来回切）</summary>
    public int AutoExitDebounceSec { get; set; } = 15;
    /// <summary>一次自动切换后的冷却秒数（期间不再自动"进入"，退出永远允许）</summary>
    public int AutoModeCooldownSec { get; set; } = 60;

    // ── 游戏模式：轻量化 + 无感切换 ────────────────────────────
    /// <summary>
    /// 轻量化：游戏模式期间只跑"出口相关"的 5 项体检、定时器降频、停设备探测、降优先级。
    /// false = 完全等同于重功能行为。
    /// </summary>
    public bool GameModeLite { get; set; } = true;
    /// <summary>退出时走快路径（只重启 ICS + 关键项复检），失败自动回退全量重建</summary>
    public bool GameFastSwitch { get; set; } = true;
    /// <summary>游戏模式下的体检周期（秒）</summary>
    public int GameProbeIntervalSec { get; set; } = 5;
    /// <summary>游戏模式下的流量采样周期（秒）</summary>
    public int GameTrafficIntervalSec { get; set; } = 3;
    /// <summary>游戏模式下的界面刷新周期（毫秒）</summary>
    public int GameUiTickMs { get; set; } = 2000;
    /// <summary>游戏模式下的进程优先级（Normal / BelowNormal / Idle）</summary>
    public string GameProcessPriority { get; set; } = "BelowNormal";
    /// <summary>
    /// 【内部】进入游戏模式前各网卡的路由度量，退出时按此精确还原。
    /// 键 = 网卡名，值 = 原度量。
    /// </summary>
    public Dictionary<string, int> GameModifiedMetrics { get; set; } = new();

    public bool TrayIcon      { get; set; } = true;   // false = 纯静默守护（连托盘都不显示）
    public bool StartMinimized{ get; set; } = true;   // 启动后只留托盘，不弹主窗口
    public bool CloseToTray   { get; set; } = true;   // 关闭窗口 = 最小化到托盘（QQ/微信行为）
    public bool LeftClickTrayOpens { get; set; } = true; // 左键单击唤出主窗口

    /// <summary>启动时显示动画卡片（约 1 秒）。看门狗拉起的实例不会显示。</summary>
    public bool ShowSplash { get; set; } = true;

    /// <summary>桌面上显示常驻悬浮球（颜色=健康状态，单击开主界面，可拖动）</summary>
    public bool FloatingBall { get; set; } = true;

    /// <summary>悬浮球上次停的位置；-1 = 还没拖过，用默认位置（主屏右侧偏下）</summary>
    public int BallX { get; set; } = -1;
    public int BallY { get; set; } = -1;

    // ── 日志 ────────────────────────────────────────────────────
    public string LogDir      { get; set; } = "logs";
    public int    LogKeepDays { get; set; } = 30;

    // ── 外部脚本（内嵌资源的对照对象）────────────────────────────
    public string LegacyScriptDir { get; set; } = "scripts";

    /// <summary>
    /// 主程序计划任务名（登录自启）。**只是线索**：程序会按"任务动作是否指向本 exe"
    /// 去核实/自动纠正，改过名也不会失灵（见 TaskWatcher.Resolve）。
    /// </summary>
    public string BootFixTaskName { get; set; } = "IcsWatch";

    /// <summary>看门狗计划任务名（--watchdog 每 2 分钟）。同上，也按 exe 路径核实。</summary>
    public string WatchTaskName   { get; set; } = "IcsWatch Watchdog";

    // ────────────────────────────────────────────────────────────
    [JsonIgnore] public string BaseDir { get; private set; } = AppContext.BaseDirectory;

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文不转义
    };

    public string ConfigPath => Path.Combine(BaseDir, "config.json");
    public string LogPath    => Path.IsPathRooted(LogDir) ? LogDir : Path.Combine(BaseDir, LogDir);
    public string ScriptPath => Path.IsPathRooted(LegacyScriptDir) ? LegacyScriptDir : Path.Combine(BaseDir, LegacyScriptDir);
    public string DataPath   => Path.Combine(BaseDir, "data");

    public static AppConfig Load()
    {
        var cfg = new AppConfig();
        try
        {
            if (File.Exists(cfg.ConfigPath))
            {
                var json = File.ReadAllText(cfg.ConfigPath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (loaded != null)
                {
                    loaded.BaseDir = cfg.BaseDir;
                    return loaded;
                }
            }
            else
            {
                cfg.Save();
            }
        }
        catch { /* 配置损坏则回落到默认值，不阻塞启动 */ }
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts),
                new System.Text.UTF8Encoding(true));
        }
        catch { }
    }
}
