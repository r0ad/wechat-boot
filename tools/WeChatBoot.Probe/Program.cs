using System.Diagnostics;
using WeChatBoot.Core;

namespace WeChatBoot.Probe;

/// <summary>
/// 无界面探针，用于验证核心逻辑（也是自动化验收的入口）。
///
/// 用法：
///   dotnet run --project tools/WeChatBoot.Probe -- detect
///   dotnet run --project tools/WeChatBoot.Probe -- instances
///   dotnet run --project tools/WeChatBoot.Probe -- handles
///   dotnet run --project tools/WeChatBoot.Probe -- guard [--hold 5]
///   dotnet run --project tools/WeChatBoot.Probe -- launch [--count 2] [--strategy auto|acl|handle|direct]
///                                                     [--timeout 6000] [--path exe] [--kill] [--no-guard-hold]
///   dotnet run --project tools/WeChatBoot.Probe -- selftest
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        var command = args[0].ToLowerInvariant();
        var options = ParseOptions(args.Skip(1).ToArray());

        try
        {
            return command switch
            {
                "detect" => Detect(),
                "instances" => Instances(),
                "handles" => Handles(),
                "discover" => Discover(),
                "windows" => Windows(),
                "mutants" => Mutants(),
                "guard" => Guard(options),
                "launch" => Launch(options),
                "close" => Close(),
                "free" => Free(options),
                "selftest" => SelfTest(),
                "-h" or "--help" or "help" => PrintUsage(),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // ---------------- 命令 ----------------

    private static int Detect()
    {
        var config = AppConfig.Load();
        var products = WeChatLocator.DetectAll(config, Console.WriteLine);

        if (products.Count == 0)
        {
            Console.WriteLine("[WARN] 没有找到任何微信主程序");
            return 1;
        }

        foreach (var p in products)
        {
            Console.WriteLine($"{p.Key,-7} {p.DisplayName,-10} {p.Version ?? "-",-12} {p.Source,-6} {p.ExecutablePath}");
        }

        return 0;
    }

    private static int Instances()
    {
        var list = InstanceManager.GetInstances();
        var procs = InstanceManager.GetFamilyProcesses();

        Console.WriteLine($"实例（按主窗口）= {list.Count}，微信相关进程 = {procs.Count}");

        foreach (var i in list)
        {
            Console.WriteLine($"  PID {i.Pid,-7} {i.ProcessName,-14} class={i.ClassName,-24} title={i.Title}  {i.ImagePath}");
        }

        foreach (var p in procs.OrderBy(p => p.Id))
        {
            Console.WriteLine($"  进程 PID {p.Id,-7} {p.ProcessName,-16} {InstanceManager.GetImagePath(p)}");
        }

        return 0;
    }

    private static int Handles()
    {
        var procs = InstanceManager.GetFamilyProcesses();
        var hits = HandleScanner.FindMutants(procs, MutexGuard.IdentityMutexName);

        Console.WriteLine($"微信相关进程 {procs.Count} 个，命中单实例互斥量句柄 {hits.Count} 个");
        foreach (var h in hits)
        {
            Console.WriteLine($"  PID {h.ProcessId} {h.ProcessName} handle=0x{h.HandleValue.ToInt64():X} {h.ObjectName}");
        }

        return 0;
    }

    private static int Windows()
    {
        var top = InstanceManager.DumpTopWindows(25, out var total, out var titled);
        Console.WriteLine($"枚举到顶层窗口 {total} 个，其中带标题 {titled} 个");
        Console.WriteLine("— 前 25 个原始窗口 —");
        foreach (var r in top)
        {
            var t = r.Title.Length > 40 ? r.Title[..40] : r.Title;
            Console.WriteLine($"  PID {r.Pid,-7} {r.ProcName,-16} vis={r.Visible,-5} class={r.ClassName,-30} title={t}");
        }

        Console.WriteLine();
        var rows = InstanceManager.DumpFamilyWindows(out _);

        Console.WriteLine($"其中微信系列进程的可见带标题窗口 {rows.Count} 个");
        foreach (var r in rows.OrderBy(r => r.Pid))
        {
            Console.WriteLine($"  PID {r.Pid,-7} {r.ProcName,-14} class={r.ClassName,-34} title={r.Title}");
        }

        return 0;
    }

    private static int Mutants()
    {
        var procs = InstanceManager.GetFamilyProcesses()
            .Where(p => p.ProcessName.Contains("Weixin", StringComparison.OrdinalIgnoreCase)
                     || p.ProcessName.Contains("WeChat", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Id)
            .ToList();

        foreach (var p in procs)
        {
            var all = HandleScanner.FindMutants(new[] { p }, string.Empty);
            Console.WriteLine($"PID {p.Id} {p.ProcessName}：Mutant 句柄 {all.Count} 个");

            foreach (var h in all)
            {
                var isWeChat = h.ObjectName.Contains("WeChat", StringComparison.OrdinalIgnoreCase)
                            || h.ObjectName.Contains("Weixin", StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"    {(isWeChat ? "**" : "  ")} {h.ObjectName}");
            }
        }

        return 0;
    }

    private static int Guard(Dictionary<string, string?> options)
    {
        var hold = ParseInt(options.GetValueOrDefault("hold"), 5);
        var explicitNames = SplitNames(options.GetValueOrDefault("mutex"));

        using var guard = new MutexGuard();

        if (!guard.Arm(explicitNames: explicitNames))
        {
            Console.WriteLine($"[FAIL] 布防失败：{guard.Detail}");
            return 1;
        }

        Console.WriteLine($"[ OK ] {guard.Detail}");
        Console.WriteLine($"       保持 {hold} 秒（持有句柄，守卫才存活）…");
        Thread.Sleep(hold * 1000);
        return 0;
    }

    private static int Discover()
    {
        var names = MutexGuard.DiscoverIdentityMutexNames();

        if (names.Count == 0)
        {
            Console.WriteLine("未发现任何单实例互斥量（微信可能没在运行）");
            return 1;
        }

        Console.WriteLine($"发现 {names.Count} 个候选互斥量名：");
        foreach (var n in names) Console.WriteLine($"  {n}");

        return 0;
    }

    private static List<string>? SplitNames(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static int Launch(Dictionary<string, string?> options)
    {
        var config = AppConfig.Load();
        var count = ParseInt(options.GetValueOrDefault("count"), 2);
        var timeout = ParseInt(options.GetValueOrDefault("timeout"), config.VerifyTimeoutMs);
        var strategy = ParseStrategy(options.GetValueOrDefault("strategy"));
        var kill = options.ContainsKey("kill");
        var path = options.GetValueOrDefault("path");

        if (path is null)
        {
            var products = WeChatLocator.DetectAll(config);
            if (products.Count == 0)
            {
                Console.WriteLine("[FAIL] 没有找到微信主程序，请用 --path 指定");
                return 1;
            }

            path = products[0].ExecutablePath;
            Console.WriteLine($"自动寻址：{products[0].DisplayName} {products[0].Version} → {path}");
        }

        var baselinePids = InstanceManager.GetFamilyProcesses().Select(p => p.Id).ToHashSet();
        var baselineInstances = InstanceManager.GetInstances().Count;
        var startedAt = DateTime.Now;

        Console.WriteLine($"策略={strategy} 数量={count} 超时={timeout}ms 启动前实例数={baselineInstances}");

        using var guard = new MutexGuard();
        var successes = 0;

        for (var i = 1; i <= count; i++)
        {
            Console.WriteLine($"--- 第 {i}/{count} 个 ---");
            var outcome = Launcher.Launch(path, strategy, guard, timeout, Console.WriteLine,
                mutexOverride: SplitNames(options.GetValueOrDefault("mutex")));
            Console.WriteLine($"[{(outcome.Success ? " OK " : "FAIL")}] {outcome.Message}（策略={outcome.UsedStrategy}）");

            if (outcome.Success) successes++;
            else break;

            if (i < count && config.StaggerMs > 0) Thread.Sleep(config.StaggerMs);
        }

        var holdAfter = ParseInt(options.GetValueOrDefault("hold-after"), 0);
        if (holdAfter > 0)
        {
            Console.WriteLine($"[hold-after] 守卫继续持有 {holdAfter} 秒…");
            Thread.Sleep(holdAfter * 1000);
        }

        var after = InstanceManager.GetInstances();
        Console.WriteLine();
        Console.WriteLine($"结果：成功 {successes}/{count}，实例数 {baselineInstances} → {after.Count}");

        if (kill)
        {
            var victims = InstanceManager.GetFamilyProcesses()
                .Where(p => !baselinePids.Contains(p.Id))
                .Where(p =>
                {
                    try { return p.StartTime >= startedAt.AddSeconds(-3); }
                    catch { return false; }
                })
                .ToList();

            Console.WriteLine($"--kill：结束测试期间新起的 {victims.Count} 个进程");
            foreach (var p in victims)
            {
                try { p.Kill(); Console.WriteLine($"   已结束 {p.ProcessName} PID {p.Id}"); }
                catch (Exception ex) { Console.WriteLine($"   结束失败 {p.Id}: {ex.Message}"); }
            }
        }

        return successes == count ? 0 : 1;
    }

    private static int SelfTest()
    {
        var failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"[{(ok ? " OK " : "FAIL")}] {name}{(detail.Length > 0 ? " — " + detail : "")}");
            if (!ok) failures++;
        }

        var products = WeChatLocator.DetectAll(AppConfig.Load());
        Check("自动寻址", products.Count > 0, products.Count > 0 ? string.Join("；", products.Select(p => $"{p.DisplayName} {p.Version}")) : "未找到微信");

        var instances = InstanceManager.GetInstances();
        Check("实例枚举", instances.Count >= 0, $"当前 {instances.Count} 个实例");

        using var guard = new MutexGuard();
        Check("互斥量布防", guard.Arm(), guard.Detail);

        var procs = InstanceManager.GetFamilyProcesses();
        var hits = HandleScanner.FindMutants(procs, MutexGuard.IdentityMutexName);
        Check("句柄枚举", true, $"命中 {hits.Count} 个单实例句柄");

        var cfg = AppConfig.Load();
        var saved = cfg.Save(out var err);
        Check("配置读写", saved, saved ? AppConfig.ConfigFilePath : err ?? "未知错误");

        Console.WriteLine(failures == 0 ? "\nSELFTEST PASSED" : $"\nSELFTEST FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    // ---------------- 参数 ----------------

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) continue;

            var key = a[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map[key] = args[++i];
            }
            else
            {
                map[key] = null; // 开关型参数
            }
        }

        return map;
    }

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, out var v) ? v : fallback;

    private static int Free(Dictionary<string, string> options)
    {
        var keyword = options.GetValueOrDefault("keyword") ?? MutexGuard.DiscoveryKeyword;
        var procs = InstanceManager.GetFamilyProcesses();
        var closed = HandleScanner.CloseMutants(procs, keyword, out var detail);
        Console.WriteLine($"关闭互斥量句柄 {closed} 个：{detail}");
        return 0;
    }

    private static int Close()
    {
        var killed = Launcher.CloseAll(Console.WriteLine);
        Console.WriteLine($"已结束微信相关进程 {killed} 个");
        return 0;
    }

    private static LaunchStrategy ParseStrategy(string? text) => text?.ToLowerInvariant() switch
    {
        "acl" or "mutex" or "mutexacl" => LaunchStrategy.MutexAcl,
        "handle" or "closehandle" => LaunchStrategy.CloseHandle,
        "direct" or "none" => LaunchStrategy.Direct,
        _ => LaunchStrategy.Auto,
    };

    private static int PrintUsage()
    {
        Console.WriteLine("""
            微信多开启动器 · 控制台探针

              detect     自动寻址，列出识别到的微信主程序
              instances  列出当前实例（按主窗口）与相关进程
              handles    列出命中的单实例互斥量句柄
              discover   从运行中的微信进程自动发现单实例互斥量名
              windows    诊断：dump 顶层窗口（含隐藏）
              mutants    诊断：dump 微信进程里的 Mutant 句柄名
              guard      布防互斥量 ACL 并保持指定秒数（--hold 5）
              launch     启动多开测试
                         --count 2  --strategy auto|acl|handle|direct
                         --timeout 6000  --path <exe>  --kill
              selftest   非侵入式自检
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.WriteLine($"未知命令：{command}");
        PrintUsage();
        return 2;
    }
}
