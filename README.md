# IcsWatch

> Windows 上给「网络共享（ICS）」当保姆的小工具：定时体检、分级维修、开机自启、看门狗兜底，顺带把流量和设备都记下来。
>
> A small Windows tray app that keeps **Internet Connection Sharing (ICS)** alive — **but it only makes sense if you have the hardware**: a second network adapter plus a WiFi router.

---

## 使用前提：这是一个需要硬件配合的方案

**它不是纯软件方案。** 它守护的是「电脑把网络共享给路由器」这条链路，所以下面三样缺一不可：

| 需要 | 说明 |
|---|---|
| **两块网卡** | 一块接上游（校园网 / 宽带），一块接下游路由器。第二块可以是 **USB 网卡**，不必是板载的 |
| **一台 WiFi 路由器** | 手机、平板、别的电脑通过它上网。建议设成 **AP / 桥接模式** |
| **网线** | 电脑的「私有端」网卡 ←→ 路由器的 **LAN 口**（不是 WAN 口） |

拓扑大致是这样：

```text
        校园网 / 宽带
              │
     ┌────────┴────────┐
     │  网卡二：公网端   │   ← Windows 自己从这里上网
     ├─────────────────┤
     │    你的电脑      │   ← IcsWatch 在这里跑
     ├─────────────────┤
     │  网卡一：私有端   │   192.168.137.1
     └────────┬────────┘
              │  网线插 LAN 口
     ┌────────┴────────┐
     │  WiFi 路由器      │   AP / 桥接模式（关掉它自己的 DHCP）
     └────────┬────────┘
              │  WiFi
     手机 / 平板 / 另一台电脑
```

**缺任何一样，这个程序都没有用武之地**：它不会凭空变出一张网卡，也不会把一台没有路由器的电脑变成热点 —— 那种场景用 Windows 自带的「移动热点」就够了，不需要这个程序。

路由器为什么要设 AP / 桥接模式：如果它自己也在发地址，就会和 Windows 共享的 DHCP 抢客户端，表现就是设备「时好时坏、老是在重新获取 IP」。

## 它解决什么问题

校园网这类公共网络常见三个限制：能同时连的设备台数有限、无线信号又差、还不给你装宽带。

把电脑当中转站、由一台专门的路由器对外发 WiFi，可以让多台设备共用一条已经授权的线路；无线那一段交给路由器，也比电脑自带的「移动热点」稳定得多、快得多。

代价是这条链路很脆：Windows 自带的「网络连接共享」经常悄悄失效 ——

- 每次重启、换网、睡眠唤醒之后，共享可能已经坏了（`ipnat` 不再转发、DHCP 分配器不响应），但界面上「共享」那个勾还打着勾。
- 一旦设备连不上，你只能手工去「网络连接」里取消共享再重新共享，或者干脆重启电脑。
- Windows 没有任何「守护/自愈」机制，也没有日志告诉你什么时候坏的。

IcsWatch 就是补这一块的：把「体检 → 定位 → 修 → 复检」这件事做成一个常驻的小程序。

## 它做什么

| 能力 | 说明 |
|---|---|
| **体检** | 20 余项检查：两块网卡的链路/地址/路由/度量、ICS 共享两端、`SharedAccess`/`NlaSvc`/`ipnat`、DHCP 监听、NAT 出口实测、网卡类别、计划任务、下游设备、僵尸租约 |
| **分级维修** | `off` 只告警 / `light` 只动轻量项 / `full` 全量重建。每一步都复检，修不好会明确说「未修好: xxx」而不是假装成功 |
| **自动维修** | 连续失败才动手（默认），有冷却与每小时上限；但「ICS 引擎死了 / NAT 不通」这类硬故障不受上限约束 |
| **开机自启 + 看门狗** | 注册两个计划任务：主程序登录自启、`--watchdog` 每 2 分钟确认主程序还活着，没了就拉起来 |
| **游戏模式** | 校园网不稳定时把出口临时切到手机 USB 共享（只改路由度量，不动共享配置），退出时切回；可按出口质量自动进出（默认关，需在设置里打开） |
| **流量统计** | 今日/累计总量（出口网卡计数器，精确）、每台设备的用量（抓包统计，是下限） |
| **访问记录** | 每台设备访问了哪个域名 / 哪个 IP:端口，持续多久、上下行多少字节（**看不到** HTTPS 里的具体网页） |
| **界面** | 启动动画、桌面悬浮球（颜色即状态）、托盘图标、六个功能页 |

## 软件环境

- Windows 10 / 11，**x64**
- 需要**管理员权限**运行（要改网卡地址、服务、ICS 共享、计划任务）
- 运行时不需要装 .NET（Release 是自包含单文件，约 63 MB）
- **可选**：[Npcap](https://npcap.com) —— 只有「按设备流量 / 访问记录」这两个功能需要它。没装也能用，只是这两块不统计

## 安装

1. 先把硬件接好：上游网线 → 公网端网卡；私有端网卡 → 路由器的 LAN 口。
2. 从 [Releases](../../releases) 下载 `IcsWatch.exe`，放到一个**固定的目录**（例如 `%LOCALAPPDATA%\IcsWatch\`，程序会把这里当工作目录）。
3. 双击运行，允许管理员提权。
4. 进「设置」页：选好**私有端**（接路由器的网卡）和**公网端**（能上网的网卡）→ 点「应用并全量重建」。
5. 想开机自动运行：点「立即全量重建一次」会在首次运行后注册计划任务；也可以在「自动化」页确认两个任务都在。

> 第一次运行会在 exe 同目录生成 `config.json`、`logs\`、`data\`。这些都是你本机的实际情况，不要提交到任何仓库。

## 命令行

带参数运行会**只做那件事然后退出**，适合脚本和排查：

```powershell
IcsWatch.exe --check          # 跑一遍体检，打印 PASS/WARN/FAIL 后退出
IcsWatch.exe --repair-light   # 只修轻量项（地址/度量/类别/共享）
IcsWatch.exe --repair         # 全量重建
IcsWatch.exe --game-on        # 进入游戏模式
IcsWatch.exe --game-off       # 退出游戏模式
IcsWatch.exe --min            # 静默启动（只留托盘）
IcsWatch.exe --watchdog       # 看门狗模式（被计划任务调用）
```

## 配置

`config.json` 就在 exe 旁边，键名见 `src/IcsWatch/Config/AppConfig.cs`。常用的几个：

| 键 | 默认 | 含义 |
|---|---|---|
| `privateAdapter` / `publicAdapter` | 以太网 / 以太网 2 | 私有端、公网端的网卡名（必须与「网络连接」里的名字完全一致） |
| `privateIp` | 192.168.137.1 | 私有端固定地址（ICS 默认网关） |
| `repairMode` | light | `off` / `light` / `full` |
| `pollSec` | 5 | 体检周期（秒） |
| `maxRepairPerHour` | 5 | 每小时自动维修上限（硬故障不受限） |
| `accessLogEnabled` | true | 是否记录访问（需要 Npcap） |
| `floatingBall` | true | 是否显示桌面悬浮球 |

## 已知限制

- **USB 网卡不会被禁用/启用**。USB 网卡一旦 disable 很可能直接掉出 USB 总线（`ERROR_NO_MORE_ITEMS`），只能拔插线才能救回来，所以这类操作程序一律不做。
- **抓包统计到的设备流量是下限**。ICE/NAT 转发路径在高负载下会丢包，抓不全；需要精确总量请看「今日总量」（来自网卡计数器）。
- **访问记录只到域名/对端/端口**。HTTPS 之后的内容、QUIC/HTTP-3、DoH/DoT、ECH 都看不到；抓包开始前已经建立的长连接也没有域名。
- **设备每次重连可能拿到新 IP**（ICS 的 DHCP 分配器行为），旧地址会以「永久」条目残留在 ARP 表里 —— 体检能识别，一键维修会清理。
- **会修改网络设置**。如果你不确定私有端/公网端选哪块网卡，先用 `--check` 看体检结果，别直接跑 `--repair`。
- 只支持 Windows x64；不支持 ARM、不支持多用户会话。

## 卸载

1. 托盘右键 → 退出（会写 `user-exit.flag`，阻止看门狗把你拉回来）。
2. 删除两个计划任务：`IcsWatch`、`IcsWatch Watchdog`（任务计划程序里删）。
3. 删除程序目录（含 `config.json` / `logs` / `data`）。
4. 需要的话，把两块网卡的 IP 与 DNS 改回「自动获得」。

## 从源码构建

```powershell
dotnet build src\IcsWatch\IcsWatch.csproj -c Release     # 编译
dotnet run  --project tests\IcsWatch.SelfCheck -c Release # 契约自检（36 条）
.\publish.ps1                                            # 出单文件到 .\publish\
.\publish.ps1 -Deploy -InstallDir C:\Tools\IcsWatch      # 出包并部署
```

自检跑的是「这台机器上的真实体检」：如果本机读不到 ICS 的 COM 状态（没有 ICS 组件、权限不足、CI 容器里），`ics.private` / `ics.public` 两项会被跳过并打印一行提示 —— 不静默放过，也不把环境问题算成代码错误。

## 许可

[MIT](LICENSE)。

第三方：可选依赖 [Npcap](https://npcap.com)，它有自己的许可（**不随本程序分发**，请自行安装）；其余依赖只有 `System.Management` 与 `System.ServiceProcess.ServiceController`。

## 免责

这个程序会修改网卡地址、路由度量、网络类别、ICS 共享和计划任务，并且需要管理员权限。请先在一台可折腾的机器上试，确认行为符合预期再用到主力环境。

---

### English TL;DR

IcsWatch babysits Windows **Internet Connection Sharing**: 20+ health checks, tiered repair (`light`/`full`), a logon task plus a watchdog, and traffic/access logging.

**It is not a pure-software solution**: you need a second network adapter (USB is fine) and a WiFi router connected through it. Without that hardware the app has nothing to do — for a single-machine hotspot, Windows' built-in "Mobile hotspot" is enough.

Build: `dotnet build src\IcsWatch\IcsWatch.csproj -c Release` · Test: `dotnet run --project tests\IcsWatch.SelfCheck -c Release` · Package: `.\publish.ps1`.

Requires Windows 10/11 x64 and **administrator rights**. [Npcap](https://npcap.com) is optional and only needed for per-device traffic and access logs. Licensed under MIT.
