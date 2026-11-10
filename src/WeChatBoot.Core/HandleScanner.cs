using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WeChatBoot.Core;

/// <summary>一条命中的内核对象句柄。</summary>
public sealed record MutantHandleInfo(int ProcessId, string ProcessName, IntPtr HandleValue, string ObjectName);

/// <summary>
/// 方案 B：直接关掉微信进程里那个"单实例互斥量"的句柄（兜底方案）。
///
/// 原理：
///   命名互斥量是内核对象，只要还有任何一个句柄开着，对象就存在。
///   我们枚举系统句柄表，找到微信进程里名为
///   _WeChat_App_Instance_Identity_Mutex_Name 的 Mutant 句柄，
///   用 DuplicateHandle(DUPLICATE_CLOSE_SOURCE) 把它在微信进程内关掉。
///   对象随即销毁 → 下一个微信进程 CreateMutexW 会重新创建而不是"已存在"，于是能正常启动。
///
/// 与方案 A 的区别：需要 PROCESS_DUP_HANDLE 权限（同用户普通权限即可），
/// 且必须"每开一个实例前后都要关一次"，侵入性比改 ACL 高，所以只作兜底。
/// </summary>
public static class HandleScanner
{
    /// <summary>在指定进程中查找名字包含 <paramref name="nameContains"/> 的 Mutant 句柄。</summary>
    public static IReadOnlyList<MutantHandleInfo> FindMutants(
        IReadOnlyCollection<Process> processes, string nameContains)
    {
        var result = new List<MutantHandleInfo>();
        if (processes.Count == 0) return result;

        var wanted = new Dictionary<int, Process>();
        foreach (var p in processes)
        {
            try { wanted[p.Id] = p; }
            catch { /* 进程已退出 */ }
        }

        var buffer = QueryHandleTable(out var count);
        if (buffer == IntPtr.Zero) return result;

        try
        {
            var entrySize = IntPtr.Size == 8 ? 40 : 28;
            var p = IntPtr.Add(buffer, IntPtr.Size * 2);

            for (long i = 0; i < count; i++, p = IntPtr.Add(p, entrySize))
            {
                var pid = Marshal.ReadIntPtr(p, IntPtr.Size).ToInt32();
                if (!wanted.TryGetValue(pid, out var process)) continue;

                var handleValue = Marshal.ReadIntPtr(p, IntPtr.Size * 2);

                // 只在类型是 Mutant 时才去查名字：对同步管道之类的句柄调用
                // NtQueryObject(ObjectNameInformation) 可能永久阻塞，先查类型可以完全规避。
                using var dup = DuplicateHandleSafe(process.Id, handleValue, false, out var error);
                if (dup is null) continue;

                var typeName = QueryObjectString(dup.Handle, NativeMethods.ObjectTypeInformation);
                if (typeName is null || !typeName.Equals("Mutant", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var objectName = QueryObjectString(dup.Handle, NativeMethods.ObjectNameInformation) ?? string.Empty;
                if (!objectName.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;

                result.Add(new MutantHandleInfo(pid, process.ProcessName, handleValue, objectName));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    /// <summary>关闭所有命中的 Mutant 句柄，返回成功关闭的数量。</summary>
    public static int CloseMutants(IReadOnlyCollection<Process> processes, string nameContains, out string detail)
    {
        var hits = FindMutants(processes, nameContains);
        if (hits.Count == 0)
        {
            detail = "未找到可关闭的单实例互斥量句柄";
            return 0;
        }

        var closed = 0;
        var failures = new List<string>();

        foreach (var hit in hits)
        {
            var src = NativeMethods.OpenProcess(NativeMethods.ProcessDupHandle, false, hit.ProcessId);
            if (src == IntPtr.Zero)
            {
                failures.Add($"PID {hit.ProcessId} 无法打开");
                continue;
            }

            try
            {
                if (NativeMethods.DuplicateHandle(
                        src, hit.HandleValue, NativeMethods.GetCurrentProcess(),
                        out var remote, 0, false, NativeMethods.DuplicateCloseSource))
                {
                    NativeMethods.CloseHandle(remote);
                    closed++;
                }
                else
                {
                    failures.Add($"PID {hit.ProcessId} 关闭失败({Marshal.GetLastWin32Error()})");
                }
            }
            finally
            {
                NativeMethods.CloseHandle(src);
            }
        }

        detail = closed > 0
            ? $"已关闭 {closed} 个单实例互斥量句柄"
            : $"关闭失败：{string.Join("，", failures)}";

        return closed;
    }

    // ---------------- 内部实现 ----------------

    private static SafeHandleWrapper? DuplicateHandleSafe(int pid, IntPtr handleValue, bool closeSource, out string error)
    {
        error = string.Empty;
        var src = NativeMethods.OpenProcess(NativeMethods.ProcessDupHandle, false, pid);
        if (src == IntPtr.Zero)
        {
            error = $"OpenProcess({pid}) 失败，错误码 {Marshal.GetLastWin32Error()}";
            return null;
        }

        try
        {
            var ok = NativeMethods.DuplicateHandle(
                src, handleValue, NativeMethods.GetCurrentProcess(),
                out var dup, 0, false,
                NativeMethods.DuplicateSameAccess | (closeSource ? NativeMethods.DuplicateCloseSource : 0));

            if (!ok)
            {
                error = $"DuplicateHandle({pid}) 失败，错误码 {Marshal.GetLastWin32Error()}";
                return null;
            }

            return new SafeHandleWrapper(dup);
        }
        finally
        {
            NativeMethods.CloseHandle(src);
        }
    }

    /// <summary>NtQueryObject 取字符串（对象名 / 类型名）。</summary>
    private static string? QueryObjectString(IntPtr handle, int infoClass)
    {
        var size = 1024;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = NativeMethods.NtQueryObject(handle, infoClass, buffer, size, out var needed);
            if (status == NativeMethods.StatusInfoLengthMismatch && needed > size)
            {
                Marshal.FreeHGlobal(buffer);
                size = needed + 16;
                buffer = Marshal.AllocHGlobal(size);
                status = NativeMethods.NtQueryObject(handle, infoClass, buffer, size, out _);
            }

            if (status != NativeMethods.StatusSuccess) return null;

            // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }
            // x64 下 Buffer 位于偏移 8（结构体对齐），x86 下偏移 4。
            var length = (ushort)Marshal.ReadInt16(buffer, 0);
            if (length == 0) return null;
            var strPtr = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return strPtr == IntPtr.Zero ? null : Marshal.PtrToStringUni(strPtr, length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr QueryHandleTable(out long handleCount)
    {
        handleCount = 0;
        var length = 1 << 20;
        var buffer = Marshal.AllocHGlobal(length);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var status = NativeMethods.NtQuerySystemInformation(
                NativeMethods.SystemExtendedHandleInformation, buffer, length, out var needed);

            if (status == NativeMethods.StatusInfoLengthMismatch)
            {
                Marshal.FreeHGlobal(buffer);
                length = Math.Max(needed + (1 << 16), length * 2);
                buffer = Marshal.AllocHGlobal(length);
                continue;
            }

            if (status != NativeMethods.StatusSuccess)
            {
                Marshal.FreeHGlobal(buffer);
                return IntPtr.Zero;
            }

            // SYSTEM_HANDLE_INFORMATION_EX { ULONG_PTR NumberOfHandles; ULONG_PTR Reserved; ... }
            handleCount = Marshal.ReadIntPtr(buffer).ToInt64();
            return buffer;
        }

        Marshal.FreeHGlobal(buffer);
        return IntPtr.Zero;
    }

    /// <summary>包一层，保证 DuplicateHandle 出来的句柄一定释放。</summary>
    private sealed class SafeHandleWrapper : IDisposable
    {
        public IntPtr Handle { get; }

        public SafeHandleWrapper(IntPtr handle) => Handle = handle;

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) NativeMethods.CloseHandle(Handle);
        }
    }
}
