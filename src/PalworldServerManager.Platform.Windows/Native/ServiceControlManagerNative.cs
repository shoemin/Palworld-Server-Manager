using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PalworldServerManager.Platform.Windows.Native;

/// <summary>
/// Project-owned P/Invoke over the SCM APIs that ServiceController does not expose - service
/// creation/config and, critically, the service security descriptor (there is no BCL API for a
/// service DACL at all).
///
/// Deliberately not shelling out to sc.exe/net.exe as the production mechanism: parsing localized
/// tool output is fragile, and sc.exe's SDDL surface silently accepts a wrong descriptor.
/// </summary>
internal static class ServiceControlManagerNative
{
    internal const uint SC_MANAGER_CONNECT = 0x0001;
    internal const uint SC_MANAGER_CREATE_SERVICE = 0x0002;

    internal const uint SERVICE_QUERY_CONFIG = 0x0001;
    internal const uint SERVICE_CHANGE_CONFIG = 0x0002;
    internal const uint SERVICE_QUERY_STATUS = 0x0004;
    internal const uint SERVICE_START = 0x0010;
    internal const uint SERVICE_STOP = 0x0020;
    internal const uint SERVICE_PAUSE_CONTINUE = 0x0040;
    internal const uint DELETE = 0x00010000;
    internal const uint READ_CONTROL = 0x00020000;
    internal const uint WRITE_DAC = 0x00040000;
    internal const uint WRITE_OWNER = 0x00080000;

    internal const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;

    internal const uint SERVICE_AUTO_START = 0x00000002;
    internal const uint SERVICE_DEMAND_START = 0x00000003;
    internal const uint SERVICE_DISABLED = 0x00000004;

    internal const uint SERVICE_ERROR_NORMAL = 0x00000001;
    internal const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

    internal const int SERVICE_CONFIG_SERVICE_SID_INFO = 5;
    internal const uint SERVICE_SID_TYPE_UNRESTRICTED = 0x00000001;

    internal const int DACL_SECURITY_INFORMATION = 0x00000004;

    internal const int ERROR_INSUFFICIENT_BUFFER = 122;
    internal const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_SERVICE_EXISTS = 1073;

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateService(
        IntPtr scManager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeServiceConfig(
        IntPtr service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeServiceConfig2(IntPtr service, int infoLevel, ref SERVICE_SID_INFO info);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceObjectSecurity(
        IntPtr service,
        int securityInformation,
        byte[]? securityDescriptor,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetServiceObjectSecurity(
        IntPtr service,
        int securityInformation,
        byte[] securityDescriptor);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_SID_INFO
    {
        internal uint dwServiceSidType;
    }

    internal static Win32Exception LastError(string operation)
        => new(Marshal.GetLastWin32Error(), $"{operation} failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
}
