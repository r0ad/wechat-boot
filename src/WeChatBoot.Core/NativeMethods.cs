using System.Runtime.InteropServices;
using System.Text;

namespace WeChatBoot.Core;

/// <summary>
/// 项目用到的全部 Win32 / NT 原生调用。刻意不引入任何 NuGet 包，
/// 这样离线、单文件、任何 Windows 上的 .NET 都能直接编译。
/// </summary>
internal static class NativeMethods
{
    // ---------------- 错误码 ----------------
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorAlreadyExists = 183;
    internal const int ErrorInsufficientBuffer = 122;

    // ---------------- 对象 / 权限 ----------------
    /// <summary>互斥量全部权限（0x1F0001）。</summary>
    internal const uint MutexAllAccess = 0x001F0001;

    /// <summary>SE_KERNEL_OBJECT：SetSecurityInfo 的对象类型。</summary>
    internal const uint SeKernelObject = 6;

    /// <summary>DACL_SECURITY_INFORMATION：只改 DACL。</summary>
    internal const uint DaclSecurityInformation = 0x00000004;

    internal const uint AclRevision = 2;

    // ---------------- 进程 / 句柄 ----------------
    internal const uint ProcessDupHandle = 0x0040;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint DuplicateCloseSource = 0x00000001;
    internal const uint DuplicateSameAccess = 0x00000002;

    // ---------------- NT 查询 ----------------
    /// <summary>SystemExtendedHandleInformation = 64，64 位下条目含完整 PID 字段。</summary>
    internal const int SystemExtendedHandleInformation = 64;
    internal const uint StatusSuccess = 0;
    internal const uint StatusInfoLengthMismatch = 0xC0000004;
    internal const int ObjectNameInformation = 1;
    internal const int ObjectTypeInformation = 2;

    // ================= kernel32 =================

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateMutexW(IntPtr lpMutexAttributes, bool bInitialOwner, string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenMutexW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    // ================= ntdll =================

    [DllImport("ntdll.dll")]
    internal static extern uint NtQuerySystemInformation(
        int SystemInformationClass, IntPtr SystemInformation,
        int SystemInformationLength, out int ReturnLength);

    [DllImport("ntdll.dll")]
    internal static extern uint NtQueryObject(
        IntPtr Handle, int ObjectInformationClass, IntPtr ObjectInformation,
        int ObjectInformationLength, out int ReturnLength);

    // ================= advapi32 =================

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool ConvertStringSidToSidW(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern int GetLengthSid(IntPtr pSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool InitializeAcl(IntPtr pAcl, int nAclLength, uint dwAclRevision);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool AddAccessDeniedAce(IntPtr pAcl, uint dwAceRevision, uint accessMask, IntPtr pSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern uint SetSecurityInfo(
        IntPtr handle, uint objectType, uint securityInfo,
        IntPtr psidOwner, IntPtr psidGroup, IntPtr pDacl, IntPtr pSacl);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    // ================= user32（窗口枚举） =================

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
