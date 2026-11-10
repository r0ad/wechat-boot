using System.Diagnostics;
using System.Text;

namespace WeChatBoot.Core;

/// <summary>一个被识别到的微信主程序。</summary>
public sealed class WeChatProduct
{
    /// <summary>产品标识：Weixin（4.x）/ WeChat（3.x）。</summary>
    public required string Key { get; init; }

    /// <summary>展示名，如「微信 4.x」。</summary>
    public required string DisplayName { get; init; }

    /// <summary>主程序完整路径。</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>文件版本，取不到则为 null。</summary>
    public string? Version { get; init; }

    /// <summary>来源：手动指定 / 注册表 / 运行中进程 / 目录扫描。</summary>
    public required string Source { get; init; }

    public string Directory => Path.GetDirectoryName(ExecutablePath) ?? string.Empty;

    /// <summary>下拉框显示文本。</summary>
    public string ToDisplayText() =>
        Version is null ? $"{DisplayName} · {Source}" : $"{DisplayName} {Version} · {Source}";

    public override string ToString() => ToDisplayText();
}

/// <summary>
/// 自动寻址：按「手动指定 → 注册表 → 运行中的进程 → 常见目录扫描」的优先级
/// 找到微信主程序。任何一步失败都只是跳过，不会抛异常。
/// </summary>
public static class WeChatLocator
{
    public const string WeixinExe = "Weixin.exe";
    public const string WeChatExe = "WeChat.exe";

    private sealed record ProductDefinition(string Key, string DisplayName, string RegistrySubKey, string ExeName);

    private static readonly ProductDefinition[] Definitions =
    {
        // 4.x 起程序改名为 Weixin.exe，注册表键也从 WeChat 改成 Weixin。
        new("Weixin", "微信 4.x", @"Software\Tencent\Weixin", WeixinExe),
        new("WeChat", "微信 3.x", @"Software\Tencent\WeChat", WeChatExe),
    };

    /// <summary>识别所有已知的微信主程序，按 4.x → 3.x 顺序返回（同路径自动去重）。</summary>
    public static IReadOnlyList<WeChatProduct> DetectAll(AppConfig? config = null, Action<string>? log = null)
    {
        var found = new List<WeChatProduct>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var def in Definitions)
        {
            var candidates = new List<WeChatProduct>();

            // 1) 手动维护的路径优先（用户指定就必须用它）
            if (config?.ExecutablePath is { Length: > 0 } manual &&
                string.Equals(Path.GetFileName(manual), def.ExeName, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(manual))
            {
                candidates.Add(Create(def, manual, "手动指定", log));
            }

            // 2) 注册表 InstallPath（微信自己的安装记录，最可靠）
            try
            {
                var installPath = ReadInstallPath(def.RegistrySubKey);
                if (installPath is not null)
                {
                    var exe = Path.Combine(installPath, def.ExeName);
                    if (File.Exists(exe)) candidates.Add(Create(def, exe, "注册表", log));
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"读取注册表失败：{ex.Message}");
            }

            // 3) 正在运行的进程（能拿到"当前真正在跑的那一份"，版本最准）
            foreach (var p in InstanceManager.GetFamilyProcesses())
            {
                if (!string.Equals(p.ProcessName, def.Key, StringComparison.OrdinalIgnoreCase)) continue;
                var image = InstanceManager.GetImagePath(p);
                if (image is not null && File.Exists(image)) candidates.Add(Create(def, image, "运行中进程", log));
            }

            // 4) 常见安装目录扫描（注册表被清理/绿色版时兜底）
            foreach (var scanned in ScanCommonLocations(def))
            {
                candidates.Add(Create(def, scanned, "目录扫描", log));
            }

            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c.ExecutablePath);
                if (seen.Add(full)) found.Add(c);
            }
        }

        return found;
    }

    private static WeChatProduct Create(ProductDefinition def, string exePath, string source, Action<string>? log)
    {
        return new WeChatProduct
        {
            Key = def.Key,
            DisplayName = def.DisplayName,
            ExecutablePath = Path.GetFullPath(exePath),
            Version = ReadVersion(exePath),
            Source = source,
        };
    }

    private static string? ReadInstallPath(string subKey)
    {
        // HKCU 覆盖绝大多数安装；HKLM 兼容管理员安装；32/64 视图都试一遍。
        var hives = new[] { Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryHive.LocalMachine };
        var views = new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 };

        foreach (var hive in hives)
        {
            foreach (var view in views)
            {
                try
                {
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(subKey);
                    if (key?.GetValue("InstallPath") is string path && path.Trim().Length > 0)
                    {
                        return path.Trim().TrimEnd('\\');
                    }
                }
                catch
                {
                    // 某个视图不存在是正常情况，继续试下一个
                }
            }
        }

        return null;
    }

    /// <summary>在常见位置找 &lt;盘符&gt;\...\Tencent\&lt;产品&gt;\&lt;exe&gt;。</summary>
    private static IEnumerable<string> ScanCommonLocations(ProductDefinition def)
    {
        var relative = Path.Combine("Tencent", def.Key, def.ExeName);
        var roots = new List<string>();

        void AddRoot(string? root)
        {
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root) && !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(root);
            }
        }

        // 每个固定磁盘都可能被用户装到非系统盘
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady) AddRoot(drive.RootDirectory.FullName);
            }
        }
        catch
        {
            // 忽略磁盘枚举异常
        }

        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        var bases = new[] { "", "Program Files\\", "Program Files (x86)\\", "Software\\", "Apps\\", "Programs\\" };

        foreach (var root in roots)
        {
            foreach (var b in bases)
            {
                var candidate = Path.Combine(root, b, relative);
                if (File.Exists(candidate)) yield return candidate;
            }
        }
    }

    private static string? ReadVersion(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var version = info.FileVersion ?? info.ProductVersion;
            if (!string.IsNullOrWhiteSpace(version)) return version.Trim();

            // 4.x 习惯把版本号做成子目录名（如 4.2.0.51），可作补充
            var dir = Path.GetDirectoryName(exePath);
            if (dir is not null)
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.Length > 0 && char.IsDigit(name[0]) && name.Count(c => c == '.') >= 2) return name;
                }
            }
        }
        catch
        {
            // 版本信息读不到不影响主流程
        }

        return null;
    }
}
