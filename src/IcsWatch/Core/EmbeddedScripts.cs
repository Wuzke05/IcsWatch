using System.Reflection;
using System.Security.Cryptography;

namespace IcsWatch.Core;

/// <summary>
/// 内嵌 PowerShell 脚本的释放与一致性校验。
///
/// 设计目的（对应"把前面几个脚本也集成进去"）：
///  1. 脚本随 exe 一起分发，不怕丢失（首启释放到 scripts\）
///  2. 哈希不符时自动重放，防止被误改
///  3. UI 可对比"内嵌版本 vs 磁盘版本"是否一致
///  4. --legacy-repair 走这套脚本作为应急回退路径
/// </summary>
public static class EmbeddedScripts
{
    /// <summary>已知的脚本文件名（用后缀匹配资源名，避免依赖资源命名规则）</summary>
    private static readonly string[] Known =
    {
        "fix-ics-final.ps1",
        "ics-netfix-watch.ps1",
        "ics-healthcheck.ps1",
    };

    public sealed class ScriptFile
    {
        public string Name { get; init; } = "";
        public string DiskPath { get; init; } = "";
        public string EmbeddedHash { get; init; } = "";
        public string DiskHash { get; init; } = "";
        public bool Extracted { get; init; }
        /// <summary>磁盘内容与内嵌内容一致</summary>
        public bool Match => !string.IsNullOrEmpty(DiskHash) && DiskHash == EmbeddedHash;
        public string StatusText => string.IsNullOrEmpty(DiskHash) ? "磁盘缺失" : (Match ? "哈希一致 ✅" : "已被修改 ⚠️");
    }

    /// <summary>只读比对，不写盘（UI 用）</summary>
    public static List<ScriptFile> Describe(string targetDir)
    {
        var list = new List<ScriptFile>();
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames();

        foreach (var file in Known)
        {
            var res = names.FirstOrDefault(n => n.EndsWith(file, StringComparison.OrdinalIgnoreCase));
            if (res == null) continue;

            using var s = asm.GetManifestResourceStream(res);
            if (s == null) continue;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            var embeddedHash = Sha256(bytes);

            var disk = Path.Combine(targetDir, file);
            var diskHash = File.Exists(disk) ? Sha256(File.ReadAllBytes(disk)) : "";

            list.Add(new ScriptFile
            {
                Name = file,
                DiskPath = disk,
                EmbeddedHash = embeddedHash,
                DiskHash = diskHash,
            });
        }
        return list;
    }

    /// <summary>首启释放（哈希不符则重放）。返回释放结果。</summary>
    public static List<ScriptFile> EnsureExtracted(string targetDir)
    {
        try { Directory.CreateDirectory(targetDir); } catch (Exception __ex) { AppLog.Swallow("EmbeddedScripts.cs:73", __ex); }

        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames();
        var done = new List<string>();

        foreach (var file in Known)
        {
            var res = names.FirstOrDefault(n => n.EndsWith(file, StringComparison.OrdinalIgnoreCase));
            if (res == null) continue;

            try
            {
                byte[] bytes;
                using (var s = asm.GetManifestResourceStream(res)!)
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    bytes = ms.ToArray();
                }

                var disk = Path.Combine(targetDir, file);
                bool need = true;
                if (File.Exists(disk))
                {
                    try { need = Sha256(File.ReadAllBytes(disk)) != Sha256(bytes); } catch { need = true; }
                }

                if (need)
                {
                    File.WriteAllBytes(disk, bytes);
                    AppLog.Info($"释放内嵌脚本: {file} -> {disk}");
                }
                done.Add(file);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"释放内嵌脚本 {file} 失败: " + AppLog.Ex(ex));
            }
        }

        if (done.Count == 0) AppLog.Warn("未发现任何内嵌 .ps1 资源");
        return Describe(targetDir);
    }

    public static string Sha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data));
    }
}
