using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PalworldServerManager.SelfTest;

// Only for handles/tokens owned by these synthetic-process fixtures. No machine-wide
// privilege changes, production impersonation, or reopening a PID for cleanup.
internal static class NativeProcessTestContext
{
    internal static void WaitForExit(Process child)
    {
        // Process.WaitForExitAsync can take a HasExited shortcut based on the exit
        // code, before the kernel object signals. Our observation contract uses
        // the stronger native signal; this independent oracle must do so too.
        var result = WaitForSingleObject(child.SafeHandle, 10000);
        if (result != 0) throw new Exception($"Synthetic process native wait failed: {result}.");
    }

    internal static T WithoutPrivileges<T>(Func<T> action)
    {
        // An elevated runner (including System.Diagnostics.Process initialization)
        // can enable SeDebugPrivilege, which bypasses a process DACL. A temporary
        // reduced token makes the actual denial test independent of runner rights.
        if (!OpenProcessToken(new IntPtr(-1), 0xB, out var source))
            throw new Exception("Synthetic source token open failed.");
        using (source)
        {
            if (!CreateRestrictedToken(source, 1, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out var reduced))
                throw new Exception("Synthetic reduced token creation failed.");
            using (reduced) return WindowsIdentity.RunImpersonated(reduced, action);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle source, uint flags, uint disabledCount,
        IntPtr disabled, uint deletedCount, IntPtr deleted, uint restrictedCount, IntPtr restricted, out SafeAccessTokenHandle token);
}
