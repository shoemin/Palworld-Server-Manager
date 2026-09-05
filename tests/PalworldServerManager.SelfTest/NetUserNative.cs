using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PalworldServerManager.SelfTest;

/// <summary>
/// TEST-ONLY native interop for creating/deleting the privileged harness's temporary non-admin
/// local users directly via NetApi32, bypassing PowerShell's New-LocalUser /
/// ConvertTo-SecureString pipeline entirely.
///
/// This exists because of a real GitHub Actions windows-latest runner defect, confirmed by two
/// independent PR CI runs: Windows PowerShell 5.1's own built-in Microsoft.PowerShell.Security
/// module fails to autoload ("ConvertTo-SecureString : ... CouldNotAutoloadMatchingModule")
/// regardless of how the child process's environment is constructed - one run used a custom
/// environment variable, the other used only stdin redirection with an untouched, verbatim
/// inherited environment, and both failed identically. The failure is specific to invoking
/// ConvertTo-SecureString at all; every other RunPowerShell call in the same harness run (which
/// never touches that module) succeeds. NetUserAdd takes a plain Unicode password directly, with
/// no SecureString/module dependency, sidestepping the defect entirely.
///
/// As a side benefit, the temporary user's password now never touches ANY child process's
/// command line, environment, or stdin - it exists only in this process's own memory and is
/// marshalled directly into the single NetApi32 call that needs it (SEC-001).
///
/// Never shipped in any production project.
/// </summary>
internal static class NetUserNative
{
    private const int USER_PRIV_USER = 1;
    private const uint UF_SCRIPT = 0x0001;
    private const uint UF_DONT_EXPIRE_PASSWD = 0x10000;
    private const int NERR_UserNotFound = 2221;

    internal static void CreateNonAdminUser(string userName, string password)
    {
        var info = new USER_INFO_1
        {
            usri1_name = userName,
            usri1_password = password,
            usri1_password_age = 0,
            usri1_priv = USER_PRIV_USER,
            usri1_home_dir = null,
            usri1_comment = null,
            usri1_flags = UF_SCRIPT | UF_DONT_EXPIRE_PASSWD,
            usri1_script_path = null,
        };

        var rc = NetUserAdd(null, 1, ref info, out var parmErr);
        if (rc != 0)
        {
            throw new Win32Exception(rc, $"NetUserAdd('{userName}') failed with code {rc} (parm_err={parmErr}).");
        }
    }

    internal static void DeleteUserIfExists(string userName)
    {
        var rc = NetUserDel(null, userName);
        if (rc != 0 && rc != NERR_UserNotFound)
        {
            throw new Win32Exception(rc, $"NetUserDel('{userName}') failed with code {rc}.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string usri1_name;
        [MarshalAs(UnmanagedType.LPWStr)] public string usri1_password;
        public uint usri1_password_age;
        public uint usri1_priv;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_home_dir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_comment;
        public uint usri1_flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_script_path;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserAdd(string? servername, int level, ref USER_INFO_1 buf, out int parm_err);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserDel(string? servername, string username);
}
