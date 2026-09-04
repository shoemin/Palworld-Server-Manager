using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PalworldServerManager.SelfTest;

/// <summary>
/// TEST-ONLY native interop for launching a helper process under a DIFFERENT Windows user's
/// token via CreateProcessWithLogonW, and capturing its stdout - with a REAL bounded timeout that
/// terminates a hung child rather than blocking the caller forever.
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
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const uint STILL_ACTIVE = 259;

    /// <summary>
    /// Runs <paramref name="exePath"/> <paramref name="arguments"/> as <paramref name="userName"/>
    /// (a local account), waits for it to exit, and returns its exit code and captured stdout.
    /// Throws <see cref="TimeoutException"/> - after terminating EXACTLY that helper process, and
    /// only that process - if it does not complete within <paramref name="timeout"/>.
    /// </summary>
    internal static (int ExitCode, string StdOut) Run(
        string userName, string password, string exePath, string arguments, TimeSpan timeout)
    {
        var securityAttributes = NewInheritableSecurityAttributes();

        if (!CreatePipe(out var readHandle, out var writeHandle, ref securityAttributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
        }

        // The parent's read end must NOT be inherited by the child, or the pipe never reports EOF.
        if (!SetHandleInformation(readHandle, HANDLE_FLAG_INHERIT, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetHandleInformation failed.");
        }

        var startupInfo = NewRedirectedStartupInfo(writeHandle);

        // Security guidance for CreateProcessWithLogonW: pass a quoted executable path as the
        // FIRST token of the command line rather than relying on lpApplicationName resolution.
        // lpCommandLine is a MUTABLE buffer per the Win32 contract, so it must be marshalled as a
        // StringBuilder, never a plain immutable .NET string.
        var commandLine = new StringBuilder($"\"{exePath}\" {arguments}");

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

        // The parent no longer needs the write end - closing it lets the reader observe EOF once
        // the child (the only other holder) exits or is terminated.
        CloseHandle(writeHandle);

        if (!started)
        {
            CloseHandle(readHandle);
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcessWithLogonW failed for user '{userName}'.");
        }

        return WaitWithTimeout(processInformation.hProcess, processInformation.hThread, readHandle, timeout, $"user '{userName}'");
    }

    /// <summary>
    /// TEST-ONLY seam for proving the timeout mechanism itself: runs a process AS THE CURRENT
    /// (already-authenticated) user via plain CreateProcess, going through the exact same
    /// WaitWithTimeout core as <see cref="Run"/>. Used only by the ordinary self-test suite, which
    /// cannot exercise CreateProcessWithLogonW without real alternate-user credentials.
    /// </summary>
    internal static (int ExitCode, string StdOut) RunAsCurrentUserForTest(string exePath, string arguments, TimeSpan timeout)
    {
        var securityAttributes = NewInheritableSecurityAttributes();

        if (!CreatePipe(out var readHandle, out var writeHandle, ref securityAttributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
        }

        if (!SetHandleInformation(readHandle, HANDLE_FLAG_INHERIT, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetHandleInformation failed.");
        }

        var startupInfo = NewRedirectedStartupInfo(writeHandle);
        var commandLine = new StringBuilder($"\"{exePath}\" {arguments}");

        var started = CreateProcess(
            null, commandLine, IntPtr.Zero, IntPtr.Zero, true, CREATE_NO_WINDOW, IntPtr.Zero, null,
            ref startupInfo, out var processInformation);

        CloseHandle(writeHandle);

        if (!started)
        {
            CloseHandle(readHandle);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed.");
        }

        return WaitWithTimeout(processInformation.hProcess, processInformation.hThread, readHandle, timeout, "current-user test helper");
    }

    private static SECURITY_ATTRIBUTES NewInheritableSecurityAttributes() => new()
    {
        nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
        bInheritHandle = true,
        lpSecurityDescriptor = IntPtr.Zero,
    };

    private static STARTUPINFO NewRedirectedStartupInfo(IntPtr writeHandle) => new()
    {
        cb = Marshal.SizeOf<STARTUPINFO>(),
        dwFlags = STARTF_USESTDHANDLES,
        hStdOutput = writeHandle,
        hStdError = writeHandle,
        hStdInput = IntPtr.Zero,
    };

    /// <summary>
    /// The shared timeout/capture core. Reads the pipe CONCURRENTLY with waiting for the process
    /// to exit (never blocking on ReadToEnd before the wait, which would make the timeout
    /// ineffective against a hung child). On timeout, terminates ONLY the exact process handle
    /// passed in - never by name - waits for it to actually die, closes every handle on every
    /// path, and throws <see cref="TimeoutException"/> rather than ever returning STILL_ACTIVE as
    /// though it were a real exit code.
    /// </summary>
    private static (int ExitCode, string StdOut) WaitWithTimeout(
        IntPtr hProcess, IntPtr hThread, IntPtr readHandle, TimeSpan timeout, string identity)
    {
        try
        {
            string? capturedOutput = null;
            Exception? readException = null;

            var readThread = new Thread(() =>
            {
                try
                {
                    using var safeReadHandle = new SafeFileHandle(readHandle, ownsHandle: true);
                    using var stream = new FileStream(safeReadHandle, FileAccess.Read);
                    using var reader = new StreamReader(stream);
                    capturedOutput = reader.ReadToEnd();
                }
                catch (Exception ex)
                {
                    readException = ex;
                }
            })
            {
                IsBackground = true,
                Name = "PSM-ProcessAsUser-StdOutReader",
            };
            readThread.Start();

            var boundedMillis = (uint)Math.Min(Math.Max(timeout.TotalMilliseconds, 0), uint.MaxValue - 1);
            var waitResult = WaitForSingleObject(hProcess, boundedMillis);

            if (waitResult == WAIT_TIMEOUT)
            {
                // Terminate ONLY this exact process handle - never a name-based kill.
                TerminateProcess(hProcess, 1);
                WaitForSingleObject(hProcess, 5000);
                readThread.Join(TimeSpan.FromSeconds(5));
                throw new TimeoutException($"Helper process ({identity}) did not complete within {timeout} and was terminated.");
            }

            if (waitResult != WAIT_OBJECT_0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"WaitForSingleObject failed for helper process ({identity}).");
            }

            // The process has exited, closing its copy of the pipe's write end, so the read
            // thread is already at or very near EOF - this join is a bounded safety net only.
            readThread.Join(TimeSpan.FromSeconds(10));

            if (readException is not null)
            {
                throw new IOException($"Failed to read helper process output ({identity}).", readException);
            }

            if (!GetExitCodeProcess(hProcess, out var exitCode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetExitCodeProcess failed for helper process ({identity}).");
            }

            if (exitCode == STILL_ACTIVE)
            {
                // WaitForSingleObject already signaled completion, so STILL_ACTIVE here would
                // mean something is genuinely wrong (e.g. PID reuse) - never trust it as an
                // outcome.
                throw new InvalidOperationException($"Helper process ({identity}) reported STILL_ACTIVE despite a signaled exit.");
            }

            return ((int)exitCode, (capturedOutput ?? string.Empty).Trim());
        }
        finally
        {
            CloseHandle(hThread);
            CloseHandle(hProcess);
        }
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
        StringBuilder commandLine,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
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
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
