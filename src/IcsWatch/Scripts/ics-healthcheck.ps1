# ics-healthcheck.ps1 —— ICS 双网卡「只读体检」
#
# 用途：一条命令跑出全部关键项的 PASS / WARN / FAIL，用于故障对照定位。
# 特性：**只读，绝不修改任何系统状态**，可随时安全运行。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File ics-healthcheck.ps1
#       （建议管理员权限，否则部分 ICS/服务项读不到）

param(
    [string]$ifPrivate = "以太网",      # 卡1：ICS 私有端（接下游路由器）
    [string]$ifPublic  = "以太网 2",    # 卡2：ICS 公网端（校园网）
    [string]$privateIP = "192.168.137.1",
    [string]$altIP     = ""            # 卡1 的从属 IP（可选，留空表示不配）
)

$ErrorActionPreference = "Continue"
$script:rows = @()

function Add-R { param([string]$S, [string]$Item, [string]$Detail)
    $script:rows += [pscustomobject]@{ 状态 = $S; 检查项 = $Item; 详情 = $Detail }
}

Write-Host ""
Write-Host "========== ICS 双网卡体检 ==========" -ForegroundColor Cyan
Write-Host "私有端(卡1) = $ifPrivate    公网端(卡2) = $ifPublic"
Write-Host ""

# ---------- 1. 网卡链路 ----------
$ad1 = Get-NetAdapter -Name $ifPrivate -ErrorAction SilentlyContinue
$ad2 = Get-NetAdapter -Name $ifPublic  -ErrorAction SilentlyContinue

if (-not $ad1) { Add-R "FAIL" "卡1[$ifPrivate] 存在" "找不到该网卡，检查网卡名" }
elseif ($ad1.Status -ne "Up") { Add-R "WARN" "卡1 链路" "Status=$($ad1.Status)（路由器没插？下结论前先插好网线）" }
else { Add-R "PASS" "卡1 链路" "Up" }

if (-not $ad2) { Add-R "FAIL" "卡2[$ifPublic] 存在" "找不到该网卡，检查网卡名" }
elseif ($ad2.Status -ne "Up") { Add-R "FAIL" "卡2 链路" "Status=$($ad2.Status)（校园网没插？）" }
else { Add-R "PASS" "卡2 链路" "Up" }

# ---------- 2. 地址 ----------
$ip1 = Get-NetIPAddress -InterfaceAlias $ifPrivate -AddressFamily IPv4 -ErrorAction SilentlyContinue
$p1  = @($ip1 | Select-Object -ExpandProperty IPAddress)
if ($p1 -contains $privateIP) {
    Add-R "PASS" "卡1 主地址" $privateIP
} else {
    Add-R "FAIL" "卡1 主地址" "期望 $privateIP，实际: $($p1 -join ',')"
}
if ($p1 | Where-Object { $_ -like "169.254.*" }) {
    Add-R "WARN" "卡1 APIPA 残留" "存在 169.254.x.x → DHCP 客户端还在跑，见「DHCP 关不掉」"
}
if ($p1 -contains $altIP) {
    Add-R "PASS" "卡1 从属 IP" "$altIP（可用于访问下游路由器管理页）"
} else {
    Add-R "WARN" "卡1 从属 IP" "缺 $altIP → 电脑进不了路由器管理页；可选，见 SKILL.md「进下游路由器管理页」"
}

$ip2 = @(Get-NetIPAddress -InterfaceAlias $ifPublic -AddressFamily IPv4 -ErrorAction SilentlyContinue |
         Select-Object -ExpandProperty IPAddress)
if ($ip2.Count -gt 0 -and -not ($ip2 | Where-Object { $_ -like "169.254.*" })) {
    Add-R "PASS" "卡2 地址" ($ip2 -join ',')
} else {
    Add-R "FAIL" "卡2 地址" "未拿到有效地址（校园网未认证 / 未插线）"
}

# ---------- 3. 默认路由（双默认网关抢占是本环境第一号杀手）----------
$defs = @(Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue)
$def1 = @($defs | Where-Object { $_.InterfaceAlias -eq $ifPrivate })
$def2 = @($defs | Where-Object { $_.InterfaceAlias -eq $ifPublic })

if ($def1.Count -gt 0) {
    Add-R "FAIL" "卡1 不得有默认路由" "出现了 -> $($def1[0].NextHop)（会抢走出口，见「双默认网关抢占」）"
} else {
    Add-R "PASS" "卡1 无默认路由" "正确（私有端只负责转发）"
}
if ($def2.Count -eq 0) {
    Add-R "FAIL" "卡2 应有默认路由" "没有默认路由 → 校园网未生效"
} else {
    Add-R "PASS" "卡2 默认路由" "-> $($def2[0].NextHop)  metric=$($def2[0].InterfaceMetric)"
}

# ---------- 4. DHCP 与度量 ----------
$i1 = Get-NetIPInterface -InterfaceAlias $ifPrivate -AddressFamily IPv4 -ErrorAction SilentlyContinue
$i2 = Get-NetIPInterface -InterfaceAlias $ifPublic  -AddressFamily IPv4 -ErrorAction SilentlyContinue
if ($i1.Dhcp -eq "Disabled") { Add-R "PASS" "卡1 DHCP" "Disabled" }
else { Add-R "FAIL" "卡1 DHCP" "=$($i1.Dhcp)（应 Disabled；Set-NetIPInterface 可能无效，需改注册表）" }

if ($i1.InterfaceMetric -eq 9999) { Add-R "PASS" "卡1 度量" "9999" }
else { Add-R "WARN" "卡1 度量" "=$($i1.InterfaceMetric)（期望 9999 作兜底）" }
if ($i2.InterfaceMetric -eq 10) { Add-R "PASS" "卡2 度量" "10" }
else { Add-R "WARN" "卡2 度量" "=$($i2.InterfaceMetric)（期望 10 保证优先）" }

$guid = (Get-NetAdapter -Name $ifPrivate -ErrorAction SilentlyContinue).InterfaceGuid
if ($guid) {
    $eh = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$guid" -ErrorAction SilentlyContinue).EnableDHCP
    if ($eh -eq 0) { Add-R "PASS" "注册表 EnableDHCP" "0" }
    else { Add-R "WARN" "注册表 EnableDHCP" "=$eh（期望 0）" }
}

# ---------- 5. 网络类别 ----------
$prof = Get-NetConnectionProfile -InterfaceAlias $ifPrivate -ErrorAction SilentlyContinue
if ($prof -and $prof.NetworkCategory -eq "Private") {
    Add-R "PASS" "卡1 网络类别" "Private（显示名'未识别的网络'属正常）"
} else {
    Add-R "WARN" "卡1 网络类别" "=$($prof.NetworkCategory)（影响局域网发现/共享；服务访问未必受影响）"
}

# ---------- 6. ICS 服务与驱动 ----------
$sa = Get-Service SharedAccess -ErrorAction SilentlyContinue
if ($sa.Status -eq "Running") { Add-R "PASS" "SharedAccess 服务" "Running" }
elseif ($sa.Status -eq "StopPending") { Add-R "FAIL" "SharedAccess 服务" "卡在 StopPending → 杀仅承载它的 svchost" }
else { Add-R "FAIL" "SharedAccess 服务" "=$($sa.Status)" }

$nla = Get-Service NlaSvc -ErrorAction SilentlyContinue
if ($nla.Status -eq "Running") { Add-R "PASS" "NlaSvc 服务" "Running" }
else { Add-R "WARN" "NlaSvc 服务" "=$($nla.Status)（停掉会导致网络类别判定异常）" }

$ipnat = (sc.exe query ipnat 2>$null | Out-String)
if ($ipnat -match "RUNNING") { Add-R "PASS" "ipnat 驱动" "RUNNING" }
else { Add-R "FAIL" "ipnat 驱动" "未运行 → NAT 不转发" }

# ---------- 7. ICS 共享两端 ----------
try {
    $sh = New-Object -ComObject HNetCfg.HNetShare
    $c1 = $null; $c2 = $null
    foreach ($conn in $sh.EnumEveryConnection) {
        $p = $sh.NetConnectionProps.Invoke($conn)
        if ($p.Name -eq $ifPrivate) { $c1 = $sh.INetSharingConfigurationForINetConnection($conn) }
        if ($p.Name -eq $ifPublic)  { $c2 = $sh.INetSharingConfigurationForINetConnection($conn) }
    }
    if ($c1 -and $c1.SharingEnabled -and $c1.SharingConnectionType -eq 1) { Add-R "PASS" "ICS 私有端(卡1)" "En=True Type=1" }
    else { Add-R "FAIL" "ICS 私有端(卡1)" "En=$($c1.SharingEnabled) Type=$($c1.SharingConnectionType)（须 En=True Type=1）" }

    if ($c2 -and $c2.SharingEnabled -and $c2.SharingConnectionType -eq 0) { Add-R "PASS" "ICS 公网端(卡2)" "En=True Type=0" }
    else { Add-R "FAIL" "ICS 公网端(卡2)" "En=$($c2.SharingEnabled) Type=$($c2.SharingConnectionType)（须 En=True Type=0）" }
} catch {
    Add-R "FAIL" "ICS 共享状态" "COM 读取失败: $($_.Exception.Message)"
}

# ---------- 8. ICS DHCP 监听 ----------
$dhcpListen = (netstat -an 2>$null | Out-String)
if ($dhcpListen -match [regex]::Escape("$privateIP" + ":67")) {
    Add-R "PASS" "ICS DHCP 监听" "$privateIP`:67"
} else {
    Add-R "FAIL" "ICS DHCP 监听" "未见 $privateIP`:67 → 下游设备拿不到 IP"
}

# ---------- 9. NAT 出口实测 ----------
if ($def2.Count -gt 0) {
    $gw = $def2[0].NextHop
    $out = ping -n 1 -w 1500 -S $privateIP $gw 2>$null | Out-String
    if ($out -match "TTL=") { Add-R "PASS" "NAT 出口实测" "源 $privateIP -> $gw 有回复（TTL 有跳数即转发正常）" }
    else { Add-R "FAIL" "NAT 出口实测" "源 $privateIP -> $gw 无回复 → NAT/转发异常" }
}

# ---------- 10. 下游设备（ICS 客户端）----------
# 只统计 ICS 私有网段内的邻居，避免把从属 IP 网段（如 192.168.3.x）的路由器也算进来
$icsPrefix = ($privateIP -split '\.')[0..2] -join '.'
$clients = @(Get-NetNeighbor -InterfaceAlias $ifPrivate -AddressFamily IPv4 -ErrorAction SilentlyContinue |
             Where-Object { $_.IPAddress -like "$icsPrefix.*" -and $_.LinkLayerAddress -notmatch "FF-FF-FF|00-00-00" } |
             Sort-Object { [int](($_.IPAddress -split '\.')[3]) })
if ($clients.Count -gt 0) {
    $liveList = @()
    foreach ($c in $clients) {
        $ok = Test-Connection -ComputerName $c.IPAddress -Count 1 -Quiet -ErrorAction SilentlyContinue
        $md = if ($c.LinkLayerAddress -match "^(8[0-9A-F]|9[0-9A-F]|[AB][0-9A-F])-") { " 手机随机MAC?" } else { "" }
        if ($ok) { $liveList += "$($c.IPAddress)$md" }
    }
    Add-R "PASS" "ICS 客户端" "共 $($clients.Count) 个租约；当前在线: $($liveList -join ', ')"
} else {
    Add-R "WARN" "ICS 客户端" "暂无租约（下游设备未连接）"
}

# ---------- 11. 计划任务 ----------
foreach ($tn in @("ICS Boot Fix", "ICS Net Fix Watch")) {
    $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
    if ($t) {
        $info = Get-ScheduledTaskInfo -TaskName $tn -ErrorAction SilentlyContinue
        if ($info.LastTaskResult -eq 0) {
            Add-R "PASS" "计划任务 $tn" "State=$($t.State) LastResult=0"
        } else {
            Add-R "WARN" "计划任务 $tn" "State=$($t.State) **LastTaskResult=$($info.LastTaskResult) 非 0 = 上次执行失败**，查对应日志"
        }
    } else {
        Add-R "WARN" "计划任务 $tn" "不存在（自动化缺失，见 SKILL.md 第 6 节）"
    }
}

# ============ 输出 ============
$colors = @{ "PASS" = "Green"; "WARN" = "Yellow"; "FAIL" = "Red" }
foreach ($r in $rows) {
    $c = $colors[$r.状态]
    Write-Host ("[{0}] {1,-24} {2}" -f $r.状态, $r.检查项, $r.详情) -ForegroundColor $c
}

$nPass = @($rows | Where-Object 状态 -eq "PASS").Count
$nWarn = @($rows | Where-Object 状态 -eq "WARN").Count
$nFail = @($rows | Where-Object 状态 -eq "FAIL").Count

Write-Host ""
Write-Host "========== 汇总: PASS=$nPass  WARN=$nWarn  FAIL=$nFail ==========" -ForegroundColor Cyan
if ($nFail -gt 0) {
    Write-Host "存在 FAIL 项 —— 按上表逐项处理后复跑本脚本。" -ForegroundColor Red
    Write-Host "对照检修：SKILL.md 第 0 节症状速查表 / references/troubleshooting-cases.md" -ForegroundColor Red
} elseif ($nWarn -gt 0) {
    Write-Host "无致命故障，有 $nWarn 项需要关注。" -ForegroundColor Yellow
} else {
    Write-Host "全部正常。" -ForegroundColor Green
}
Write-Host ""
