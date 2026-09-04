using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PalworldServerManager.SelfTest;

/// <summary>
/// TEST-ONLY native interop for launching a helper process under a DIFFERENT Windows user's
/// token via CreateProcessWithLogonW, and capturing its stdout.
///
/// This exists purely to give the #41 privileged integration harness a genuine way to prove
/// authorized-vs-unauthorized non-admin behavior and cross-user DPAPI isolation: an elevated
/// process CHECKING group membership would not actually prove anything about what that user's own
/// token can and cannot do. Deliberately not shipped in any production project - it lives only in
/// this test executable.
/// </summary>
internal static class ProcessAsUser
{
    private const int LOGON_WITH_PROFILE = 0x00000001;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint HANDLE_FLAG_INHERIT = 1;

    /// <summary>
    /// Runs <paramref name="exePath"/> <paramref name="arguments"/> as <paramref name="userName"/>
    /// (a local account), waits for it to exit, and returns its exit code and captured stdout.
    /// </summary>
    internal static (int ExitCode, string StdOut) Run(
        string userName, string password, string exePath, string arguments, TimeSpan timeout)
    {
        var securityAttributes = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
            lpSecurityDescriptor = IntPtr.Zero,
        };

        if (!CreatePipe(out var readHandle, out var writeHandle, ref securityAttributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
        }

        // The parent's read end must NOT be inherited by the child, or the pipe never reports EOF.
        if (!SetHandleInformation(readHandle, HANDLE_FLAG_INHERIT, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetHandleInformation failed.");
        }

        var startupInfo = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESTDHANDLES,
            hStdOutput = writeHandle,
            hStdError = writeHandle,
            hStdInput = IntPtr.Zero,
        };

        // Security guidance for CreateProcessWithLogonW: pass a quoted executable path as the
        // FIRST token of the command line rather than relying on lpApplicationName resolution.
        var commandLine = $"\"{exePath}\" {arguments}";

        var started = CreateProcessWithLogonW(
            userName,
            ".",
            password,
            LOGON_WITH_PROFILE,
            null,
            commandLine,
            CREATE_NO_WINDOW,
            IntPtr.Zero,
            null,
            ref startupInfo,
            out var processInformation);

        // The parent no longer needs the write end - closing it lets ReadToEnd observe EOF once
        // the child (the only other holder) exits.
        CloseHandle(writeHandle);

        if (!started)
        {
            CloseHandle(readHandle);
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcessWithLogonW failed for user '{userName}'.");
        }

        string output;
        using (var safeReadHandle = new SafeFileHandle(readHandle, ownsHandle: true))
        using (var stream = new FileStream(safeReadHandle, FileAccess.Read))
        using (var reader = new StreamReader(stream))
        {
            output = reader.ReadToEnd();
        }

        WaitForSingleObject(processInformation.hProcess, (uint)timeout.TotalMilliseconds);
        GetExitCodeProcess(processInformation.hProcess, out var exitCode);

        CloseHandle(processInformation.hThread);
        CloseHandle(processInformation.hProcess);

        return ((int)exitCode, output.Trim());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithLogonW(
        string userName,
        string? domain,
        string password,
        int logonFlags,
        string? applicationName,
        string commandLine,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
