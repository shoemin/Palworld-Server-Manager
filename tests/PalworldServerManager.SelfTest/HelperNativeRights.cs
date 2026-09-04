using System.Runtime.InteropServices;

namespace PalworldServerManager.SelfTest;

/// <summary>
/// TEST-ONLY, self-contained SCM P/Invoke used by the "--helper-native-rights" mode (#41) to
/// independently verify the activation-group DACL from a completely separate code path than
/// production's own <c>ServiceControlManagerNative</c> - a bug shared between production code and
/// a test that reuses the very same P/Invoke would not be caught this way.
///
/// Prints one "GRANTED:&lt;right&gt;" or "DENIED:&lt;right&gt;" line per probed right for the
/// (elevated) harness to parse.
/// </summary>
internal static class HelperNativeRights
{
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const int ERROR_ACCESS_DENIED = 5;

    private static readonly (string Name, uint Right)[] ForbiddenRights =
    [
        ("SERVICE_STOP", 0x0020),
        ("SERVICE_CHANGE_CONFIG", 0x0002),
        ("DELETE", 0x00010000),
    ];

    internal static void ProbeForbiddenRights(string serviceName)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            throw new InvalidOperationException($"OpenSCManager failed with code {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            foreach (var (name, right) in ForbiddenRights)
            {
                var handle = OpenService(scm, serviceName, right);
                if (handle != IntPtr.Zero)
                {
                    CloseServiceHandle(handle);
                    Console.WriteLine($"GRANTED:{name}");
                    continue;
                }

                var error = Marshal.GetLastWin32Error();
                Console.WriteLine(error == ERROR_ACCESS_DENIED ? $"DENIED:{name}" : $"ERROR:{name}:{error}");
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
