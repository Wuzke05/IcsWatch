using System.Management;

namespace IcsWatch.Core;

/// <summary>
/// 网络类别（Public / Private / Domain）的原生读写。
///
/// ── 实现选型记录（踩坑留档）─────────────────────────────────
/// 【弃用】NLM COM（INetworkListManager / INetwork / IEnumNetworks）
///   在本机 Win11 24H2 上实测：即使按 `dual` 接口补齐 IDispatch 的 4 个
///   vtable 槽位（否则调用直接错位），通过了 GetNetworks，但
///   `IEnumNetworks.Next` 仍然抛 AccessViolationException ——
///   这是 Windows 的致命错误，try/catch 拦不住，会直接带走整个进程。
///   对常驻守护程序来说这是不可接受的（一次维修就把守护进程搞死）。
///
/// 【采用】WMI：`root\StandardCimv2:MSFT_NetConnectionProfile` 的
///   NetworkCategory 属性是可写的，改完 Put() 立即生效，
///   语义完全等价于 PowerShell 的 Set-NetConnectionProfile。
///   优点：零 COM 互操作风险、异常可捕获、不需要子进程隔离。
/// </summary>
public static class NetworkCategoryApi
{
    public const int Public = 0;
    public const int Private = 1;
    public const int Domain = 2;

    private const string ScopePath = @"\\.\root\StandardCimv2";

    /// <summary>读网卡所属网络的类别；取不到返回 -1</summary>
    public static int GetCategory(string adapterAlias)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(ScopePath),
                new ObjectQuery("SELECT NetworkCategory FROM MSFT_NetConnectionProfile " +
                                $"WHERE InterfaceAlias='{Esc(adapterAlias)}'"));
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    var v = o["NetworkCategory"];
                    return v == null ? -1 : Convert.ToInt32(v);
                }
            }
        }
        catch (Exception ex) { AppLog.Debug("读网络类别失败: " + AppLog.Ex(ex)); }
        return -1;
    }

    public static bool SetPrivate(string adapterAlias, out string err)
        => SetCategory(adapterAlias, Private, out err);

    public static bool SetPublic(string adapterAlias, out string err)
        => SetCategory(adapterAlias, Public, out err);

    /// <summary>写网络类别（立即生效）</summary>
    public static bool SetCategory(string adapterAlias, int category, out string err)
    {
        err = "";
        try
        {
            var hits = 0;
            using var searcher = new ManagementObjectSearcher(new ManagementScope(ScopePath),
                new ObjectQuery("SELECT * FROM MSFT_NetConnectionProfile " +
                                $"WHERE InterfaceAlias='{Esc(adapterAlias)}'"));
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    o["NetworkCategory"] = (uint)category;
                    o.Put();
                    hits++;
                }
            }
            if (hits == 0) { err = "未找到该网卡的连接配置文件"; return false; }
            return true;
        }
        catch (Exception ex) { err = AppLog.Ex(ex); return false; }
    }

    public static string CategoryText(int c) => c switch
    {
        Public => "Public",
        Private => "Private",
        Domain => "Domain",
        _ => "未知",
    };

    private static string Esc(string s) => s.Replace("'", "''");
}
