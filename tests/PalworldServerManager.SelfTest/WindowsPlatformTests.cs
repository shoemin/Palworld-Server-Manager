using System.Security.AccessControl;
using System.Security.Principal;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

// #41 Windows platform tests.
//
// ISOLATION: everything here uses temporary directories, in-memory fakes, or test-scoped
// registry/service abstractions. Nothing creates a real service, local group, or user, and
// nothing writes to the real HKCU Run key or the real machine-wide Host data root - so an
// ordinary developer running ./scripts/build.ps1 never mutates machine state.
public static class WindowsPlatformTests
{
    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"{what}: expected {expected}, got {actual}");
        }
    }

    private static void True(bool condition, string what)
    {
        if (!condition) throw new Exception($"Expected condition to hold: {what}");
    }

    private static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        catch (Exception ex) { throw new Exception($"{what}: expected {typeof(TException).Name} but got {ex.GetType().Name}"); }
        throw new Exception($"{what}: expected {typeof(TException).Name} but nothing was thrown");
    }

    // ------------------------------------------------- service binary path quoting (security)

    public static Task TestServiceBinaryPathQuotesPathsWithSpaces()
    {
        // An unquoted service path with spaces is a classic privilege-escalation vector: SCM
        // would try C:\Program.exe before "C:\Program Files\...\Host.exe".
        Equal("\"C:\\Program Files\\Palworld Server Manager\\Host.exe\"",
            ServiceBinaryPath.Build(@"C:\Program Files\Palworld Server Manager\Host.exe"),
            "path with spaces is quoted");

        Equal("\"C:\\Apps\\Host.exe\"", ServiceBinaryPath.Build(@"C:\Apps\Host.exe"),
            "path without spaces is quoted too");

        // Path and arguments stay separate internally and combine safely.
        Equal("\"C:\\Program Files\\PSM\\Host.exe\" --service",
            ServiceBinaryPath.Build(@"C:\Program Files\PSM\Host.exe", "--service"),
            "arguments follow the quoted path");

        // A quote in the path could terminate the quoted argument early.
        Throws<ArgumentException>(() => ServiceBinaryPath.Build("C:\\Bad\"Path\\Host.exe"),
            "an embedded quote must be rejected");
        Throws<ArgumentException>(() => ServiceBinaryPath.Build("  "),
            "an empty path must be rejected");
        return Task.CompletedTask;
    }

    // ------------------------------------------------- service DACL

    private static RawSecurityDescriptor BaselineDescriptor()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var dacl = new RawAcl(GenericAcl.AclRevision, 2);
        dacl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, unchecked((int)0xF01FF), system, false, null));
        dacl.InsertAce(1, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, unchecked((int)0xF01FF), admins, false, null));
        return new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative, null, null, null, dacl);
    }

    public static Task TestActivationGroupAceGrantsExactlyStartAndQueryStatus()
    {
        var group = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var updated = ServiceDaclBuilder.AddActivationGroupAce(BaselineDescriptor(), group);

        var mask = ServiceDaclBuilder.FindActivationGroupMask(updated, group)
            ?? throw new Exception("the activation group ACE is missing");

        // SERVICE_START (0x10) | SERVICE_QUERY_STATUS (0x04) == 0x14, and nothing else.
        Equal(0x14, mask, "activation group mask is exactly SERVICE_START|SERVICE_QUERY_STATUS");

        foreach (var forbidden in ServiceDaclBuilder.ForbiddenForActivationGroup)
        {
            True((mask & forbidden) == 0, $"activation group must not hold right 0x{forbidden:X}");
        }

        return Task.CompletedTask;
    }

    public static Task TestActivationGroupAcePreservesExistingAces()
    {
        var group = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var updated = ServiceDaclBuilder.AddActivationGroupAce(BaselineDescriptor(), group);

        // SYSTEM/Administrators maintenance rights survive untouched.
        Equal(unchecked((int)0xF01FF), ServiceDaclBuilder.FindActivationGroupMask(updated, system)!.Value, "SYSTEM ACE preserved");
        Equal(unchecked((int)0xF01FF), ServiceDaclBuilder.FindActivationGroupMask(updated, admins)!.Value, "Administrators ACE preserved");
        return Task.CompletedTask;
    }

    public static Task TestActivationGroupAceIsIdempotent()
    {
        var group = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var once = ServiceDaclBuilder.AddActivationGroupAce(BaselineDescriptor(), group);
        var twice = ServiceDaclBuilder.AddActivationGroupAce(once, group);

        var aceCount = ((RawAcl)twice.DiscretionaryAcl!).Count;
        Equal(3, aceCount, "re-provisioning must not accumulate duplicate ACEs");
        Equal(0x14, ServiceDaclBuilder.FindActivationGroupMask(twice, group)!.Value, "mask unchanged after re-apply");
        return Task.CompletedTask;
    }

    // ------------------------------------------------- boot start mapping

    public static Task TestBootStartMapsToServiceStartType()
    {
        // Desktop default is boot-start OFF -> demand/manual.
        Equal(3u, WindowsHostServiceLifecycle.ToNativeStartType(HostServiceStartMode.Manual), "Manual -> SERVICE_DEMAND_START");
        Equal(2u, WindowsHostServiceLifecycle.ToNativeStartType(HostServiceStartMode.Automatic), "Automatic -> SERVICE_AUTO_START");
        Equal(4u, WindowsHostServiceLifecycle.ToNativeStartType(HostServiceStartMode.Disabled), "Disabled -> SERVICE_DISABLED");
        return Task.CompletedTask;
    }

    public static Task TestDedicatedServiceAccountIsAPerServiceVirtualAccount()
    {
        var lifecycle = new WindowsHostServiceLifecycle("TestHostSvc");
        Equal(@"NT SERVICE\TestHostSvc", lifecycle.ServiceAccountName, "per-service virtual account");

        // Must not be one of the SHARED built-in service accounts - those would not be a
        // dedicated identity at all.
        foreach (var shared in new[] { "LocalSystem", @"NT AUTHORITY\LocalService", @"NT AUTHORITY\NetworkService" })
        {
            True(!string.Equals(lifecycle.ServiceAccountName, shared, StringComparison.OrdinalIgnoreCase),
                $"service identity must not be the shared {shared}");
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------- machine data root + ACL

    public static Task TestMachineWideHostDataRootIsUnderProgramData()
    {
        var provider = new WindowsHostDataRootProvider();
        var root = provider.GetMachineWideHostDataRoot();
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        True(root.StartsWith(programData, StringComparison.OrdinalIgnoreCase), "root is under CommonApplicationData");
        True(root.EndsWith(Path.Combine("PalworldServerManager", "Host"), StringComparison.OrdinalIgnoreCase), "root ends with the product/Host layout");

        // Machine-wide, never a user profile - v0.4 used LocalApplicationData.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        True(!root.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase), "Host state is not in a user profile");
        return Task.CompletedTask;
    }

    public static Task TestServiceSidIsDerivedAndMatchesWindows()
    {
        // Ground truth captured from Windows' own `sc showsid`. Deriving rather than resolving by
        // name matters because NT SERVICE\<name> only resolves once the service exists - the
        // derivation lets the Host data directory and its ACL be created independently of service
        // creation order, and `sc showsid` computes the same value for a not-yet-created service.
        Equal("S-1-5-80-3951239711-1671533544-1416304335-3763227691-3930497994",
            ServiceSecurityIdentifier.ToSddl("Spooler"), "derived SID matches sc showsid for Spooler");

        Equal("S-1-5-80-2926812050-146808811-1615361383-2252931796-2031534707",
            ServiceSecurityIdentifier.ToSddl("PalworldServerManagerHost"),
            "derived SID matches sc showsid for the product service (which need not exist)");

        // Case-insensitive, as Windows treats service names.
        Equal(ServiceSecurityIdentifier.ToSddl("Spooler"), ServiceSecurityIdentifier.ToSddl("SPOOLER"),
            "service SID derivation is case-insensitive");

        // Round-trips into a real SecurityIdentifier.
        Equal("S-1-5-80-3951239711-1671533544-1416304335-3763227691-3930497994",
            ServiceSecurityIdentifier.ForServiceName("Spooler").Value, "SecurityIdentifier round-trip");
        return Task.CompletedTask;
    }

    public static Task TestHostStateAclGrantsServiceAndAdminsButNotTheActivationGroup()
    {
        // Built without touching the filesystem.
        var security = WindowsHostDataRootProvider.BuildHostStateSecurity(@"NT SERVICE\TestHostSvc");

        True(security.AreAccessRulesProtected, "inheritance is disabled so broader ProgramData rights are not inherited");

        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
        var sids = rules.Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value).ToList();

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value;

        True(sids.Contains(system), "SYSTEM has access");
        True(sids.Contains(admins), "Administrators have access");
        // The activation group (and ordinary Users generally) must NOT gain direct Host-state
        // access - start eligibility is not SQLite authority.
        True(!sids.Contains(users), "ordinary Users have no direct Host-state access");
        Equal(3, rules.Count, "exactly SYSTEM, Administrators, and the service account");
        return Task.CompletedTask;
    }

    // ------------------------------------------------- IHostActivation

    private sealed class FakeServiceHandle : IServiceControlHandle
    {
        private readonly Func<HostServiceRunState> _status;
        private readonly Action _start;

        public FakeServiceHandle(Func<HostServiceRunState> status, Action start)
        {
            _status = status;
            _start = start;
        }

        public HostServiceRunState Status => _status();

        public void Start() => _start();

        public void Dispose() { }
    }

    public static async Task TestActivationIsIdempotentAndMapsFailureClasses()
    {
        // Already running -> no Start issued.
        var startCalls = 0;
        var running = new WindowsHostActivation("svc", _ => new FakeServiceHandle(() => HostServiceRunState.Running, () => startCalls++));
        Equal(HostActivationResult.AlreadyRunning, await running.RequestStartAsync(), "Running -> AlreadyRunning");
        Equal(0, startCalls, "no Start is issued for an already-running service");
        Equal(true, await running.IsHostRunningAsync(), "IsHostRunning true when Running");

        // StartPending -> non-error, still no second Start.
        var pending = new WindowsHostActivation("svc", _ => new FakeServiceHandle(() => HostServiceRunState.StartPending, () => startCalls++));
        Equal(HostActivationResult.StartRequested, await pending.RequestStartAsync(), "StartPending -> StartRequested");
        Equal(0, startCalls, "no Start is issued while a start is already pending");

        // Stopped -> exactly one Start.
        var stopped = new WindowsHostActivation("svc", _ => new FakeServiceHandle(() => HostServiceRunState.Stopped, () => startCalls++));
        Equal(HostActivationResult.StartRequested, await stopped.RequestStartAsync(), "Stopped -> StartRequested");
        Equal(1, startCalls, "exactly one Start issued");

        // Failure classes are distinguishable, and IsHostRunning stays false rather than throwing.
        var missing = new WindowsHostActivation("svc", _ => throw new InvalidOperationException("x",
            new System.ComponentModel.Win32Exception(1060)));
        Equal(HostActivationResult.ServiceNotInstalled, await missing.RequestStartAsync(), "1060 -> ServiceNotInstalled");
        Equal(false, await missing.IsHostRunningAsync(), "missing service reads as not running");

        var denied = new WindowsHostActivation("svc", _ => throw new InvalidOperationException("x",
            new System.ComponentModel.Win32Exception(5)));
        Equal(HostActivationResult.AccessDenied, await denied.RequestStartAsync(), "5 -> AccessDenied");

        var other = new WindowsHostActivation("svc", _ => new FakeServiceHandle(
            () => HostServiceRunState.Stopped, () => throw new InvalidOperationException("boom")));
        Equal(HostActivationResult.StartFailed, await other.RequestStartAsync(), "other failure -> StartFailed");
    }

    // ------------------------------------------------- login start

    private sealed class FakeRegistryStore : ILoginStartRegistryStore
    {
        public Dictionary<string, string> Values { get; } = new();

        public string? GetValue(string valueName) => Values.TryGetValue(valueName, out var v) ? v : null;

        public void SetValue(string valueName, string value) => Values[valueName] = value;

        public void DeleteValue(string valueName) => Values.Remove(valueName);
    }

    public static async Task TestLoginStartUsesTestScopedStoreAndQuotesCommand()
    {
        var store = new FakeRegistryStore();
        var platform = new WindowsClientLoginStartPlatform(store, "TestValue");

        Equal(false, await platform.IsLoginStartEnabledAsync(), "disabled initially");

        // Caller supplies the stable launch command - #41 never resolves a packaged path itself.
        var command = WindowsClientLoginStartPlatform.BuildLaunchCommand(@"C:\Program Files\PSM\Client.exe", "--minimized");
        Equal("\"C:\\Program Files\\PSM\\Client.exe\" --minimized", command, "launch command quotes a spaced path");

        await platform.SetLoginStartAsync(true, command);
        Equal(true, await platform.IsLoginStartEnabledAsync(), "enabled after set");
        Equal(command, store.Values["TestValue"], "value written to the injected store");

        await platform.SetLoginStartAsync(false, command);
        Equal(false, await platform.IsLoginStartEnabledAsync(), "disabled after clear");
        True(!store.Values.ContainsKey("TestValue"), "value removed");

        Throws<ArgumentException>(
            () => WindowsClientLoginStartPlatform.BuildLaunchCommand("C:\\Bad\"Path.exe"),
            "an embedded quote must be rejected");
    }

    // ------------------------------------------------- shell integration

    private sealed class RecordingLauncher : IShellProcessLauncher
    {
        public List<string> Launched { get; } = new();

        public void LaunchFolder(string localDirectoryPath) => Launched.Add(localDirectoryPath);
    }

    public static async Task TestShellIntegrationOpensOnlyLocalDirectoriesAndNeverSpawns()
    {
        var launcher = new RecordingLauncher();
        var shell = new WindowsClientShellIntegration(launcher);

        var temp = Path.Combine(Path.GetTempPath(), "psm-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            await shell.OpenLocalFolderAsync(temp);
            Equal(1, launcher.Launched.Count, "exactly one launch recorded");
            Equal(Path.GetFullPath(temp), launcher.Launched[0], "the exact local directory is passed through");

            // A remote/UNC path is never "locally openable".
            await AssertThrowsAsync<ArgumentException>(() => shell.OpenLocalFolderAsync(@"\\server\share\data"),
                "a UNC path must be rejected");
            await AssertThrowsAsync<DirectoryNotFoundException>(
                () => shell.OpenLocalFolderAsync(Path.Combine(temp, "does-not-exist")),
                "a missing directory must be rejected");

            Equal(1, launcher.Launched.Count, "no launch occurred for the rejected paths");
        }
        finally { Directory.Delete(temp, true); }
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string what) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        catch (Exception ex) { throw new Exception($"{what}: expected {typeof(TException).Name} but got {ex.GetType().Name}"); }
        throw new Exception($"{what}: expected {typeof(TException).Name} but nothing was thrown");
    }
}
