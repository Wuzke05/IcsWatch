# IcsWatch

> Windows 上给「网络共享（ICS）」当保姆的小工具：定时体检、分级维修、开机自启、看门狗兜底，顺带把流量和设备都记下来。
>
> A small Windows tray app that keeps **Internet Connection Sharing (ICS)** alive: periodic health checks, tiered auto-repair, watchdog, plus traffic and per-device statistics.

---

## 这个程序解决什么问题

Windows 自带的「网络连接共享」很脆：

- 每次重启、换网、睡眠唤醒之后，共享经常悄悄失效（`ipnat` 不再转发、DHCP 分配器不响应），但界面上「共享」那个勾还打着勾。
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
| **游戏模式** | 临时把出口切到手机 USB 共享（只改路由度量，不动共享配置），退出时切回 |
| **流量统计** | 今日/累计总量（出口网卡计数器，精确）、每台设备的用量（抓包统计，是下限） |
| **访问记录** | 每台设备访问了哪个域名 / 哪个 IP:端口，持续多久、上下行多少字节（**看不到** HTTPS 里的具体网页） |
| **界面** | 启动动画、桌面悬浮球（颜色即状态）、托盘图标、六个功能页 |

## 环境要求

- Windows 10 / 11，**x64**
- 需要**管理员权限**运行（要改网卡地址、服务、ICS 共享、计划任务）
- 运行时不需要装 .NET（Release 是自包含单文件，约 63 MB）
- **可选**：[Npcap](https://npcap.com) —— 只有「按设备流量 / 访问记录」这两个功能需要它。没装也能用，只是这两块不统计

## 安装

1. 从 [Releases](../../releases) 下载 `IcsWatch.exe`。
2. 放到一个**固定的目录**，例如 `%LOCALAPPDATA%\IcsWatch\`（程序会把这个目录当作工作目录）。
3. 双击运行，允许管理员提权。
4. 进「设置」页：选好**私有端**（接路由器/下游设备的网卡）和**公网端**（能上网的网卡）→ 点「应用并全量重建」。
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

## 已知限制（请先看完再判断适不适合你）

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

IcsWatch is a Windows tray app that babysits **Internet Connection Sharing**: it runs 20+ health checks, repairs in tiers (`light`/`full`), registers a logon task plus a watchdog, and records traffic/access logs.

Build: `dotnet build src\IcsWatch\IcsWatch.csproj -c Release` · Test: `dotnet run --project tests\IcsWatch.SelfCheck -c Release` · Package: `.\publish.ps1`.

Requires Windows 10/11 x64 and **administrator rights**. [Npcap](https://npcap.com) is optional and only needed for per-device traffic and access logs. Licensed under MIT.
