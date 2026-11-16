using System.Reflection;

namespace IcsWatch.Core;

public sealed class TaskState
{
    public string Name { get; init; } = "";
    public bool Found { get; init; }
    public string State { get; init; } = "";       // Ready / Running / Disabled
    public DateTime? LastRun { get; init; }
    public int LastResult { get; init; }
    public string TriggerText { get; init; } = "";
    public string Err { get; init; } = "";

    public string ResultText => !Found ? "未部署" : (LastResult == 0 ? "0 ✅" : $"{LastResult} ⚠️");
}

/// <summary>
/// 读取计划任务状态（COM: Schedule.Service）—— 即"脚本功能的运行情况"。
/// 用反射避免引入 TaskScheduler 互操作程序集。
/// </summary>
public static class TaskWatcher
{
    public static TaskState Get(string taskName)
    {
        try
        {
            var t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) return new TaskState { Name = taskName, Found = false, Err = "找不到 Schedule.Service" };

            object? svc = Activator.CreateInstance(t);
            if (svc == null) return new TaskState { Name = taskName, Found = false, Err = "实例化 Schedule.Service 失败" };

            try
            {
                t.InvokeMember("Connect", BindingFlags.InvokeMethod, null, svc, new object?[] { null, null, null, null });

                var folder = t.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, svc, new object[] { @"\" });
                object? task;
                try
                {
                    task = folder!.GetType().InvokeMember("GetTask", BindingFlags.InvokeMethod, null, folder, new object[] { taskName });
                }
                catch (Exception ex)
                {
                    return new TaskState { Name = taskName, Found = false, Err = ex.Message };
                }
                if (task == null) return new TaskState { Name = taskName, Found = false, Err = "任务不存在" };

                var tt = task.GetType();
                object? Get(string m) { try { return tt.InvokeMember(m, BindingFlags.GetProperty, null, task, null); } catch { return null; } }

                var stateRaw = Get("State");
                var state = stateRaw == null ? "" : Convert.ToInt32(stateRaw) switch
                {
                    0 => "Unknown", 1 => "Disabled", 2 => "Queued", 3 => "Ready", 4 => "Running",
                    _ => stateRaw.ToString() ?? "",
                };

                DateTime? last = null;
                var lr = Get("LastRunTime");
                if (lr is DateTime d && d.Year > 1900) last = d;

                var lastResult = 0;
                var lres = Get("LastTaskResult");
                if (lres != null) { try { lastResult = Convert.ToInt32(lres); } catch (Exception __ex) { AppLog.Swallow("TaskWatcher.cs:66", __ex); } }

                // 触发器概要
                var trigText = DescribeTriggers(task, tt);

                return new TaskState
                {
                    Name = taskName,
                    Found = true,
                    State = state,
                    LastRun = last,
                    LastResult = lastResult,
                    TriggerText = trigText,
                };
            }
            finally { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(svc); } catch (Exception __ex) { AppLog.Swallow("TaskWatcher.cs:81", __ex); } }
        }
        catch (Exception ex)
        {
            return new TaskState { Name = taskName, Found = false, Err = AppLog.Ex(ex) };
        }
    }

    private static string DescribeTriggers(object task, Type tt)
    {
        var parts = new List<string>();
        try
        {
            var trigs = tt.InvokeMember("Definition", BindingFlags.GetProperty, null, task, null);
            if (trigs == null) return "";
            var defT = trigs.GetType();
            var coll = defT.InvokeMember("Triggers", BindingFlags.GetProperty, null, trigs, null);
            var countObj = coll?.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, coll, null);
            int count = countObj == null ? 0 : Convert.ToInt32(countObj);
            for (int i = 1; i <= count; i++)
            {
                var tr = coll!.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, coll, new object[] { i });
                if (tr == null) continue;
                var typeObj = tr.GetType().InvokeMember("Type", BindingFlags.GetProperty, null, tr, null);
                int type = typeObj == null ? -1 : Convert.ToInt32(typeObj);
                // 【曾经全错】原来的映射是 1→开机 / 2→登录 / 8→空闲 / 9→事件，与
                // TASK_TRIGGER_TYPE2 完全错位：现场把 LogonTrigger 显示成了"空闲"。
                // 正确枚举见下表（MSDN: TASK_TRIGGER_TYPE2）。
                parts.Add(type switch
                {
                    0 => "事件",
                    1 => "一次性时间",
                    2 => "每日",
                    3 => "每周",
                    4 => "每月",
                    5 => "每月第几周",
                    6 => "空闲",
                    7 => "注册",
                    8 => "开机",
                    9 => "登录",
                    10 => "会话状态变化",
                    11 => "自定义",
                    _ => "类型" + type,
                });
            }
        }
        catch (Exception __ex) { AppLog.Swallow("TaskWatcher.cs:114", __ex); }
        return string.Join("+", parts);
    }

    /// <summary>让任务立刻跑一次（用于界面的"立即运行"按钮）</summary>
    public static bool Run(string taskName)
    {
        try
        {
            var t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) return false;
            object? svc = Activator.CreateInstance(t);
            if (svc == null) return false;
            try
            {
                t.InvokeMember("Connect", BindingFlags.InvokeMethod, null, svc, new object?[] { null, null, null, null });
                var folder = t.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, svc, new object[] { @"\" });
                var task = folder!.GetType().InvokeMember("GetTask", BindingFlags.InvokeMethod, null, folder, new object[] { taskName });
                if (task == null) return false;
                task.GetType().InvokeMember("Run", BindingFlags.InvokeMethod, null, task, new object?[] { null });
                AppLog.Info($"已触发计划任务: {taskName}");
                return true;
            }
            finally { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(svc); } catch (Exception __ex) { AppLog.Swallow("TaskWatcher.cs:137", __ex); } }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"触发计划任务 {taskName} 失败: " + AppLog.Ex(ex));
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  任务名解析（配置里的名字可能过期）
    //
    //  【现场问题】config.json 里还写着 PowerShell 时代的
    //    "ICS Boot Fix" / "ICS Net Fix Watch" —— 这两个任务确实存在，但都已被
    //    Disabled（最后运行 9/28、9/29）；真正在用的 "IcsWatch" / "IcsWatch Watchdog"
    //    反而没被监测。于是界面把"自启与看门狗完全正常"报成了"计划任务 Disabled"。
    //
    //  【判据】不看名字，看**任务动作指向哪个 exe**：Path 等于本程序、
    //    且参数是否带 --watchdog 决定它是主程序还是看门狗。这样任务被改名也不怕。
    //  结果缓存：体检每 5 秒一轮，没必要每轮都扫 COM。
    // ══════════════════════════════════════════════════════════════

    private static readonly object ResolveGate = new();
    private static readonly Dictionary<bool, string> Resolved = new();

    /// <summary>本程序 exe 的完整路径（单文件发布下就是进程自身）</summary>
    private static string ExePath
    {
        get
        {
            try { return Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "IcsWatch.exe"); }
            catch { return Path.Combine(AppContext.BaseDirectory, "IcsWatch.exe"); }
        }
    }

    /// <summary>
    /// 解析"本程序对应的计划任务名"。
    /// 配置名优先 —— 但必须核实它的动作真的指向本 exe；否则全盘扫描找工作目录下的任务。
    /// 都找不到就原样返回配置名（让体检照旧报"未部署"，而不是瞎猜）。
    /// </summary>
    public static string Resolve(string configuredName, bool watchdog)
    {
        lock (ResolveGate)
        {
            if (Resolved.TryGetValue(watchdog, out var cached)) return cached;
        }

        var found = FindTask(configuredName, watchdog) ? configuredName : ScanFor(watchdog) ?? configuredName;

        lock (ResolveGate) { Resolved[watchdog] = found; }
        if (found != configuredName)
            AppLog.Info($"[任务] 配置名「{configuredName}」不对应本程序" +
                        $" → 实际使用「{found}」{(watchdog ? "（看门狗）" : "（主程序）")}");
        return found;
    }

    /// <summary>清掉解析缓存（换网卡/换任务后想重新探测时用）</summary>
    public static void ResetResolveCache()
    {
        lock (ResolveGate) Resolved.Clear();
    }

    /// <summary>某个名字的任务是不是"我们的"（动作指向本 exe，参数匹配主程序/看门狗）</summary>
    private static bool FindTask(string name, bool watchdog)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        try
        {
            return WithFolder(folder => TaskMatches(folder!, name, watchdog), false);
        }
        catch (Exception __ex) { AppLog.Swallow("TaskWatcher.FindTask", __ex); return false; }
    }

    /// <summary>扫描根目录下所有任务，找第一个动作指向本 exe 的</summary>
    private static string? ScanFor(bool watchdog)
    {
        try
        {
            return WithFolder<string?>(folder =>
            {
                var coll = folder!.GetType().InvokeMember("GetTasks", BindingFlags.InvokeMethod, null, folder, new object[] { 1 });
                if (coll == null) return (string?)null;
                var count = Convert.ToInt32(coll.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, coll, null));
                for (int i = 1; i <= count; i++)
                {
                    var task = coll.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, coll, new object[] { i });
                    if (task == null) continue;
                    var name = task.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, task, null) as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (ActionsMatch(task, watchdog)) return name;
                }
                return (string?)null;
            }, null);
        }
        catch (Exception __ex) { AppLog.Swallow("TaskWatcher.ScanFor", __ex); return null; }
    }

    private static bool TaskMatches(object folder, string name, bool watchdog)
    {
        try
        {
            var task = folder.GetType().InvokeMember("GetTask", BindingFlags.InvokeMethod, null, folder, new object[] { name });
            if (task == null) return false;
            return ActionsMatch(task, watchdog);
        }
        catch { return false; }
    }

    /// <summary>任务的第一个 Exec 动作是不是"本 exe + 对应参数"</summary>
    private static bool ActionsMatch(object task, bool watchdog)
    {
        try
        {
            var def = task.GetType().InvokeMember("Definition", BindingFlags.GetProperty, null, task, null);
            if (def == null) return false;
            var acts = def.GetType().InvokeMember("Actions", BindingFlags.GetProperty, null, def, null);
            if (acts == null) return false;

            var count = Convert.ToInt32(acts.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, acts, null));
            for (int i = 1; i <= count; i++)
            {
                var a = acts.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, acts, new object[] { i });
                if (a == null) continue;
                var type = Convert.ToInt32(a.GetType().InvokeMember("Type", BindingFlags.GetProperty, null, a, null));
                if (type != 0) continue;                       // 0 = TASK_ACTION_EXEC

                var path = (a.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, a, null) as string ?? "").Trim();
                var args = (a.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, a, null) as string ?? "").Trim();

                if (!SamePath(path, ExePath)) continue;
                var isWatchdog = args.Contains("--watchdog", StringComparison.OrdinalIgnoreCase);
                if (isWatchdog == watchdog) return true;
            }
        }
        catch (Exception __ex) { AppLog.Swallow("TaskWatcher.ActionsMatch", __ex); }
        return false;
    }

    private static bool SamePath(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>连上任务计划服务、拿到根目录，跑一段逻辑后释放 COM</summary>
    private static T WithFolder<T>(Func<object?, T> body, T fallback)
    {
        var t = Type.GetTypeFromProgID("Schedule.Service");
        if (t == null) return fallback;
        object? svc = null;
        try
        {
            svc = Activator.CreateInstance(t);
            if (svc == null) return fallback;
            t.InvokeMember("Connect", BindingFlags.InvokeMethod, null, svc, new object?[] { null, null, null, null });
            var folder = t.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, svc, new object[] { @"\" });
            return body(folder);
        }
        catch (Exception __ex) { AppLog.Swallow("TaskWatcher.WithFolder", __ex); return fallback; }
        finally
        {
            if (svc != null) { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(svc); } catch { } }
        }
    }
}
