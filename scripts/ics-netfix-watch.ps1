# ics-netfix-watch.ps1
# ICS 双网卡「自愈」脚本 —— 网络状态变化时触发，只做发现异常才修的最小动作
# 不重启服务、不重写 IP，因此可以安全地频繁运行
#
# 修正项：
#   1. ICS 共享若被自动关闭 -> 重新启用（卡2 公网 / 卡1 私有）★需卡1 链路 Up 才动
#   2. 卡1 网络类别掉成 Public -> 改回 Private
#   3. 接口度量被重置 -> 卡1=9999 / 卡2=10
#   4. 卡1 注册表 EnableDHCP 被打开 -> 关回 0
#   5. 卡1 意外出现默认路由 -> 记录告警（不自动改，交人工判断）

$logFile       = "C:\Scripts\ics-netfix-watch.log"
$ifPrivate     = "以太网"      # 卡1：PCIe，接 WiFi 路由器，ICS 私有端
$ifPublic      = "以太网 2"    # 卡2：USB，接校园网，ICS 公网端
$metricPrivate = 9999
$metricPublic  = 10

function Write-Log {
    param([string]$Message)
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - $Message" | Out-File -FilePath $logFile -Append -Encoding UTF8
}

# 日志轮转：超过 200KB 就从头来，避免无限增长
if ((Test-Path $logFile) -and ((Get-Item $logFile).Length -gt 200KB)) {
    Remove-Item $logFile -Force -ErrorAction SilentlyContinue
}

$changed = $false

# ---------------------------------------------------------------
# 1. ICS 共享检查：被关闭则重新启用（仅当卡1 有链路，避免无效反复开关）
# ---------------------------------------------------------------
$privAdapter = Get-NetAdapter -Name $ifPrivate -ErrorAction SilentlyContinue
if ($privAdapter -and $privAdapter.Status -eq "Up") {
    try {
        $share = New-Object -ComObject HNetCfg.HNetShare
        $privConn = $null; $pubConn = $null
        foreach ($conn in $share.EnumEveryConnection) {
            $p = $share.NetConnectionProps.Invoke($conn)
            if ($p.Name -eq $ifPrivate) { $privConn = $conn }
            if ($p.Name -eq $ifPublic)  { $pubConn  = $conn }
        }
        if ($privConn -and $pubConn) {
            $cPriv = $share.INetSharingConfigurationForINetConnection($privConn)
            $cPub  = $share.INetSharingConfigurationForINetConnection($pubConn)
            if (-not ($cPriv.SharingEnabled -and $cPub.SharingEnabled)) {
                $cPriv.DisableSharing(); $cPub.DisableSharing()
                Start-Sleep -Seconds 2
                $cPub.EnableSharing(0)    # 0 = 公网端
                Start-Sleep -Seconds 2
                $cPriv.EnableSharing(1)   # 1 = 私有端
                Start-Sleep -Seconds 3
                Write-Log "修正: ICS 共享被关闭，已重新启用（$ifPublic=公网, $ifPrivate=私有）"
                $changed = $true
            }
        }
    } catch {
        Write-Log "失败: ICS 共享检查 - $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------
# 2. 卡1 网络类别必须为 Private
# ---------------------------------------------------------------
$prof = Get-NetConnectionProfile -InterfaceAlias $ifPrivate -ErrorAction SilentlyContinue
if ($prof -and $prof.NetworkCategory -ne "Private") {
    try {
        Set-NetConnectionProfile -InterfaceAlias $ifPrivate -NetworkCategory Private -ErrorAction Stop
        Write-Log "修正: $ifPrivate 网络类别 $($prof.NetworkCategory) -> Private"
        $changed = $true
    } catch {
        Write-Log "失败: 设置网络类别 - $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------
# 3. 接口度量：卡1=9999 / 卡2=10（保证校园网永远是首选出口）
# ---------------------------------------------------------------
$mp = (Get-NetIPInterface -InterfaceAlias $ifPrivate -AddressFamily IPv4 -ErrorAction SilentlyContinue).InterfaceMetric
$mu = (Get-NetIPInterface -InterfaceAlias $ifPublic  -AddressFamily IPv4 -ErrorAction SilentlyContinue).InterfaceMetric
if ($mp -ne $metricPrivate) {
    netsh interface ipv4 set interface $ifPrivate metric=$metricPrivate | Out-Null
    Write-Log "修正: $ifPrivate 度量 $mp -> $metricPrivate"
    $changed = $true
}
if ($mu -ne $metricPublic) {
    netsh interface ipv4 set interface $ifPublic metric=$metricPublic | Out-Null
    Write-Log "修正: $ifPublic 度量 $mu -> $metricPublic"
    $changed = $true
}

# ---------------------------------------------------------------
# 4. 卡1 注册表 EnableDHCP 必须为 0（关掉 DHCP，避免从路由器拿网关）
# ---------------------------------------------------------------
$guid = (Get-NetAdapter -Name $ifPrivate -ErrorAction SilentlyContinue).InterfaceGuid
if ($guid) {
    $reg = "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$guid"
    $dhcp = (Get-ItemProperty $reg -ErrorAction SilentlyContinue).EnableDHCP
    if ($dhcp -ne 0) {
        Set-ItemProperty -Path $reg -Name "EnableDHCP" -Value 0 -Type DWord -ErrorAction SilentlyContinue
        Write-Log "修正: $ifPrivate 注册表 EnableDHCP $dhcp -> 0"
        $changed = $true
    }
}

# ---------------------------------------------------------------
# 5. 告警：卡1 若意外出现默认路由（会抢占校园网出口）
# ---------------------------------------------------------------
$badRoute = Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
            Where-Object { $_.InterfaceAlias -eq $ifPrivate }
if ($badRoute) {
    Write-Log "告警: $ifPrivate 出现默认路由 -> $($badRoute.NextHop) (metric=$($badRoute.InterfaceMetric))，建议手动检查"
}

if ($changed) { Write-Log "本次运行有修正" }
