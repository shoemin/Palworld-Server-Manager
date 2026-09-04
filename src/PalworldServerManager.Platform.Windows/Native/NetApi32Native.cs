using System.Runtime.InteropServices;

namespace PalworldServerManager.Platform.Windows.Native;

/// <summary>
/// Project-owned P/Invoke over the local-group NetApi32 functions used to PROVISION the Host
/// activation group (SS2). Deliberately narrow: only existence-check and create are exposed here
/// - there is no membership function anywhere in this file, so "no member-add operation occurs"
/// is a structural property of the surface, not just current behavior.
/// </summary>
internal static class NetApi32Native
{
    internal const int NERR_Success = 0;
    internal const int NERR_GroupNotFound = 2220;
    internal const int NERR_GroupExists = 2223;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LOCALGROUP_INFO_0
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        internal string lgrpi0_name;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetLocalGroupGetInfo(string? servername, string groupname, int level, out IntPtr bufptr);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetLocalGroupAdd(string? servername, int level, ref LOCALGROUP_INFO_0 buf, out int parm_err);

    [DllImport("netapi32.dll")]
    internal static extern int NetApiBufferFree(IntPtr buffer);
}
