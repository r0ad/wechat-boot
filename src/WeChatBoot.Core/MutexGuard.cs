using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WeChatBoot.Core;

/// <summary>
/// 方案 A：互斥量 ACL 守卫（推荐，无需管理员权限）。
///
/// 原理：
///   微信启动时执行 CreateMutexW(..., "&lt;单实例互斥量名&gt;")，
///   若返回 ERROR_ALREADY_EXISTS 就认为"已有实例"，激活旧窗口后自杀。
///   我们先一步创建同名互斥量，并把它的 DACL 改成"拒绝 Everyone 的 MUTEX_ALL_ACCESS"。
///   之后微信再调 CreateMutexW 会失败并返回 ERROR_ACCESS_DENIED（而不是 ALREADY_EXISTS），
///   它的"已存在"判断落空，于是正常启动 —— 想要几个就启动几个。
///
/// 关于互斥量名（实测，本机 4.2.0.51）：
///   3.x 时代是 _WeChat_App_Instance_Identity_Mutex_Name；
///   4.x 改成了 XWeChat_XWEB_Instance_Identity_Mutex_Name（名字里的 WeChat/Weixin 混用）。
///   为了不把某个版本的字符串写死，这里同时采用两条路：
///     1) 内置候选名单（Legacy + 4.x）；
///     2) 从正在运行的微信进程句柄表里自动发现名字含 "Instance_Identity_Mutex" 的互斥量。
///   发现出来的名字优先，内置名单兜底，用户在配置里还能再手动追加。
///
/// 生命周期：
///   命名互斥量在最后一个句柄关闭时销毁，所以本类必须一直持有句柄（守卫常驻）。
///   只要启动器开着，守卫就在；关掉启动器后，微信自己仍持有该句柄，防线会保持到微信全部退出。
/// </summary>
public sealed class MutexGuard : IDisposable
{
    /// <summary>3.x 的单实例互斥量名。</summary>
    public const string LegacyIdentityMutexName = "_WeChat_App_Instance_Identity_Mutex_Name";

    /// <summary>4.x 实测到的单实例互斥量名。</summary>
    public const string XWeChatIdentityMutexName = "XWeChat_XWEB_Instance_Identity_Mutex_Name";

    /// <summary>兼容旧代码的别名（= 3.x 名字）。</summary>
    public const string IdentityMutexName = LegacyIdentityMutexName;

    /// <summary>自动发现时用的名字特征。</summary>
    public const string DiscoveryKeyword = "Instance_Identity_Mutex";

    /// <summary>内置候选名单：即使一个微信都没在跑也能直接布防。</summary>
    public static readonly string[] DefaultMutexNames =
    {
        XWeChatIdentityMutexName,
        LegacyIdentityMutexName,
        "_WeChat_App_Instance_Identity_Mutex_Name_Global",
    };

    private readonly List<(string Name, IntPtr Handle)> _held = new();

    /// <summary>不是由本进程创建、但已被（本程序此前运行或其它工具）锁死的名字。</summary>
    private readonly List<string> _externallyArmed = new();
    private bool _disposed;

    /// <summary>是否已成功布防。</summary>
    public bool Armed { get; private set; }

    /// <summary>最近一次布防的说明（给界面/日志用）。</summary>
    public string Detail { get; private set; } = "未布防";

    /// <summary>从运行中的微信进程里自动发现的名字。</summary>
    public IReadOnlyList<string> DiscoveredNames { get; private set; } = Array.Empty<string>();

    /// <summary>实际布防的互斥量名（含"已被锁死、无需本进程持有句柄"的情况）。</summary>
    public IReadOnlyList<string> ArmedNames =>
        _held.Select(x => x.Name).Concat(_externallyArmed).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// 从正在运行的微信进程里，自动发现单实例互斥量名。
    /// 找不到（微信没运行 / 权限不足）时返回空列表，不抛异常。
    /// </summary>
    public static IReadOnlyList<string> DiscoverIdentityMutexNames(IReadOnlyCollection<Process>? processes = null)
    {
        try
        {
            processes ??= InstanceManager.GetFamilyProcesses();

            var ips = processes
                .Where(p => InstanceManager.MainProcessNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (ips.Count == 0) return Array.Empty<string>();

            var names = HandleScanner.FindMutants(ips, DiscoveryKeyword)
                .Select(h => h.ObjectName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                // 句柄表里带的是 \Sessions\1\BaseNamedObjects\ 前缀，CreateMutexW 只认名字部分
                .Select(StripNamespace)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>去掉对象路径前缀，只留互斥量名。</summary>
    internal static string StripNamespace(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        var idx = name.LastIndexOf('\\');
        if (idx < 0 || idx == name.Length - 1) return name;

        var tail = name[(idx + 1)..];

        // \Sessions\1\BaseNamedObjects\ 这种前缀后面才是真名；但 \BaseNamedObjects\A\B 里的 A\B 可能本身就是名字，
        // 简单起见取最后一段（微信的互斥量名都不含反斜杠）。
        return tail;
    }

    /// <summary>
    /// 布防（幂等）。已布防时直接返回成功，不重复创建。
    /// </summary>
    /// <param name="extraNames">用户手动追加的互斥量名（高级配置）。</param>
    /// <param name="explicitNames">指定名单：给了就只布防这些名字（诊断/调试用）。</param>
    /// <param name="autoDiscover">是否从运行中的微信进程自动发现互斥量名。</param>
    public bool Arm(
        IEnumerable<string>? extraNames = null,
        IEnumerable<string>? explicitNames = null,
        bool autoDiscover = true)
    {
        if (Armed)
        {
            return true;
        }

        if (autoDiscover)
        {
            DiscoveredNames = DiscoverIdentityMutexNames();
        }

        var names = new List<string>();

        void Add(string? n)
        {
            if (string.IsNullOrWhiteSpace(n)) return;
            n = n.Trim();
            if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
        }

        if (explicitNames is not null)
        {
            foreach (var n in explicitNames) Add(n);
        }
        else
        {
            // 自动发现的优先（更贴近当前版本），内置名单兜底
            foreach (var n in DiscoveredNames) Add(n);
            foreach (var n in DefaultMutexNames) Add(n);
            if (extraNames is not null)
            {
                foreach (var n in extraNames) Add(n);
            }
        }

        if (names.Count == 0)
        {
            Detail = "没有可布防的互斥量名";
            return false;
        }

        var errors = new List<string>();
        foreach (var name in names)
        {
            if (ArmOne(name, out var error))
            {
                Armed = true;
            }
            else if (error is not null)
            {
                errors.Add(error);
            }
        }

        if (Armed)
        {
            var discovered = DiscoveredNames.Count > 0
                ? $"，自动发现 {string.Join("、", DiscoveredNames)}"
                : string.Empty;
            Detail = $"已布防：{string.Join("、", ArmedNames)}{discovered}";
        }
        else
        {
            Detail = errors.Count > 0 ? string.Join("；", errors) : "布防失败";
        }

        return Armed;
    }

    private bool ArmOne(string name, out string? error)
    {
        error = null;

        var handle = NativeMethods.CreateMutexW(IntPtr.Zero, false, name);
        var lastError = Marshal.GetLastWin32Error();

        if (handle == IntPtr.Zero)
        {
            if (lastError == NativeMethods.ErrorAccessDenied)
            {
                // 已经被"拒绝访问"占住了（本程序上一次运行的守卫仍在，或其它多开工具的守卫）。
                // 进程拿不到句柄，但对象确实存在且谁也打不开 —— 对微信来说效果是一样的。
                // 常见来源：本程序上一次运行的守卫；只要还有微信实例持有该互斥量，锁就一直是生效的。
                if (!_externallyArmed.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    _externallyArmed.Add(name);
                }

                error = $"{name} 已被锁定（拒绝访问），沿用现有布防";
                return true;
            }

            error = $"{name} 创建失败，错误码 {lastError}";
            return false;
        }

        if (!DenyEveryone(handle))
        {
            var code = Marshal.GetLastWin32Error();
            NativeMethods.CloseHandle(handle);
            error = $"{name} 设置拒绝 ACL 失败，错误码 {code}";
            return false;
        }

        _held.Add((name, handle));

        // 自检：此刻我们自己再开一次同名互斥量也必须被拒绝，否则说明 ACL 没生效。
        var probe = NativeMethods.CreateMutexW(IntPtr.Zero, false, name);
        if (probe != IntPtr.Zero)
        {
            var probeError = Marshal.GetLastWin32Error();
            NativeMethods.CloseHandle(probe);
            error = $"{name} ACL 未生效（自查仍可打开，错误码 {probeError}）";
            return false;
        }

        return true;
    }

    /// <summary>把互斥量的 DACL 替换为"拒绝 Everyone 全部权限"。</summary>
    private static bool DenyEveryone(IntPtr handle)
    {
        IntPtr sid = IntPtr.Zero;
        IntPtr acl = IntPtr.Zero;

        try
        {
            // S-1-1-0 = Everyone，省掉 AllocateAndInitializeSid 的一串参数。
            if (!NativeMethods.ConvertStringSidToSidW("S-1-1-0", out sid))
            {
                return false;
            }

            var sidLength = NativeMethods.GetLengthSid(sid);
            // ACL 头 + 一个 ACCESS_DENIED_ACE + SID，留点余量。
            var aclSize = 64 + sidLength;
            acl = Marshal.AllocHGlobal(aclSize);

            if (!NativeMethods.InitializeAcl(acl, aclSize, NativeMethods.AclRevision))
            {
                return false;
            }

            if (!NativeMethods.AddAccessDeniedAce(acl, NativeMethods.AclRevision, NativeMethods.MutexAllAccess, sid))
            {
                return false;
            }

            var result = NativeMethods.SetSecurityInfo(
                handle,
                NativeMethods.SeKernelObject,
                NativeMethods.DaclSecurityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                acl,
                IntPtr.Zero);

            return result == 0;
        }
        finally
        {
            if (acl != IntPtr.Zero) Marshal.FreeHGlobal(acl);
            if (sid != IntPtr.Zero) NativeMethods.LocalFree(sid);
        }
    }

    /// <summary>撤销守卫：关闭自己持有的句柄（微信自身的句柄不受影响）。</summary>
    public void Disarm()
    {
        foreach (var (_, h) in _held)
        {
            if (h != IntPtr.Zero) NativeMethods.CloseHandle(h);
        }

        _held.Clear();
        _externallyArmed.Clear();
        Armed = false;
        Detail = "已解除布防";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disarm();
    }
}
