using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PalworldServerManager.SelfTest;

/// <summary>
/// TEST-ONLY native interop (#41 item 3) that reads the TokenUser SID of a REAL running process
/// by PID. This is deliberately independent of - and a stronger proof than - reading
/// Win32_Service.StartName, which only reflects the SCM CONFIGURATION requested at install time,
/// not what the actual running process token turned out to be. Never shipped in any production
/// project; no production API is added merely to support this assertion.
/// </summary>
internal static class ProcessTokenInspector
{
    // The minimum right that still allows OpenProcessToken (documented as sufficient since
    // Windows Vista), rather than the broader PROCESS_QUERY_INFORMATION.
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenUser = 1;

    internal static SecurityIdentifier GetProcessTokenUserSid(int processId)
    {
        var processHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess failed for PID {processId}.");
        }

        try
        {
            if (!OpenProcessToken(processHandle, TOKEN_QUERY, out var tokenHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcessToken failed for PID {processId}.");
            }

            try
            {
                // Two-call size-probe pattern: the first call reports the required buffer size.
                GetTokenInformation(tokenHandle, TokenUser, IntPtr.Zero, 0, out var requiredSize);
                var buffer = Marshal.AllocHGlobal(requiredSize);
                try
                {
                    if (!GetTokenInformation(tokenHandle, TokenUser, buffer, requiredSize, out _))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetTokenInformation(TokenUser) failed for PID {processId}.");
                    }

                    // TOKEN_USER is a single SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } -
                    // the SID pointer is the buffer's first pointer-sized field.
                    var sidPointer = Marshal.ReadIntPtr(buffer, 0);
                    return new SecurityIdentifier(sidPointer);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
