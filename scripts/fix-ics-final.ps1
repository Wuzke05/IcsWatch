# fix-ics-final.ps1
# 最终的ICS修复脚本

$logFile = "C:\Scripts\fix-ics-final.log"

function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    "$timestamp - $Message" | Out-File -FilePath $logFile -Append -Encoding UTF8
}

Write-Log "===== 开始执行最终ICS修复 ====="

# 等待USB网卡完全初始化（2分钟）
Write-Log "等待120秒，让USB网卡完全初始化..."
Start-Sleep -Seconds 120

# 检查网卡状态
Write-Log "检查网卡状态..."
$ethernet = Get-NetAdapter -Name "以太网" -ErrorAction SilentlyContinue
$ethernet2 = Get-NetAdapter -Name "以太网 2" -ErrorAction SilentlyContinue

if (-not $ethernet -or -not $ethernet2) {
    Write-Log "错误：找不到网卡"
    exit 1
}

Write-Log "以太网（PCIe）状态: $($ethernet.Status)"
Write-Log "以太网 2（USB）状态: $($ethernet2.Status)"

# 确保 NlaSvc（网络位置感知）在运行 —— 它停掉会导致网络类别无法判定、ICS 行为异常
if ((Get-Service NlaSvc).Status -ne "Running") {
    Write-Log "NlaSvc 未运行，启动中..."
    Start-Service NlaSvc -ErrorAction SilentlyContinue
    Write-Log "NlaSvc 状态: $((Get-Service NlaSvc).Status)"
}

# 停止ICS服务
Write-Log "停止SharedAccess服务..."
Stop-Service SharedAccess -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

# 若 SharedAccess 卡死在 StopPending（半死状态会持续抢改网络配置），
# 杀掉仅承载它的 svchost 进程即可让 SCM 归位
for ($i = 1; $i -le 5; $i++) {
    if ((Get-Service SharedAccess).Status -ne "StopPending") { break }
    Write-Log "SharedAccess 卡在 StopPending（第 $i 次），尝试清理宿主进程..."
    # 用 Win32_Service 精确取 PID 与「同进程托管的全部服务」（不要解析 tasklist 文本）：
    # tasklist 输出里 PID 与服务名之间**只有 1 个空格**，而服务名之间是 ", "。
    # 若按 -split '\s{2,}' 切分，会得到 ["svchost.exe", "31668 SharedAccess"] 两个元素，
    # 把 PID 和服务名粘在一起 → 多服务 svchost 也会被误判成「只有 1 个」而遭误杀。
    $saSvc = Get-CimInstance -ClassName Win32_Service -Filter "Name='SharedAccess'" -ErrorAction SilentlyContinue
    $zombiePid = $saSvc.ProcessId
    if ($zombiePid -and $zombiePid -gt 0) {
        $sameHost = @(Get-CimInstance -ClassName Win32_Service -Filter "ProcessId=$zombiePid" -ErrorAction SilentlyContinue)
        if ($sameHost.Count -le 1) {
            Write-Log "杀死僵尸 svchost (PID $zombiePid，同进程仅托管 SharedAccess)"
            Stop-Process -Id $zombiePid -Force -ErrorAction SilentlyContinue
        } else {
            Write-Log "跳过：该 svchost (PID $zombiePid) 还托管 $($sameHost.Count - 1) 个其他服务，不宜杀"
            break
        }
    } else {
        Write-Log "无法确定 SharedAccess 的宿主进程，继续等待 SCM……"
    }
    Start-Sleep -Seconds 3
}
Write-Log "SharedAccess 状态: $((Get-Service SharedAccess).Status)"

# 确保以太网（PCIe）的静态IP正确 —— 必须「静态 + 不设网关」
# 【关键】卡1 一旦带上默认网关，插上路由器后就会抢走默认路由，导致校园网被完全闲置。
Write-Log "配置以太网（PCIe）静态 IP 192.168.137.1/24（不设网关）..."

# 清掉 DHCP 残留地址（169.254.x.x 等），只保留 192.168.137.1
Get-NetIPAddress -InterfaceAlias "以太网" -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -ne "192.168.137.1" } |
    ForEach-Object { Remove-NetIPAddress -InterfaceAlias "以太网" -IPAddress $_.IPAddress -Confirm:$false -ErrorAction SilentlyContinue }

# 用 netsh 写静态配置（同时会把注册表 EnableDHCP 置 0）
netsh interface ipv4 set address name="以太网" static 192.168.137.1 255.255.255.0 | Out-Null

# 双保险：直接写注册表关闭 DHCP 并清掉路由器留下的陈旧租约
# （Set-NetIPInterface -Dhcp Disabled 在本机无效，会被弹回 Enabled）
$ethGuid = (Get-NetAdapter -Name "以太网").InterfaceGuid
$ethReg  = "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$ethGuid"
Set-ItemProperty -Path $ethReg -Name "EnableDHCP" -Value 0 -Type DWord -ErrorAction SilentlyContinue
foreach ($key in @("DhcpIPAddress","DhcpDefaultGateway","DhcpSubnetMask","DhcpNameServer","DhcpServer")) {
    Remove-ItemProperty -Path $ethReg -Name $key -ErrorAction SilentlyContinue
}

# 清 DNS（ICS 会自行向客户端下发）
Set-DnsClientServerAddress -InterfaceAlias "以太网" -ResetServerAddresses -ErrorAction SilentlyContinue
Write-Log "以太网（PCIe）静态 IP 配置完成"

# 确保注册表ScopeAddress正确
Write-Log "设置注册表ScopeAddress..."
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters" -Name ScopeAddress -Value "192.168.137.1"

# 启动ICS服务
Write-Log "启动SharedAccess服务..."
Start-Service SharedAccess -ErrorAction Stop
Start-Sleep -Seconds 5

# 使用COM接口启用ICS共享
Write-Log "使用COM接口启用ICS共享..."
try {
    $share = New-Object -ComObject HNetCfg.HNetShare
    $connections = $share.EnumEveryConnection
    
    $ethernetConn = $null
    $ethernet2Conn = $null
    
    foreach ($conn in $connections) {
        $props = $share.NetConnectionProps($conn)
        if ($props.Name -eq "以太网") {
            $ethernetConn = $conn
        }
        if ($props.Name -eq "以太网 2") {
            $ethernet2Conn = $conn
        }
    }
    
    if ($ethernetConn -and $ethernet2Conn) {
        # 先禁用共享
        $share.INetSharingConfigurationForINetConnection($ethernetConn).DisableSharing()
        $share.INetSharingConfigurationForINetConnection($ethernet2Conn).DisableSharing()
        Start-Sleep -Seconds 2
        
        # 启用共享：以太网 2（USB，校园网）作为公共连接
        $publicConfig = $share.INetSharingConfigurationForINetConnection($ethernet2Conn)
        $publicConfig.EnableSharing(0)  # 0 = public
        Write-Log "已启用以太网 2（USB）为公共连接"
        
        # 启用共享：以太网（PCIe，WiFi路由器）作为私有连接
        $privateConfig = $share.INetSharingConfigurationForINetConnection($ethernetConn)
        $privateConfig.EnableSharing(1)  # 1 = private
        Write-Log "已启用以太网（PCIe）为私有连接"
    } else {
        Write-Log "警告：未找到网络连接"
    }
} catch {
    Write-Log "错误：COM接口操作失败 - $($_.Exception.Message)"
}

# 将私有网卡强制设为 Private
# 原因：ICS 启用后私有网卡没有网关/DNS，Windows 会把它归类为"未识别的网络"(Public)，
# 而"网络发现"(WSD/SSDP/LLMNR)与"文件共享"入站规则只在 Private 配置文件下生效，
# 结果局域网内电脑互相看不到、无法访问共享。ICS 重启后会重置回 Public，必须每次修正。
Write-Log "设置以太网（PCIe）网络类别为 Private..."
$categoryOK = $false
for ($i = 1; $i -le 12; $i++) {
    try {
        $prof = Get-NetConnectionProfile -InterfaceAlias "以太网" -ErrorAction SilentlyContinue
        if ($prof -and $prof.NetworkCategory -eq "Private") {
            Write-Log "网络类别已是 Private"
            $categoryOK = $true
            break
        }
        Set-NetConnectionProfile -InterfaceAlias "以太网" -NetworkCategory Private -ErrorAction Stop
        Start-Sleep -Seconds 2
        $prof = Get-NetConnectionProfile -InterfaceAlias "以太网" -ErrorAction SilentlyContinue
        if ($prof -and $prof.NetworkCategory -eq "Private") {
            Write-Log "网络类别已设为 Private（第 $i 次尝试成功）"
            $categoryOK = $true
            break
        }
    } catch {
        Write-Log "第 $i 次设置网络类别失败: $($_.Exception.Message)"
    }
    Start-Sleep -Seconds 5
}
if (-not $categoryOK) {
    Write-Log "警告：网络类别仍未设为 Private，局域网发现/共享可能不可用"
}

# 重新落实接口度量（ICS 启用后可能重置）
# 【核心】卡1 度量 9999、卡2 度量 10 —— 保证校园网(卡2)永远是首选出口，
# 即使之后路由器插上并给卡1下发网关，也无法抢走默认路由。
Write-Log "设置接口度量（卡1=9999，卡2=10）..."
netsh interface ipv4 set interface "以太网" metric=9999 | Out-Null
netsh interface ipv4 set interface "以太网 2" metric=10 | Out-Null

# 记录默认路由，便于事后排查（正常情况下应只有 以太网 2 一条）
$defRoutes = Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue
foreach ($r in $defRoutes) {
    Write-Log "默认路由: $($r.InterfaceAlias) -> $($r.NextHop) (InterfaceMetric=$($r.InterfaceMetric))"
}
if (-not ($defRoutes | Where-Object { $_.InterfaceAlias -eq "以太网 2" })) {
    Write-Log "警告：以太网 2 没有默认路由，校园网可能未生效"
}
if ($defRoutes | Where-Object { $_.InterfaceAlias -eq "以太网" }) {
    Write-Log "警告：以太网（PCIe）出现了默认路由，可能会抢占校园网出口"
}

# 验证服务状态
$service = Get-Service SharedAccess
Write-Log "SharedAccess服务状态: $($service.Status)"

# 验证IP配置
$finalIP = Get-NetIPAddress -InterfaceAlias "以太网" -AddressFamily IPv4 -ErrorAction SilentlyContinue
Write-Log "最终以太网（PCIe）IP: $($finalIP.IPAddress)"

Write-Log "===== 最终ICS修复完成 ====="
