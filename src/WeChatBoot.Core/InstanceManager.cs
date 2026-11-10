using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WeChatBoot.Core;

/// <summary>一个正在运行的微信实例（以"主窗口"为准，而不是进程数）。</summary>
public sealed record WeChatInstance(
    int Pid,
    string ProcessName,
    string? ImagePath,
    string Title,
    string ClassName,
    DateTime? StartTime);

/// <summary>
/// 实例枚举。
///
/// 为什么按窗口数而不是进程数：微信 4.x 一个实例会拉起 5 个以上进程
/// （Weixin.exe 主进程 + 多个 WeChatAppEx.exe + 子进程），数进程会严重高估；
/// 而每个实例必然有自己的主窗口（4.x 是 Qt 窗口类，3.x 是 WeChatMainWndForPC）。
/// </summary>
public static class InstanceManager
{
    /// <summary>承载"一个登录实例"的进程名。</summary>
    public static readonly string[] MainProcessNames = { "Weixin", "WeChat" };

    /// <summary>"全部关闭"时要处理的进程名（不含更新程序等无关进程）。</summary>
    public static readonly string[] FamilyProcessNames = { "Weixin", "WeChat", "WeChatAppEx", "WechatAppLauncher" };

    /// <summary>已知的主窗口类名（3.x 固定；4.x 为 Qt 窗口，按后缀匹配）。</summary>
    private static readonly string[] KnownWindowClasses = { "WeChatMainWndForPC", "WeChatLoginWndForPC" };

    /// <summary>枚举当前所有微信实例（按主窗口去重，按 PID 排序）。</summary>
    public static IReadOnlyList<WeChatInstance> GetInstances()
    {
        var windows = new List<(int Pid, string Proc, string Class, string Title)>();
        var procNames = new Dictionary<int, string>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            var title = GetWindowText(hWnd);
            if (title.Length == 0) return true;

            var cls = GetClassName(hWnd);
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);

            if (!procNames.TryGetValue((int)pid, out var procName))
            {
                procName = TryGetProcessName((int)pid) ?? string.Empty;
                procNames[(int)pid] = procName;
            }

            if (!LooksLikeMainWindow(procName, cls, title)) return true;

            windows.Add(((int)pid, procName, cls, title));
            return true;
        }, IntPtr.Zero);

        var result = new List<WeChatInstance>();
        foreach (var group in windows.GroupBy(w => w.Pid))
        {
            // 同一个实例可能有多个窗口（主窗口 + 隐藏副本），取标题最"正式"的那个
            var best = group.OrderByDescending(w => w.Title is "微信" or "Weixin" or "WeChat").First();
            var image = TryGetImagePathByPid(group.Key);
            DateTime? start = null;
            try
            {
                using var p = Process.GetProcessById(group.Key);
                start = p.StartTime;
            }
            catch
            {
                // 进程恰好退出
            }

            result.Add(new WeChatInstance(group.Key, best.Proc, image, best.Title, best.Class, start));
        }

        return result.OrderBy(i => i.Pid).ToList();
    }

    /// <summary>诊断用：列出前 N 个顶层窗口的原始信息（不做任何过滤）。</summary>
    public static IReadOnlyList<(int Pid, string ProcName, string ClassName, string Title, bool Visible)> DumpTopWindows(
        int max, out int totalEnumerated, out int withTitle)
    {
        var total = 0;
        var titled = 0;
        var rows = new List<(int, string, string, string, bool)>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            total++;

            var title = GetWindowText(hWnd);
            var cls = GetClassName(hWnd);
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);

            if (title.Length > 0) titled++;

            if (rows.Count < max)
            {
                rows.Add(((int)pid, TryGetProcessName((int)pid) ?? "?", cls, title,
                    NativeMethods.IsWindowVisible(hWnd)));
            }

            return true;
        }, IntPtr.Zero);

        totalEnumerated = total;
        withTitle = titled;
        return rows;
    }

    /// <summary>诊断用：列出微信系列进程的所有可见顶层窗口（不做主窗口过滤）。</summary>
    public static IReadOnlyList<(int Pid, string ProcName, string ClassName, string Title)> DumpFamilyWindows(
        out int totalEnumerated)
    {
        var total = 0;
        var rows = new List<(int, string, string, string)>();
        var names = new Dictionary<int, string>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            total++;

            var title = GetWindowText(hWnd);
            if (title.Length == 0) return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);

            if (!names.TryGetValue((int)pid, out var procName))
            {
                procName = TryGetProcessName((int)pid) ?? string.Empty;
                names[(int)pid] = procName;
            }

            if (!FamilyProcessNames.Contains(procName, StringComparer.OrdinalIgnoreCase)) return true;

            rows.Add(((int)pid, procName, GetClassName(hWnd), title));
            return true;
        }, IntPtr.Zero);

        totalEnumerated = total;
        return rows;
    }

    /// <summary>当前所有微信相关进程。</summary>
    public static IReadOnlyList<Process> GetFamilyProcesses()
    {
        var list = new List<Process>();
        foreach (var name in FamilyProcessNames)
        {
            try { list.AddRange(Process.GetProcessesByName(name)); }
            catch { /* 忽略 */ }
        }

        return list;
    }

    /// <summary>取进程完整路径（受保护进程会失败，返回 null）。</summary>
    public static string? GetImagePath(Process process)
    {
        try
        {
            var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, process.Id);
            if (handle == IntPtr.Zero) return null;

            try
            {
                var sb = new StringBuilder(1024);
                var size = sb.Capacity;
                return NativeMethods.QueryFullProcessImageNameW(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetImagePathByPid(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return GetImagePath(p);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeMainWindow(string processName, string className, string title)
    {
        if (!MainProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)) return false;

        // 4.x 用 Qt：主窗口/登录窗口的类名形如 Qt51514QWindowIcon，Qt 版本会变，用后缀匹配更稳
        if (className.EndsWith("QWindowIcon", StringComparison.OrdinalIgnoreCase)) return true;

        // 3.x：类名固定
        if (KnownWindowClasses.Contains(className, StringComparer.OrdinalIgnoreCase)) return true;

        // 兜底按标题认，但必须排掉对话框（#32770）。
        // 否则"微信已在运行"这类提示框会被当成一个实例，导致假成功。
        var looksLikeTitle = title.Equals("微信", StringComparison.Ordinal) ||
                             title.Equals("Weixin", StringComparison.OrdinalIgnoreCase) ||
                             title.Equals("WeChat", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeTitle) return false;

        return !className.Equals("#32770", StringComparison.OrdinalIgnoreCase)
            && !className.Contains("MessageBox", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowText(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        NativeMethods.GetWindowTextW(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassNameW(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
