using System.Diagnostics;

namespace WeChatBoot.Core;

/// <summary>多开策略。</summary>
public enum LaunchStrategy
{
    /// <summary>自动：先 ACL 守卫，失败再退到关句柄，最后直接启动。</summary>
    Auto = 0,

    /// <summary>互斥量 ACL 守卫（推荐）。</summary>
    MutexAcl = 1,

    /// <summary>关闭微信进程内的单实例互斥量句柄。</summary>
    CloseHandle = 2,

    /// <summary>不做任何处理，直接启动（用于对照/诊断）。</summary>
    Direct = 3,
}

/// <summary>一次启动的结果。</summary>
public sealed class LaunchOutcome
{
    public bool Success { get; init; }
    public LaunchStrategy UsedStrategy { get; init; }
    public int InstanceDelta { get; init; }
    public int? Pid { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 启动器：布防/兜底 → 拉起进程 → 用"同产品实例窗口数是否真的多出一个"验证结果。
///
/// 为什么验证要这么小心：
///   互斥量名用错时，微信不会报错，只会把已有的最小化窗口还原一下就退出。
///   如果只看"我启动的进程还活着"或者"窗口总数变了"，就会把失败当成成功。
///   所以这里的判据是：出现了"启动前不存在、且稳定存活"的同类实例主窗口。
/// </summary>
public static class Launcher
{
    /// <summary>新实例需要稳定存活多久才认定多开成功（毫秒）。</summary>
    // 单实例折叠的延迟实测约 2-3 秒（新窗口先出现、随后进程退出），
    // 所以稳定性窗口必须大于这个值，否则会给出"多开成功"的假阳性。
    private const int StabilityMs = 5000;

    /// <summary>
    /// 启动一个微信实例并校验结果。
    /// </summary>
    /// <param name="exePath">微信主程序路径。</param>
    /// <param name="strategy">策略。</param>
    /// <param name="guard">ACL 守卫（由调用方持有，保证生命周期覆盖整个启动过程）。</param>
    /// <param name="verifyTimeoutMs">等待主窗口出现的超时。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="extraMutexNames">用户手动追加的互斥量名（来自配置）。</param>
    /// <param name="mutexOverride">指定只布防这些互斥量名（诊断用，正常使用留空）。</param>
    public static LaunchOutcome Launch(
        string exePath,
        LaunchStrategy strategy,
        MutexGuard guard,
        int verifyTimeoutMs = 6000,
        Action<string>? log = null,
        IEnumerable<string>? extraMutexNames = null,
        IEnumerable<string>? mutexOverride = null)
    {
        if (!File.Exists(exePath))
        {
            return new LaunchOutcome { Success = false, UsedStrategy = strategy, Message = $"找不到微信主程序：{exePath}" };
        }

        var product = ProductName(exePath);
        var baseline = InstancesOf(exePath);
        var before = baseline.Count;
        log?.Invoke($"[{product}] 启动前已有 {before} 个实例");

        switch (strategy)
        {
            case LaunchStrategy.MutexAcl:
                return RunWithAcl(exePath, guard, verifyTimeoutMs, before, baseline, log, extraMutexNames, mutexOverride);

            case LaunchStrategy.CloseHandle:
                return RunWithHandleClose(exePath, verifyTimeoutMs, before, baseline, log);

            case LaunchStrategy.Direct:
                return SpawnAndVerify(exePath, LaunchStrategy.Direct, verifyTimeoutMs, before, baseline, log);

            default:
                // 自动：ACL → 关句柄 → 直接启动
                var acl = RunWithAcl(exePath, guard, verifyTimeoutMs, before, baseline, log, extraMutexNames, mutexOverride);
                if (acl.Success) return acl;
                log?.Invoke("ACL 方式未生效，改用关闭句柄方式重试…");

                var handle = RunWithHandleClose(exePath, verifyTimeoutMs, before, baseline, log);
                if (handle.Success) return handle;
                log?.Invoke("关闭句柄方式未生效，直接启动（可能被单实例折叠）…");

                return SpawnAndVerify(exePath, LaunchStrategy.Direct, verifyTimeoutMs, before, baseline, log);
        }
    }

    private static LaunchOutcome RunWithAcl(
        string exePath,
        MutexGuard guard,
        int timeoutMs,
        int before,
        IReadOnlyList<WeChatInstance> baseline,
        Action<string>? log,
        IEnumerable<string>? extraMutexNames,
        IEnumerable<string>? mutexOverride)
    {
        if (guard.Arm(extraMutexNames, mutexOverride))
        {
            log?.Invoke(guard.Detail);
        }
        else
        {
            log?.Invoke($"布防失败：{guard.Detail}");
        }

        return SpawnAndVerify(exePath, LaunchStrategy.MutexAcl, timeoutMs, before, baseline, log);
    }

    private static LaunchOutcome RunWithHandleClose(
        string exePath,
        int timeoutMs,
        int before,
        IReadOnlyList<WeChatInstance> baseline,
        Action<string>? log)
    {
        var processes = InstanceManager.GetFamilyProcesses()
            .Where(p => InstanceManager.MainProcessNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var closed = HandleScanner.CloseMutants(processes, MutexGuard.DiscoveryKeyword, out var detail);
        log?.Invoke(closed > 0 ? detail : $"句柄清理：{detail}");

        return SpawnAndVerify(exePath, LaunchStrategy.CloseHandle, timeoutMs, before, baseline, log);
    }

    /// <summary>拉起进程并轮询"是否多出一个稳定存活的同类实例"，以此判定是否真的多开成功。</summary>
    private static LaunchOutcome SpawnAndVerify(
        string exePath,
        LaunchStrategy strategy,
        int timeoutMs,
        int before,
        IReadOnlyList<WeChatInstance> baseline,
        Action<string>? log)
    {
        var product = ProductName(exePath);
        var baselinePids = baseline.Select(i => i.Pid).ToHashSet();

        Process? process = null;
        try
        {
            // UseShellExecute = true 是必须的，不能改成 false：
            //   1) false 时子进程会继承我们的 stdout/stderr 句柄，微信写日志到管道失败会闪退
            //      （实测 3.9.12.55 每次启动十几秒后就自己退出），调用方 shell 还会一直等 EOF；
            //   2) true 走 ShellExecuteEx，不继承句柄，也与资源管理器双击的行为一致。
            // 代价是拿不到可用的进程句柄（Id/HasExited 会抛异常），所以下面改用实例列表来判定。
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            };

            process = Process.Start(psi);

            log?.Invoke($"已发出启动请求（{Path.GetFileName(exePath)}），等待主窗口…");
        }
        catch (Exception ex)
        {
            return new LaunchOutcome
            {
                Success = false,
                UsedStrategy = strategy,
                Message = $"启动失败：{ex.Message}",
            };
        }

        var sw = Stopwatch.StartNew();
        int? candidatePid = null;
        long candidateSince = 0;
        long candidateLastSeen = 0;
        var lastCount = before;

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(250);

            var current = InstancesOf(exePath);
            lastCount = current.Count;

            var fresh = current.FirstOrDefault(i => !baselinePids.Contains(i.Pid));

            if (fresh is null)
            {
                // 没有新实例：如果启动的进程已经死了，多半是被单实例机制折叠了，不用再等
                if (candidatePid is null && HasExited(process) == true)
                {
                    break;
                }

                // 新实例出现过又消失了 —— 这就是“还原已有窗口后自杀”的折叠路径
                if (candidatePid is not null && sw.ElapsedMilliseconds - candidateLastSeen > 2500)
                {
                    break;
                }

                continue;
            }

            if (candidatePid != fresh.Pid)
            {
                candidatePid = fresh.Pid;
                candidateSince = sw.ElapsedMilliseconds;
                candidateLastSeen = candidateSince;
                continue;
            }

            candidateLastSeen = sw.ElapsedMilliseconds;

            // 同一个新实例连续存活够久，才算真的多开成功
            if (sw.ElapsedMilliseconds - candidateSince >= StabilityMs && IsAlive(fresh.Pid))
            {
                return new LaunchOutcome
                {
                    Success = true,
                    UsedStrategy = strategy,
                    InstanceDelta = current.Count - before,
                    Pid = fresh.Pid,
                    Message = $"多开成功：{product} 实例 {before} → {current.Count}（新实例 PID {fresh.Pid}）",
                };
            }
        }

        var exitedEarly = HasExited(process) == true;
        var reason = candidatePid is not null
            ? $"新实例 PID {candidatePid} 未能稳定存活（疑似登录窗口一闪而过）"
            : exitedEarly
                ? "新进程已自行退出，判定为被单实例机制折叠"
                : "超时未出现新实例窗口";

        return new LaunchOutcome
        {
            Success = false,
            UsedStrategy = strategy,
            InstanceDelta = Math.Max(0, lastCount - before),
            Pid = candidatePid ?? SafePid(process),
            Message = reason,
        };
    }

    /// <summary>某个产品的执行文件名（Weixin.exe → Weixin）。</summary>
    private static string ProductName(string exePath) => Path.GetFileNameWithoutExtension(exePath);

    /// <summary>
    /// 当前属于"同一个安装"的实例。
    /// 只按进程名匹配是不够的：本机可能同时装了 3.x / 4.x，甚至两个不同目录的 4.x，
    /// 它们进程名都叫 Weixin.exe，混在一起会导致实例数永远对不上。
    /// </summary>
    /// <summary>
    /// 统计某个微信安装（按可执行文件的目录区分）当前有多少个实例。
    /// 界面上「已经开了几个 / 还要开几个」都基于这个方法，避免把两个不同版本的
    /// Weixin.exe 混成一个产品来数。
    /// </summary>
    public static List<WeChatInstance> InstancesOf(string exePath)
    {
        var product = ProductName(exePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));

        return InstanceManager.GetInstances()
            .Where(i => string.Equals(i.ProcessName, product, StringComparison.OrdinalIgnoreCase))
            .Where(i => MatchesDirectory(i, dir))
            .ToList();
    }

    private static bool MatchesDirectory(WeChatInstance instance, string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return true;
        if (string.IsNullOrEmpty(instance.ImagePath)) return true;

        var instanceDir = Path.GetDirectoryName(instance.ImagePath);
        return string.Equals(
            instanceDir?.TrimEnd('\\', '/'),
            dir.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>进程是否已退出；拿不到句柄（ShellExecute 启动）时返回 null 表示未知。</summary>
    private static bool? HasExited(Process? p)
    {
        try { return p?.HasExited ?? false; }
        catch { return null; }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static int? SafePid(Process? p)
    {
        try { return p?.Id; }
        catch { return null; }
    }

    /// <summary>结束所有微信相关进程，返回结束的数量。</summary>
    public static int CloseAll(Action<string>? log = null)
    {
        var killed = 0;
        foreach (var p in InstanceManager.GetFamilyProcesses())
        {
            try
            {
                if (p.HasExited) continue;
                p.Kill(entireProcessTree: false);
                killed++;
                log?.Invoke($"已结束 {p.ProcessName}.exe (PID {p.Id})");
            }
            catch (Exception ex)
            {
                log?.Invoke($"结束 {p.ProcessName}.exe (PID {p.Id}) 失败：{ex.Message}");
            }
        }

        return killed;
    }
}
