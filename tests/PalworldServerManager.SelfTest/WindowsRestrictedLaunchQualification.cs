using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;

namespace PalworldServerManager.SelfTest;

// Qualification only, not a production launcher or a claim about Palworld/mod isolation.
// No real credential is passed to the child and no shared token/desktop ACL is changed.
internal static class WindowsRestrictedLaunchQualification
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void NativeCheck(bool value, string step)
    { if (!value) throw new Win32Exception(Marshal.GetLastWin32Error(), "Restricted launch qualification: " + step); }

    internal static int Child(string canary, string parent)
    {
        var stage = 40;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            Check(identity.User!.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal), "Expected virtual service identity.");
            stage = 41;
            NativeCheck(OpenProcessToken(new(-1), 8, out var token), "child token");
            using (token) Check(IsTokenRestricted(token), "Child token is not restricted.");
            stage = 42;
            using (var readable = File.OpenRead(Environment.ProcessPath!))
                Check(readable.ReadByte() == 'M' && readable.ReadByte() == 'Z', "Public executable read failed.");
            stage = 43; var denied = false;
            try { using var file = File.OpenRead(canary); } catch (UnauthorizedAccessException) { denied = true; }
            Check(denied, "Restricted child read private canary.");
            stage = 44; denied = false;
            var replacement = new FileSecurity(); replacement.SetAccessRuleProtection(true, false);
            replacement.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            try { new FileInfo(canary).SetAccessControl(replacement); } catch (UnauthorizedAccessException) { denied = true; }
            Check(denied, "Restricted child replaced private canary DACL.");
            stage = 45;
            Check(int.TryParse(parent, out var pid) && pid > 0 && pid != Environment.ProcessId, "Invalid parent.");
            foreach (var access in new uint[] { 0x1, 0x2, 0x8, 0x10, 0x20, 0x40, 0x80, 0x40000, 0x80000 })
            {
                using var handle = OpenProcess(access, false, pid); var error = Marshal.GetLastWin32Error();
                Check(handle.IsInvalid && error == 5, "Restricted child obtained parent process authority.");
            }
            stage = 46;
            using (var query = OpenProcess(0x1000, false, pid))
            {
                if (query.IsInvalid) Check(Marshal.GetLastWin32Error() == 5, "Parent query failed unexpectedly.");
                else
                {
                    var opened = OpenProcessToken(query.DangerousGetHandle(), 2, out var parentToken);
                    var error = Marshal.GetLastWin32Error();
                    using (parentToken) Check(!opened && error == 5, "Restricted child could duplicate parent token.");
                }
            }
            return 0;
        }
        catch { return stage; } // Bounded stage only; no private paths, values or native text on child output.
    }

    internal static Task OutsideServiceIsNotQualified()
    {
        using var identity = WindowsIdentity.GetCurrent();
        Check(!identity.User!.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal), "Ordinary suite requires its nonservice runner.");
        var missing = Path.Combine(Path.GetTempPath(), "astra-unqualified-" + Guid.NewGuid().ToString("N"));
        try { Run(missing, CancellationToken.None); throw new Exception("Unqualified caller was accepted."); }
        catch (Exception ex) when (ex.Message == "Actual virtual service required.") { }
        Check(!Directory.Exists(missing) && Child(Path.Combine(missing, "absent"), "1") == 40,
            "Unqualified probe performed IO or reported success.");
        return Task.CompletedTask;
    }

    internal static void Run(string root, CancellationToken ct)
    {
        using var identity = WindowsIdentity.GetCurrent();
        Check(identity.User!.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal), "Actual virtual service required.");
        ct.ThrowIfCancellationRequested();
        var canary = Path.Combine(root, "restricted-launch-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            // Inherits the fixture Host root's service/Admin/SYSTEM-only ACL.
            File.WriteAllText(canary, "SYNTHETIC-NOT-A-CREDENTIAL");
            NativeCheck(OpenProcessToken(new(-1), 0xB, out var source), "source token");
            using (source)
            using (var sids = new RestrictingSids(source))
            {
                NativeCheck(CreateRestrictedToken(source, 1, 0, IntPtr.Zero, 0, IntPtr.Zero,
                    (uint)sids.Values.Length, sids.Values, out var reduced), "restricted token");
                using (reduced)
                using (var desktop = new OwnedDesktop(identity.User))
                {
                    var executable = Environment.ProcessPath ?? throw new InvalidOperationException();
                    var arguments = new[] { "--restricted-service-child", canary, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                    var command = new StringBuilder(WindowsClientCommandLine.Build(new ClientLaunchTarget(executable, arguments)));
                    var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = desktop.Name };
                    ct.ThrowIfCancellationRequested();
                    NativeCheck(CreateProcessAsUserW(reduced, executable, command, IntPtr.Zero, IntPtr.Zero, false,
                        0x08000000, IntPtr.Zero, Path.GetDirectoryName(executable)!, ref startup, out var child), "create child");
                    using var process = new SafeProcessHandle(child.Process, true);
                    using var thread = new SafeWaitHandle(child.Thread, true);
                    try
                    {
                        var deadline = Environment.TickCount64 + 15000;
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested(); var wait = WaitForSingleObject(process, 100);
                            if (wait == 0) break;
                            Check(wait == 258 && Environment.TickCount64 < deadline, "Restricted child did not signal within bound.");
                        }
                        NativeCheck(GetExitCodeProcess(process, out var code), "exit code");
                        Check(code == 0, $"Restricted child qualification failed, exit {code:X8}.");
                    }
                    finally
                    {
                        // Cleanup only through the handle returned for our own child, never PID reopen.
                        if (WaitForSingleObject(process, 0) != 0)
                        {
                            NativeCheck(TerminateProcess(process, 42), "owned child cleanup");
                            Check(WaitForSingleObject(process, 10000) == 0, "Owned child cleanup did not signal.");
                        }
                    }
                }
            }
            Check(File.ReadAllText(canary) == "SYNTHETIC-NOT-A-CREDENTIAL", "Private canary changed.");
        }
        finally { File.Delete(canary); }
    }

    private sealed class RestrictingSids : IDisposable
    {
        private readonly List<IntPtr> allocated = [];
        internal SidAndAttributes[] Values { get; }
        internal RestrictingSids(SafeAccessTokenHandle source)
        {
            try
            {
                var identities = new List<SecurityIdentifier> { new(WellKnownSidType.WorldSid, null), new(WellKnownSidType.BuiltinUsersSid, null) };
                GetTokenInformation(source, 28, IntPtr.Zero, 0, out var needed);
                if (needed > 0)
                {
                    Check(needed <= 4096, "Unexpected logon SID size."); var buffer = Marshal.AllocHGlobal(needed);
                    try
                    {
                        NativeCheck(GetTokenInformation(source, 28, buffer, needed, out _), "logon SID");
                        var count = Marshal.ReadInt32(buffer); Check(count is 0 or 1, "Unexpected logon SID count.");
                        if (count == 1) identities.Add(new(Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4)));
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                Values = identities.Select(sid =>
                {
                    var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
                    var pointer = Marshal.AllocHGlobal(bytes.Length); allocated.Add(pointer); Marshal.Copy(bytes, 0, pointer, bytes.Length);
                    return new SidAndAttributes { Sid = pointer };
                }).ToArray();
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var pointer in allocated) Marshal.FreeHGlobal(pointer); allocated.Clear(); }
    }

    private sealed class OwnedDesktop : IDisposable
    {
        private IntPtr handle;
        internal IntPtr Name { get; private set; }
        internal OwnedDesktop(SecurityIdentifier service)
        {
            var original = GetThreadDesktop(GetCurrentThreadId());
            try
            {
                var station = GetProcessWindowStation();
                GetUserObjectInformationW(station, 2, IntPtr.Zero, 0, out var needed);
                Check(needed is > 0 and <= 4096, "Unexpected station name size."); var buffer = Marshal.AllocHGlobal((int)needed);
                string stationName;
                try { NativeCheck(GetUserObjectInformationW(station, 2, buffer, needed, out _), "station name"); stationName = Marshal.PtrToStringUni(buffer)!; }
                finally { Marshal.FreeHGlobal(buffer); }
                Check(!stationName.Equals("WinSta0", StringComparison.OrdinalIgnoreCase), "Interactive service station refused.");
                var name = "AstraRestricted" + Guid.NewGuid().ToString("N");
                NativeCheck(ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;{service.Value})(A;;GA;;;WD)", 1, out var sd, out _), "desktop security");
                try
                {
                    var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Descriptor = sd };
                    handle = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, 0x000F01FF, ref security);
                    NativeCheck(handle != IntPtr.Zero, "private desktop");
                }
                finally { LocalFree(sd); }
                // Never switch the visible desktop or alter an existing object's ACL.
                Name = Marshal.StringToHGlobalUni(stationName + "\\" + name);
                if (GetThreadDesktop(GetCurrentThreadId()) != original) NativeCheck(SetThreadDesktop(original), "restore fixture thread desktop");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (Name != IntPtr.Zero) { Marshal.FreeHGlobal(Name); Name = IntPtr.Zero; }
            if (handle != IntPtr.Zero) { NativeCheck(CloseDesktop(handle), "close private desktop"); handle = IntPtr.Zero; }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { internal IntPtr Sid; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Size; internal IntPtr Descriptor; internal int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        internal int Size; internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        internal ushort Show, ReservedSize; internal IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { internal IntPtr Process, Thread; internal uint Id, ThreadId; }
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr buffer, int length, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateRestrictedToken(SafeAccessTokenHandle source, uint flags, uint disabledCount, IntPtr disabled, uint deletedCount, IntPtr deleted, uint restrictCount, [In] SidAndAttributes[] restrict, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformationW(IntPtr handle, int index, IntPtr data, uint length, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint version, out IntPtr sd, out uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode, uint flags, uint access, ref SecurityAttributes security);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
}
