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
// No real credential is passed to the child. Only the disposable service's verified
// logon station receives a temporary unique-SID grant, restored before fixture completion.
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
            var authorityStage = 50;
            foreach (var access in new uint[] { 0x1, 0x2, 0x8, 0x10, 0x20, 0x40, 0x80, 0x40000, 0x80000 })
            {
                stage = authorityStage++;
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
            NativeCheck(OpenProcessToken(new(-1), 0x8B, out var source), "source token");
            using (source)
            using (var sids = new RestrictingSids(source))
            {
                NativeCheck(CreateRestrictedToken(source, 1, 0, IntPtr.Zero, 0, IntPtr.Zero,
                    (uint)sids.Values.Length, sids.Values, out var reduced), "restricted token");
                using (reduced)
                using (var desktop = new OwnedDesktop(identity.User, sids.ChildSid, source))
                {
                    ConfigureChildObjectDacl(reduced, identity.User, sids.ChildSid);
                    var startupAccess = desktop.ProbeAccess(reduced);
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
                        Check(code == 0, $"Restricted child qualification failed, exit {code:X8}; {startupAccess}.");
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
        internal SecurityIdentifier ChildSid { get; }
        internal RestrictingSids(SafeAccessTokenHandle source)
        {
            var nonce = Guid.NewGuid().ToByteArray();
            ChildSid = new($"S-1-5-21-{BitConverter.ToUInt32(nonce, 0)}-{BitConverter.ToUInt32(nonce, 4)}-{BitConverter.ToUInt32(nonce, 8)}-{BitConverter.ToUInt32(nonce, 12)}");
            try
            {
                var identities = new List<SecurityIdentifier> { new(WellKnownSidType.WorldSid, null), new(WellKnownSidType.BuiltinUsersSid, null), ChildSid };
                // Do not inherit the shared logon SID's access to other service objects.
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

    private static void ConfigureChildObjectDacl(SafeAccessTokenHandle reduced, SecurityIdentifier service, SecurityIdentifier child)
    {
        // Only this newly created token: let the child access its own new kernel objects.
        // The unique restricting SID is not granted access to any Host object or file.
        NativeCheck(ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;{service.Value})(A;;GA;;;SY)(A;;GA;;;{child.Value})", 1, out var sd, out _), "child object DACL");
        var information = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            NativeCheck(GetSecurityDescriptorDacl(sd, out var present, out var dacl, out _), "read child DACL");
            Check(present && dacl != IntPtr.Zero, "Missing child DACL."); Marshal.WriteIntPtr(information, dacl);
            NativeCheck(SetTokenInformation(reduced, 6, information, IntPtr.Size), "set copied token default DACL");
        }
        finally { Marshal.FreeHGlobal(information); LocalFree(sd); }
    }

    private sealed class OwnedDesktop : IDisposable
    {
        private IntPtr handle;
        private IntPtr ownedStation;
        private byte[]? originalStationSecurity;
        private string stationName = "", desktopName = "";
        internal IntPtr Name { get; private set; }
        internal OwnedDesktop(SecurityIdentifier service, SecurityIdentifier child, SafeAccessTokenHandle source)
        {
            var original = GetThreadDesktop(GetCurrentThreadId());
            try
            {
                var station = GetProcessWindowStation();
                GetUserObjectInformationW(station, 2, IntPtr.Zero, 0, out var needed);
                Check(needed is > 0 and <= 4096, "Unexpected station name size."); var buffer = Marshal.AllocHGlobal((int)needed);
                try { NativeCheck(GetUserObjectInformationW(station, 2, buffer, needed, out _), "station name"); stationName = Marshal.PtrToStringUni(buffer)!; }
                finally { Marshal.FreeHGlobal(buffer); }
                Check(!stationName.Equals("WinSta0", StringComparison.OrdinalIgnoreCase), "Interactive service station refused.");
                VerifyServiceLogon(source, stationName);
                ownedStation = OpenWindowStationW(stationName, false, 0x60000);
                NativeCheck(ownedStation != IntPtr.Zero, "open owned service station security");
                originalStationSecurity = ReadSecurity(ownedStation);
                var descriptor = new RawSecurityDescriptor(originalStationSecurity, 0);
                Check(descriptor.DiscretionaryAcl is not null, "Missing service station DACL.");
                // Operational station rights only, no WRITE_DAC/WRITE_OWNER/DELETE for child.
                descriptor.DiscretionaryAcl!.InsertAce(descriptor.DiscretionaryAcl.Count,
                    new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x2037F, child, false, null));
                var changed = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(changed, 0);
                uint securityInformation = 4;
                NativeCheck(SetUserObjectSecurity(ownedStation, ref securityInformation, changed), "temporary child station rights");
                var name = "AstraRestricted" + Guid.NewGuid().ToString("N");
                desktopName = name;
                NativeCheck(ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;{service.Value})(A;;0x201ff;;;{child.Value})", 1, out var sd, out _), "desktop security");
                try
                {
                    var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Descriptor = sd };
                    handle = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, 0x000F01FF, ref security);
                    NativeCheck(handle != IntPtr.Zero, "private desktop");
                }
                finally { LocalFree(sd); }
                // Never switch a visible desktop or grant access to a pre-existing user/system station.
                Name = Marshal.StringToHGlobalUni(stationName + "\\" + name);
                if (GetThreadDesktop(GetCurrentThreadId()) != original) NativeCheck(SetThreadDesktop(original), "restore fixture thread desktop");
            }
            catch { Dispose(); throw; }
        }
        internal string ProbeAccess(SafeAccessTokenHandle reduced)
            => WindowsIdentity.RunImpersonated(reduced, () =>
            {
                var station = OpenWindowStationW(stationName, false, 0x2037F);
                var stationError = station == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                if (station != IntPtr.Zero) NativeCheck(CloseWindowStation(station), "close diagnostic station handle");
                var desktop = OpenDesktopW(desktopName, 0, false, 0x201FF);
                var desktopError = desktop == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                if (desktop != IntPtr.Zero) NativeCheck(CloseDesktop(desktop), "close diagnostic desktop handle");
                // Object names are test-only nonsecret Windows resource metadata.
                return $"station={stationName}, stationOperationalError={stationError}, desktopOperationalError={desktopError}";
            });
        private static void VerifyServiceLogon(SafeAccessTokenHandle source, string name)
        {
            GetTokenInformation(source, 10, IntPtr.Zero, 0, out var length);
            Check(length is >= 16 and <= 4096, "Unexpected token statistics size."); var buffer = Marshal.AllocHGlobal(length);
            try
            {
                NativeCheck(GetTokenInformation(source, 10, buffer, length, out _), "service logon identity");
                var low = unchecked((uint)Marshal.ReadInt32(buffer, 8)); var high = unchecked((uint)Marshal.ReadInt32(buffer, 12));
                Check(high != 0 || low is not (0x3e4 or 0x3e5 or 0x3e7), "Shared built-in service logon refused.");
                Check(name.Equals($"Service-0x{high:x}-{low:x}$", StringComparison.OrdinalIgnoreCase), "Station does not match this service logon.");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        private static byte[] ReadSecurity(IntPtr station)
        {
            uint information = 4;
            GetUserObjectSecurity(station, ref information, null, 0, out var length);
            Check(length is > 0 and <= 65536, "Unexpected station descriptor size."); var bytes = new byte[length];
            NativeCheck(GetUserObjectSecurity(station, ref information, bytes, length, out _), "read station security"); return bytes;
        }
        public void Dispose()
        {
            try
            {
                if (ownedStation != IntPtr.Zero && originalStationSecurity is not null)
                {
                    uint information = 4;
                    NativeCheck(SetUserObjectSecurity(ownedStation, ref information, originalStationSecurity), "restore station security");
                    var restored = new RawSecurityDescriptor(ReadSecurity(ownedStation), 0).GetSddlForm(AccessControlSections.Access);
                    Check(restored == new RawSecurityDescriptor(originalStationSecurity, 0).GetSddlForm(AccessControlSections.Access), "Station security restoration mismatch.");
                    originalStationSecurity = null;
                }
            }
            finally
            {
                if (Name != IntPtr.Zero) { Marshal.FreeHGlobal(Name); Name = IntPtr.Zero; }
                try { if (handle != IntPtr.Zero) { NativeCheck(CloseDesktop(handle), "close private desktop"); handle = IntPtr.Zero; } }
                finally { if (ownedStation != IntPtr.Zero) { NativeCheck(CloseWindowStation(ownedStation), "close owned station security handle"); ownedStation = IntPtr.Zero; } }
            }
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
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr buffer, int length);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr dacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern IntPtr OpenWindowStationW(string name, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern IntPtr OpenDesktopW(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseWindowStation(IntPtr station);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectSecurity(IntPtr handle, ref uint information, [Out] byte[]? descriptor, uint length, out uint needed);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetUserObjectSecurity(IntPtr handle, ref uint information, byte[] descriptor);
}
