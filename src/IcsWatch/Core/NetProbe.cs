using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace IcsWatch.Core;

/// <summary>单块网卡的观测快照</summary>
public sealed class AdapterInfo
{
    public string Name = "";
    public bool Exists;
    public bool IsUp;
    public List<string> Ipv4 = new();
    public List<string> Ipv4WithPrefix = new();
    public bool HasGateway;
    public string Gateway = "";
    public int? Metric;
    public string? Category;     // Private / Public / Domain
    public string? ProfileName;
    public string? Guid;
    public int? IfIndex;
    public string LinkSpeed = "";
}

/// <summary>
/// 原生网络状态读取（NetworkInterface + WMI + 注册表 + P/Invoke），不 spawn PowerShell。
/// 与 ics-healthcheck.ps1 的 22 项一一对应。
/// </summary>
public static class NetProbe
{
    public static string? LastWmiError;

    // ── 网卡枚举缓存 ────────────────────────────────────────────
    // 【为什么必须缓存】实测 Interfaces() 单次约 **103 毫秒**
    // （内部要扫注册表/驱动），而拿到对象后读统计只要 0.1 毫秒。
    // 流量采样每秒枚举一次 = 10% 的 CPU；体检每项都重新枚举 = 单次体检 0.6 秒。
    // 这里缓存 3 秒（小于 5 秒体检周期，仍能及时发现网卡增减），属性依然实时读取。
    private static System.Net.NetworkInformation.NetworkInterface[]? _niCache;
    private static DateTime _niCacheAt = DateTime.MinValue;

    /// <summary>取网卡对象列表（3 秒缓存）</summary>
    public static System.Net.NetworkInformation.NetworkInterface[] Interfaces()
    {
        if (_niCache != null && (DateTime.Now - _niCacheAt).TotalMilliseconds < 3000) return _niCache;
        try
        {
            _niCache = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            _niCacheAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            AppLog.Swallow("NetProbe.Interfaces", ex);
            return _niCache ?? Array.Empty<System.Net.NetworkInformation.NetworkInterface>();
        }
        return _niCache;
    }

    /// <summary>按名字取网卡对象（精确匹配；找不到返回 null）</summary>
    public static System.Net.NetworkInformation.NetworkInterface? FindNi(string name)
        => Interfaces().FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));

    // ── 网卡基础信息 ────────────────────────────────────────────
    /// <summary>
    /// 网卡是否被"管理性禁用"（Disable-NetAdapter / 设备管理器禁用）。
    /// 【判断依据】Win32_NetworkAdapter.ConfigManagerErrorCode == 22（CM_PROB_DISABLED）。
    /// 踩过的两个坑：
    ///  ① .NET 的 OperationalStatus 禁用后仍可能报 Up（它反映链路/介质状态）
    ///  ② 驱动注册表里不一定有 *IfAdminStatus（Realtek 就没有）
    ///  ③ NetEnabled=false 也不能用 —— 无线网卡"只是没连上"时它同样是 false
    /// </summary>
    public static bool? IsAdminDisabled(string adapterName)
    {
        // 【注意】网卡被禁用后会从 NetworkInterface 枚举里消失（ReadAdapter 变成不存在），
        // 所以这里不能走 ReadAdapter 拿 GUID，直接用"连接名称"NetConnectionID 匹配 ——
        // 它在禁用状态下依然存在。
        try
        {
            using var s = new ManagementObjectSearcher(
                new ObjectQuery("SELECT ConfigManagerErrorCode FROM Win32_NetworkAdapter " +
                                $"WHERE NetConnectionID='{adapterName.Replace("'", "''")}'"));
            foreach (ManagementBaseObject o in s.Get())
            {
                var v = o["ConfigManagerErrorCode"];
                if (v == null) return null;
                return Convert.ToInt32(v) == 22;
            }
        }
        catch (Exception ex) { LastWmiError = ex.Message; }
        return null;
    }

    /// <summary>
    /// 是否 USB 总线上的网卡（USB 千兆网卡 / 手机 RNDIS 共享）。
    /// 【为什么重要】**USB 网卡绝不能 disable/enable** —— 实测会让设备掉出 USB 总线，
    /// 之后 `netsh admin=enabled` 报 ERROR_NO_MORE_ITEMS，pnputil /scan-devices 也扫不回来，
    /// 只能物理拔插（2026-10-20 卡2 = Realtek USB GbE 就这么丢的）。
    /// PCIe 网卡（卡1）disable/enable 是安全的。
    /// </summary>
    public static bool IsUsbAdapter(string adapterName)
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                new ObjectQuery("SELECT PNPDeviceID FROM Win32_NetworkAdapter " +
                                $"WHERE NetConnectionID='{adapterName.Replace("'", "''")}'"));
            foreach (ManagementBaseObject o in s.Get())
            {
                var pid = o["PNPDeviceID"]?.ToString() ?? "";
                if (pid.Length > 0) return pid.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) { AppLog.Swallow("NetProbe.IsUsbAdapter", ex); }

        // 回退：按网卡描述判断（"Realtek USB GbE" / "Remote NDIS Compatible Device" …）
        try
        {
            var ni = Interfaces()
                     .FirstOrDefault(n => string.Equals(n.Name, adapterName, StringComparison.OrdinalIgnoreCase));
            var d = ni?.Description ?? "";
            return d.Contains("USB", StringComparison.OrdinalIgnoreCase)
                || d.Contains("NDIS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { AppLog.Swallow("NetProbe.IsUsbAdapter(fallback)", ex); return false; }
    }

    public static AdapterInfo ReadAdapter(string name)
    {
        var a = new AdapterInfo { Name = name, Exists = false };
        NetworkInterface? ni = null;
        try
        {
            ni = Interfaces()
                 .FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception __ex) { AppLog.Swallow("NetProbe.cs:75", __ex); }

        if (ni == null) return a;
        // 【严禁模糊匹配】原来这里有个兜底：n.Name.Contains(name) —— 想兼容"网卡被改名"。
        // 但它是**危险的**：卡1 被禁用后会从枚举里消失，Contains("以太网") 会匹配到
        // 「以太网 3」（手机 USB 共享网卡），于是程序把手机网卡当成卡1：
        //   · 读到 172.29.230.135 当成"卡1 主地址"
        //   · 把手机网卡默认路由判成"卡1 不该有默认路由"
        //   · 自动维修会把 192.168.137.1 写到手机网卡上（会弄断 USB 共享）
        // 所以只允许精确匹配；找不到就是找不到，由 IsAdminDisabled 判断"被禁用"。

        a.Exists = true;
        a.IsUp = ni.OperationalStatus == OperationalStatus.Up;
        try { a.Guid = ni.Id; } catch (Exception __ex) { AppLog.Swallow("NetProbe.cs:88", __ex); }
        try { a.LinkSpeed = ni.Speed > 0 ? (ni.Speed / 1_000_000) + " Mbps" : ""; } catch (Exception __ex) { AppLog.Swallow("NetProbe.cs:89", __ex); }

        try
        {
            var p = ni.GetIPProperties();
            foreach (var u in p.UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = u.Address.ToString();
                if (ip.StartsWith("169.254.")) continue;   // APIPA 由调用方另行判断
                a.Ipv4.Add(ip);
                a.Ipv4WithPrefix.Add($"{ip}/{u.PrefixLength}");
            }
            // 只要有一个非 0 网关，就认为该网卡会生成默认路由
            foreach (var g in p.GatewayAddresses)
            {
                if (g.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (g.Address.Equals(IPAddress.Any)) continue;
                a.HasGateway = true;
                a.Gateway = g.Address.ToString();
            }
            try { a.IfIndex = p.GetIPv4Properties()?.Index; } catch (Exception __ex) { AppLog.Swallow("NetProbe.cs:110", __ex); }
        }
        catch (Exception __ex) { AppLog.Swallow("NetProbe.cs:112", __ex); }

        a.Metric = GetInterfaceMetric(name);
        var (cat, prof) = GetConnectionProfile(name);
        a.Category = cat;
        a.ProfileName = prof;
        return a;
    }

    /// <summary>取该网卡上的 APIPA 地址（169.254.x.x），会有说明 DHCP 客户端在跑</summary>
    public static List<string> GetApipa(string name)
    {
        var res = new List<string>();
        try
        {
            var ni = Interfaces()
                     .FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
            if (ni == null) return res;
            foreach (var u in ni.GetIPProperties().UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork && u.Address.ToString().StartsWith("169.254."))
                    res.Add(u.Address.ToString());
        }
        catch (Exception __ex) { AppLog.Swallow("NetProbe.cs:134", __ex); }
        return res;
    }

    // ── 接口度量（WMI root/StandardCimv2 : MSFT_NetIPInterface）──
    public static int? GetInterfaceMetric(string alias)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\StandardCimv2");
            var q = new ObjectQuery(
                $"SELECT InterfaceMetric FROM MSFT_NetIPInterface WHERE InterfaceAlias='{Esc(alias)}' AND AddressFamily=2");
            using var s = new ManagementObjectSearcher(scope, q);
            foreach (ManagementBaseObject o in s.Get())
            {
                using (o as IDisposable) { }
                var v = o["InterfaceMetric"];
                if (v != null) return Convert.ToInt32(v);
            }
        }
        catch (Exception ex) { LastWmiError = "Metric: " + ex.Message; }
        return null;
    }

    // ── 网络类别（WMI root/StandardCimv2 : MSFT_NetConnectionProfile）──
    public static (string? category, string? profile) GetConnectionProfile(string alias)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\StandardCimv2");
            var q = new ObjectQuery(
                $"SELECT Name, NetworkCategory FROM MSFT_NetConnectionProfile WHERE InterfaceAlias='{Esc(alias)}'");
            using var s = new ManagementObjectSearcher(scope, q);
            foreach (ManagementBaseObject o in s.Get())
            {
                var name = o["Name"]?.ToString();
                var raw = o["NetworkCategory"];
                string? cat = raw == null ? null : Convert.ToInt64(raw) switch
                {
                    0 => "Public",
                    1 => "Private",
                    2 => "Domain",
                    _ => raw.ToString(),
                };
                return (cat, name);
            }
        }
        catch (Exception ex) { LastWmiError = "Profile: " + ex.Message; }
        return (null, null);
    }

    // ── 注册表 EnableDHCP（DHCP 开关最可靠的来源）──────────────────
    public static int? GetEnableDhcpReg(string? adapterGuid)
    {
        if (string.IsNullOrEmpty(adapterGuid)) return null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapterGuid}");
            var v = k?.GetValue("EnableDHCP");
            return v == null ? null : Convert.ToInt32(v);
        }
        catch { return null; }
    }

    // ── ICS DHCP 是否在监听（UDP 67）────────────────────────────
    public static bool UdpListening(string localIp, int port)
    {
        try
        {
            var want = IPAddress.Parse(localIp);
            foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners())
            {
                if (ep.Port == port && (ep.Address.Equals(want) || ep.Address.Equals(IPAddress.Any)))
                    return true;
            }
        }
        catch (Exception __ex) { AppLog.Swallow("NetProbe.cs:211", __ex); }
        return false;
    }

    // ── 服务 / 驱动状态 ─────────────────────────────────────────
    public static string ServiceState(string serviceName)
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(serviceName);
            return sc.Status.ToString();     // Running / Stopped / StopPending ...
        }
        catch (Exception ex) { return "ERR:" + ex.GetType().Name; }
    }

    // ── ARP 邻居（下游设备）────────────────────────────────────
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

    /// <summary>
    /// 删掉一条 ARP 邻居（僵尸租约）。
    /// 【为什么要它能删】ICS 给下游设备分配地址时会写一条 **Permanent** 静态 ARP 条目，
    /// 这类条目永不自动过期：同一台设备每重连一次就留一条，实测 7 小时累积 18 条。
    /// 253 个地址照这个速度约 8 天耗尽，最后所有人都拿不到地址。
    /// 【为什么用 netsh】`DeleteIpNetEntry` 对这些条目返回 1168（ERROR_NOT_FOUND）——
    /// 它们不在 ARP 缓存里，走不了这个 API；`netsh interface ipv4 delete neighbors` 才行。
    /// 删完会留下一条 `00-00-00-00-00-00 Unreachable` 的占位（正常现象，动态条目会自己老化）。
    /// </summary>
    public static bool DeleteNeighbor(string adapterName, string ip)
    {
        try
        {
            var r = ProcessRunner.Run("netsh.exe",
                $"interface ipv4 delete neighbors \"{adapterName}\" {ip}", 15_000);
            return r.ExitCode == 0;
        }
        catch (Exception __ex) { AppLog.Swallow("NetProbe.DeleteNeighbor", __ex); return false; }
    }

    public static List<(string ip, string mac, int ifIndex, int type)> GetArpTable()
    {
        var res = new List<(string, string, int, int)>();
        int size = 0;
        try
        {
            GetIpNetTable(IntPtr.Zero, ref size, false);
            if (size <= 0) return res;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetIpNetTable(buf, ref size, false) != 0) return res;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    IntPtr row = IntPtr.Add(buf, 4 + i * 24);
                    int idx = Marshal.ReadInt32(row);
                    int len = Marshal.ReadInt32(row, 4);
                    if (len <= 0 || len > 8) continue;
                    var mac = new byte[len];
                    Marshal.Copy(IntPtr.Add(row, 8), mac, 0, len);
                    int addr = Marshal.ReadInt32(row, 16);
                    int type = Marshal.ReadInt32(row, 20);
                    if (type != 3 && type != 4) continue;       // 只保留 dynamic(3)/static(4)
                    var ip = new IPAddress(BitConverter.GetBytes(addr)).ToString();
                    res.Add((ip, string.Join("-", mac.Select(b => b.ToString("X2"))), idx, type));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception __ex) { AppLog.Swallow("NetProbe.cs:260", __ex); }
        return res;
    }

    /// <summary>取指定网卡上、属于指定网段的邻居设备</summary>
    public static List<DownstreamDevice> GetDevices(string adapterName, string subnetPrefix)
    {
        var list = new List<DownstreamDevice>();
        var a = ReadAdapter(adapterName);
        if (a.IfIndex == null) return list;
        foreach (var (ip, mac, idx, _) in GetArpTable())
        {
            if (idx != a.IfIndex) continue;
            if (!ip.StartsWith(subnetPrefix, StringComparison.Ordinal)) continue;
            var seg = ip.Split('.');
            if (seg.Length == 4 && (seg[3] == "255" || seg[3] == "0")) continue;
            // 空 MAC = 解析失败的"未完成"条目；全 F = 广播。两者都不是设备。
            if (mac == "00-00-00-00-00-00" || mac == "FF-FF-FF-FF-FF-FF") continue;
            list.Add(new DownstreamDevice { Ip = ip, Mac = mac });
        }
        return list.OrderBy(d => int.TryParse(d.Ip.Split('.')[3], out var o) ? o : 999).ToList();
    }

    // ── 出网自测（普通 ping，用于"实际出网"检查）──────────────
    public static bool PingPlain(string target, int timeoutMs = 1500)
    {
        try
        {
            using var p = new Ping();
            var r = p.Send(target, timeoutMs);
            return r != null && r.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    private static string Esc(string s) => s.Replace("'", "''");
}
