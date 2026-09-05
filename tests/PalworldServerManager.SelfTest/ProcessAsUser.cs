using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PalworldServerManager.SelfTest;

/// <summary>
/// TEST-ONLY native interop for launching a helper process under a DIFFERENT Windows user's
/// token via CreateProcessWithLogonW, and capturing its stdout - with a REAL, FAIL-CLOSED bounded
/// timeout: every native call that matters for correctness (TerminateProcess, the post-kill wait,
/// the output-reader's completion) is verified, never assumed.
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
    /// Throws <see cref="TimeoutException"/> if it does not complete within <paramref name="timeout"/>
    /// - and that exception is only thrown once termination has actually been CONFIRMED (or, if
    /// confirmation itself failed, the exception says so explicitly rather than claiming success).
    /// </summary>
    internal static (int ExitCode, string StdOut) Run(
        string userName, string password, string exePath, string arguments, TimeSpan timeout)
    {
        var securityAttributes = NewInheritableSecurityAttributes();

        if (!CreatePipe(out var readHandle, out var writeHandle, ref securityAttributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
        }

        // From here, both pipe handles have exactly ONE clear owner at every point: THIS method,
        // until either a setup failure below (this method closes both ends itself, exactly once)
        // or process creation succeeds, at which point the write end is closed immediately below
        // and readHandle's ownership transfers entirely to WaitWithTimeout's reader thread.
        if (!SetHandleInformation(readHandle, HANDLE_FLAG_INHERIT, 0))
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(readHandle);
            CloseHandle(writeHandle);
            throw new Win32Exception(error, "SetHandleInformation failed.");
        }

        var startupInfo = NewRedirectedStartupInfo(writeHandle);

        // Security guidance for CreateProcessWithLogonW: pass a quoted executable path as the
        // FIRST token of the command line rather than relying on lpApplicationName resolution.
        // lpCommandLine is a MUTABLE buffer per the Win32 contract, so it must be marshalled as a
        // StringBuilder, never a plain immutable .NET string.
        var commandLine = new StringBuilder($"\"{exePath}\" {arguments}");

        var started = CreateProcessWithLogonW(
            userName, ".", password, LOGON_WITH_PROFILE, null, commandLine, CREATE_NO_WINDOW,
            IntPtr.Zero, null, ref startupInfo, out var processInformation);

        if (!started)
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(readHandle);
            CloseHandle(writeHandle);
            throw new Win32Exception(error, $"CreateProcessWithLogonW failed for user '{userName}'.");
        }

        // The parent no longer needs its own copy of the write end - closing it lets the reader
        // observe EOF once the child (the only remaining holder) exits or is terminated.
        CloseHandle(writeHandle);

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
            var error = Marshal.GetLastWin32Error();
            CloseHandle(readHandle);
            CloseHandle(writeHandle);
            throw new Win32Exception(error, "SetHandleInformation failed.");
        }

        var startupInfo = NewRedirectedStartupInfo(writeHandle);
        var commandLine = new StringBuilder($"\"{exePath}\" {arguments}");

        var started = CreateProcess(
            null, commandLine, IntPtr.Zero, IntPtr.Zero, true, CREATE_NO_WINDOW, IntPtr.Zero, null,
            ref startupInfo, out var processInformation);

        if (!started)
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(readHandle);
            CloseHandle(writeHandle);
            throw new Win32Exception(error, "CreateProcess failed.");
        }

        CloseHandle(writeHandle);

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
    /// The shared timeout/capture core, FAIL-CLOSED at every step:
    ///
    /// - the pipe is read on a background thread CONCURRENTLY with waiting for the process to
    ///   exit (never blocking on ReadToEnd before the wait, which would make the timeout
    ///   ineffective against a hung child);
    /// - on timeout, TerminateProcess's own return value is checked - a failure to terminate is
    ///   reported as such, never silently assumed to have worked;
    /// - the post-kill WaitForSingleObject result is checked (WAIT_OBJECT_0 = confirmed dead;
    ///   WAIT_TIMEOUT or anything else = cleanup failure, reported explicitly);
    /// - the output reader's completion is checked (bounded ManualResetEventSlim.Wait) on BOTH
    ///   the timeout and the normal-completion path - output is only returned, and readHandle
    ///   only closed, once the reader has actually finished;
    /// - STILL_ACTIVE is never treated as a real exit code;
    /// - process/thread handles are closed exactly once, on every path, via the outer finally.
    ///
    /// Termination is always by the exact process HANDLE captured at creation - never by name.
    /// </summary>
    private static (int ExitCode, string StdOut) WaitWithTimeout(
        IntPtr hProcess, IntPtr hThread, IntPtr readHandle, TimeSpan timeout, string identity)
    {
        try
        {
            string? capturedOutput = null;
            Exception? readException = null;
            var readerDone = new ManualResetEventSlim(false);

            // Ownership of readHandle transfers here: this is the ONLY place that wraps/closes it.
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
                finally
                {
                    readerDone.Set();
                }
            })
            {
                IsBackground = true,
                Name = "PSM-ProcessAsUser-StdOutReader",
            };
            readThread.Start();

            var waitResult = WaitForSingleObject(hProcess, BoundedMillis(timeout));

            if (waitResult == WAIT_TIMEOUT)
            {
                if (!TerminateProcess(hProcess, 1))
                {
                    var terminateError = Marshal.GetLastWin32Error();
                    throw new TimeoutException(
                        $"Helper process ({identity}) did not complete within {timeout}, and TerminateProcess itself FAILED " +
                        $"(Win32 error {terminateError}) - termination is NOT confirmed; the process may still be running.");
                }

                var postKillWait = WaitForSingleObject(hProcess, 5000);
                if (postKillWait == WAIT_TIMEOUT)
                {
                    throw new TimeoutException(
                        $"Helper process ({identity}) did not complete within {timeout}; TerminateProcess was called but the " +
                        "process did not report terminated within 5s afterward - termination is NOT confirmed.");
                }

                if (postKillWait != WAIT_OBJECT_0)
                {
                    var waitError = Marshal.GetLastWin32Error();
                    throw new TimeoutException(
                        $"Helper process ({identity}) did not complete within {timeout}; the post-termination wait itself " +
                        $"failed (Win32 error {waitError}) - termination is NOT confirmed.");
                }

                // Termination is now CONFIRMED (WAIT_OBJECT_0). The terminated process's copy of
                // the pipe write end is now closed, so give the reader a bounded chance to
                // observe EOF before reporting a cleanup failure.
                if (!readerDone.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        $"Helper process ({identity}) was confirmed terminated after timing out, but its output reader did " +
                        "not reach EOF within 5s afterward.");
                }

                throw new TimeoutException(
                    $"Helper process ({identity}) did not complete within {timeout} and was terminated " +
                    "(termination confirmed; output reader confirmed drained).");
            }

            if (waitResult != WAIT_OBJECT_0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"WaitForSingleObject failed for helper process ({identity}).");
            }

            // The process exited normally - confirm the reader actually reaches EOF rather than
            // silently returning with it still blocked.
            if (!readerDone.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException($"Helper process ({identity}) exited, but its output reader did not reach EOF within 10s afterward.");
            }

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

    private static uint BoundedMillis(TimeSpan timeout)
        => (uint)Math.Min(Math.Max(timeout.TotalMilliseconds, 0), uint.MaxValue - 1);

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
