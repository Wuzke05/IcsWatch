using System.Collections;
using System.Runtime.InteropServices;

namespace IcsWatch.Core;

/// <summary>一条网络连接的 ICS 共享配置</summary>
public sealed class IcsConnState
{
    public string Name { get; init; } = "";
    public bool Enabled { get; init; }
    /// <summary>0 = 公网端(Public) / 1 = 私有端(Private)</summary>
    public int Type { get; init; }
    public string TypeText => Type == 0 ? "公网端" : "私有端";
}

/// <summary>
/// ICS 共享配置的读写（COM: HNetCfg.HNetShare）。
///
/// 注意：C# 里没有参数化属性的语法，`INetSharingConfigurationForINetConnection(conn)`
/// 这一类的调用必须走反射 InvokeMember。这里把它封装成一个实例类，
/// 保持 COM 对象存活，便于检测与修复共用。
/// </summary>
public sealed class IcsShareManager : IDisposable
{
    private object? _share;
    private Type? _t;
    private readonly Hashtable _conns = new();   // name -> connection object

    public bool Available { get; private set; }
    public string LastError { get; private set; } = "";

    public IcsShareManager()
    {
        try
        {
            _t = Type.GetTypeFromProgID("HNetCfg.HNetShare");
            if (_t == null) { LastError = "找不到 HNetCfg.HNetShare"; return; }
            _share = Activator.CreateInstance(_t);
            if (_share == null) { LastError = "HNetCfg.HNetShare 实例化失败"; return; }

            var conns = Invoke("EnumEveryConnection");
            if (conns is IEnumerable en)
            {
                foreach (var conn in en)
                {
                    var props = _t.InvokeMember("NetConnectionProps",
                        System.Reflection.BindingFlags.GetProperty, null, _share, new[] { conn });
                    var name = props?.GetType().InvokeMember("Name",
                        System.Reflection.BindingFlags.GetProperty, null, props, null) as string;
                    if (!string.IsNullOrEmpty(name)) _conns[name!] = conn!;
                }
            }
            Available = true;
        }
        catch (Exception ex)
        {
            LastError = AppLog.Ex(ex);
            AppLog.Warn("初始化 ICS COM 失败: " + LastError);
        }
    }

    private object? Invoke(string member, params object[] args)
    {
        if (_share == null || _t == null) return null;
        return _t.InvokeMember(member,
            System.Reflection.BindingFlags.GetProperty | System.Reflection.BindingFlags.InvokeMethod,
            null, _share, args);
    }

    private object? ConfigFor(string name)
    {
        if (!_conns.ContainsKey(name)) return null;
        return Invoke("INetSharingConfigurationForINetConnection", _conns[name]!);
    }

    public IcsConnState? Get(string name)
    {
        try
        {
            var cfg = ConfigFor(name);
            if (cfg == null) return null;
            var t = cfg.GetType();
            var en = t.InvokeMember("SharingEnabled", System.Reflection.BindingFlags.GetProperty, null, cfg, null);
            var ty = t.InvokeMember("SharingConnectionType", System.Reflection.BindingFlags.GetProperty, null, cfg, null);
            return new IcsConnState
            {
                Name = name,
                Enabled = en is bool b && b,
                Type = ty == null ? -1 : Convert.ToInt32(ty),
            };
        }
        catch (Exception ex)
        {
            LastError = AppLog.Ex(ex);
            return null;
        }
    }

    public List<IcsConnState> GetMany(params string[] names)
    {
        var list = new List<IcsConnState>();
        foreach (var n in names)
        {
            var s = Get(n);
            if (s != null) list.Add(s);
        }
        return list;
    }

    /// <summary>启用共享：type 0 = 公网端，1 = 私有端</summary>
    public bool Enable(string name, int type) => Switch(name, type, enable: true);

    public bool Disable(string name) => Switch(name, -1, enable: false);

    /// <summary>
    /// 启用/禁用共享的统一入口。
    ///
    /// 【为什么要重试】休眠/唤醒的转换窗口里，HNetCfg 会抛 0x8007030F
    /// （"系统电源状态正从 … 转换"）。这类失败是**暂时的**：等几秒网络栈收敛后
    /// 再调一次就成功。原来一次失败就直接返回 false，于是整条维修链上后面
    /// 7 项（静态地址 / 类别 / 度量 / DHCP 监听…）全部连带失败。
    /// 现在对"电源态错误"做两次退避重试（4s / 8s），其它错误立刻返回失败。
    /// </summary>
    private bool Switch(string name, int type, bool enable)
    {
        var what = enable ? "启用" : "禁用";

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var cfg = ConfigFor(name);
                if (cfg == null) { LastError = $"未找到连接「{name}」"; return false; }

                if (enable)
                    cfg.GetType().InvokeMember("EnableSharing", System.Reflection.BindingFlags.InvokeMethod,
                        null, cfg, new object[] { type });
                else
                    cfg.GetType().InvokeMember("DisableSharing", System.Reflection.BindingFlags.InvokeMethod,
                        null, cfg, null);

                AppLog.Info(enable
                    ? $"已启用共享: {name} ({(type == 0 ? "公网端" : "私有端")})"
                    : $"已禁用共享: {name}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = AppLog.Ex(ex);

                if (enable && PowerStateWatcher.IsPowerStateError(ex) && attempt < 3)
                {
                    var waitMs = attempt == 1 ? 4000 : 8000;
                    AppLog.Warn($"[电源] {what}共享 {name} 撞到电源状态切换（0x8007030F）" +
                                $"· {waitMs / 1000}s 后重试（第 {attempt} 次）");
                    Thread.Sleep(waitMs);
                    continue;
                }

                AppLog.Error($"{what}共享失败 {name}: {LastError}");
                return false;
            }
        }
        return false;
    }

    public void Dispose()
    {
        try { if (_share != null) Marshal.ReleaseComObject(_share); } catch (Exception __ex) { AppLog.Swallow("IcsShareManager.cs:141", __ex); }
        _share = null;
        _conns.Clear();
    }
}
