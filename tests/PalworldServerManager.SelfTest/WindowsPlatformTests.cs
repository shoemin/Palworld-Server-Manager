using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

public static class WindowsPlatformTests
{
    internal static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static string TemporaryRoot()
    { var path = Path.Combine(Path.GetTempPath(), "psm-astra-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    internal sealed class FakeKeys : ILocalPrincipalKeyGenerator
    {
        private int _sequence;
        public LocalPrincipalKeyPair Generate()
        {
            var id = Interlocked.Increment(ref _sequence);
            return new(System.Text.Encoding.UTF8.GetBytes("FAKE PUBLIC " + id), System.Text.Encoding.UTF8.GetBytes("FAKE PRIVATE TEST MATERIAL " + id));
        }
    }
    private sealed class FakeService(ServiceControllerStatus status, int error = 0) : IWindowsActivationService
    {
        public int Starts;
        public ServiceControllerStatus Query() { if (error != 0) throw new InvalidOperationException("native", new Win32Exception(error)); return status; }
        public void Start() => Starts++;
        public void Dispose() { }
    }
    public static async Task Activation()
    {
        foreach (var (state, expected, starts) in new[] {
            (ServiceControllerStatus.Running, HostActivationStatus.AlreadyRunning, 0),
            (ServiceControllerStatus.StartPending, HostActivationStatus.StartRequested, 0),
            (ServiceControllerStatus.Stopped, HostActivationStatus.StartRequested, 1),
            (ServiceControllerStatus.StopPending, HostActivationStatus.Failed, 0) })
        {
            var fake = new FakeService(state); var activation = new WindowsHostActivation(() => fake);
            Check((await activation.RequestStartAsync()).Status == expected && fake.Starts == starts, "Activation mapping/start mismatch.");
        }
        foreach (var (error, expected) in new[] { (5, HostActivationStatus.AccessDenied), (1060, HostActivationStatus.ServiceMissing), (1058, HostActivationStatus.Failed) })
            Check((await new WindowsHostActivation(() => new FakeService(ServiceControllerStatus.Stopped, error)).RequestStartAsync()).Status == expected, "Native error mapping.");
        var stopped = new FakeService(ServiceControllerStatus.Stopped);
        Check((await new WindowsHostActivation(() => stopped).IsHostRunningAsync()).Status == HostActivationStatus.Stopped && stopped.Starts == 0, "Query started service.");
    }
    public static Task SecurityPolicy()
    {
        var sid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var descriptor = new RawSecurityDescriptor(WindowsHostPlatform.BuildServiceDacl(sid));
        var aces = descriptor.DiscretionaryAcl!.Cast<CommonAce>().ToArray();
        Check(aces.Length == 3 && aces.Single(a => a.SecurityIdentifier == sid).AccessMask == 0x14, "Delegated service rights are not exact.");
        Check(aces.Where(a => a.SecurityIdentifier != sid).All(a => a.AccessMask == 0xF01FF), "Maintenance rights lost.");
        var directory = WindowsHostPlatform.BuildHostDirectoryAcl(sid);
        Check(directory.AreAccessRulesProtected, "Host root inherited access.");
        var rules = directory.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Check(rules.Length == 3 && rules.All(r => r.FileSystemRights == FileSystemRights.FullControl), "Unexpected Host ACL.");
        Check(WindowsHostPlatform.QuoteExecutable(@"C:\Program Files\PSM\Host.exe") == "\"C:\\Program Files\\PSM\\Host.exe\"", "Unquoted service path.");
        Check(new WindowsHostPlatform().GetHostDataRoot() == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PalworldServerManager", "Host"), "Wrong machine root.");
        return Task.CompletedTask;
    }
    public static Task ExistingStateAcl()
    {
        var service = new SecurityIdentifier("S-1-5-80-1-2-3-4-5");
        var administrator = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var outsider = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        FileSecurity Acl(bool protectedAcl, bool grantService)
        {
            var acl = new FileSecurity(); acl.SetOwner(administrator); acl.SetAccessRuleProtection(protectedAcl, false);
            acl.AddAccessRule(new FileSystemAccessRule(administrator, FileSystemRights.FullControl, AccessControlType.Allow));
            if (grantService) acl.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.FullControl, AccessControlType.Allow));
            return acl;
        }
        void Rejected(FileSecurity acl)
        {
            try { WindowsHostPlatform.ValidateExistingStateAcl(acl, service); throw new Exception("Unsafe/unusable existing ACL accepted."); }
            catch (UnauthorizedAccessException) { }
        }
        Rejected(Acl(true, false)); // reviewer regression: protected administrator-only database
        var denied = Acl(true, true); denied.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.WriteData, AccessControlType.Deny)); Rejected(denied);
        var groupDeny = Acl(false, true); groupDeny.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.WriteData, AccessControlType.Deny)); Rejected(groupDeny);
        var wrongOwner = Acl(true, true); wrongOwner.SetOwner(outsider); Rejected(wrongOwner);
        var broad = Acl(true, true); broad.AddAccessRule(new FileSystemAccessRule(outsider, FileSystemRights.ReadData, AccessControlType.Allow)); Rejected(broad);
        WindowsHostPlatform.ValidateExistingStateAcl(Acl(true, true), service);
        WindowsHostPlatform.ValidateExistingStateAcl(Acl(false, false), service); // root propagation supplies service rights
        var directory = WindowsHostPlatform.BuildHostDirectoryAcl(service);
        WindowsHostPlatform.ValidateExistingStateAcl(directory, service);
        return Task.CompletedTask;
    }
    private sealed class FakeRegistry : ILoginStartRegistry
    { public string? Value; public string? Read() => Value; public void Write(string command) => Value = command; public void Delete() => Value = null; }
    public static async Task LoginStart()
    {
        var registry = new FakeRegistry(); var platform = new WindowsClientLoginStart(registry);
        var target = new ClientLaunchTarget(@"C:\Program Files\PSM\Client.exe", ["--path", @"C:\has space\", "say \"hello\""]);
        await platform.SetEnabledAsync(true, target);
        Check(registry.Value == "\"C:\\Program Files\\PSM\\Client.exe\" \"--path\" \"C:\\has space\\\\\" \"say \\\"hello\\\"\"", "Login command escapes differ.");
        Check(await platform.IsEnabledAsync(), "Login not enabled.");
        await platform.SetEnabledAsync(false, target); Check(!await platform.IsEnabledAsync(), "Login not disabled.");
    }
    private sealed class FakeLauncher : ILocalDirectoryLauncher { public int Count; public void Open(string directory) => Count++; }
    public static async Task Shell()
    {
        var root = TemporaryRoot();
        try
        {
            var launcher = new FakeLauncher(); var shell = new WindowsClientShellIntegration(root, launcher);
            await shell.OpenClientDiagnosticsAsync(); Check(launcher.Count == 1, "Expected injected launcher.");
            foreach (var path in new[] { @"\\server\share", "https://example.com", "cmd.exe /c echo bad", @"\\?\C:\Windows" })
            {
                try { await shell.OpenAuthorizedLocalDirectoryAsync(path); throw new Exception("Unsafe path accepted."); }
                catch (ArgumentException) { }
            }
            Check(launcher.Count == 1, "Rejected paths launched.");
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task CredentialLifecycle()
    {
        var root = TemporaryRoot();
        try
        {
            var path = Path.Combine(root, "principal.bin"); var generator = new FakeKeys();
            var store = new WindowsLocalPrincipalCredentialStore(generator, path);
            Check(!await store.HasCredentialAsync() && await store.LoadAsync() is null, "Absent lifecycle.");
            var first = await store.CreateAndStoreAsync();
            Check(first.PublicKey.SequenceEqual((await store.CreateAndStoreAsync()).PublicKey), "Unbound retry replaced key.");
            Check(!await store.HasCredentialAsync() && await store.LoadAsync() is null, "Unbound exposed as credential.");
            var id = Guid.NewGuid(); await store.BindPrincipalIdAsync(id);
            Check(await store.HasCredentialAsync() && (await store.LoadAsync())!.LocalPrincipalId == id, "Binding failed.");
            var reopened = new WindowsLocalPrincipalCredentialStore(generator, path);
            Check((await reopened.LoadAsync())!.KeyPair.PrivateKey.SequenceEqual(first.PrivateKey), "Shared same-user/restart key differs.");
            var disk = File.ReadAllBytes(path);
            Check(!System.Text.Encoding.UTF8.GetString(disk).Contains("FAKE PRIVATE"), "Plaintext private key on disk.");
            Check(!System.Text.Encoding.UTF8.GetString(disk).Contains(Convert.ToBase64String(first.PrivateKey)), "Base64 plaintext on disk.");
            await reopened.DeleteAsync(); Check(await store.LoadAsync() is null, "Delete did not remove credential.");
            Check(!(await store.CreateAndStoreAsync()).PublicKey.SequenceEqual(first.PublicKey), "Delete/recreate reused key.");
            Check(typeof(ILocalPrincipalCredentialStore).GetMethods().All(m => !m.Name.Contains("Host")), "Host machine credential API leaked.");
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task CredentialWriteFailure()
    {
        var root = TemporaryRoot();
        try
        {
            var path = Path.Combine(root, "principal.bin"); var generator = new FakeKeys();
            var good = new WindowsLocalPrincipalCredentialStore(generator, path); var key = await good.CreateAndStoreAsync();
            var bytes = File.ReadAllBytes(path);
            var failing = new WindowsLocalPrincipalCredentialStore(generator, path, (_, _) => throw new IOException("Injected pre-commit failure."));
            try { await failing.BindPrincipalIdAsync(Guid.NewGuid()); throw new Exception("Failure not injected."); } catch (IOException) { }
            Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Last good state corrupted.");
            Check((await good.CreateAndStoreAsync()).PublicKey.SequenceEqual(key.PublicKey), "Retry changed key.");
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task ConcurrentCredentials()
    {
        var root = TemporaryRoot();
        try
        {
            var generator = new FakeKeys(); var path = Path.Combine(root, "principal.bin");
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => new WindowsLocalPrincipalCredentialStore(generator, path).CreateAndStoreAsync())));
            Check(results.All(r => r.PublicKey.SequenceEqual(results[0].PublicKey)), "Concurrent stores generated divergent credentials.");
        }
        finally { Directory.Delete(root, true); }
    }
    public static Task Runtime()
    {
        var root = TemporaryRoot(); var mutex = @"Global\PSM_Astra_" + Guid.NewGuid().ToString("N");
        try
        {
            using (var runtime = HostServiceRuntime.Start(new HostDataRoot(root), default, mutex))
            {
                Check(File.Exists(Path.Combine(root, "host.db")), "Runtime did not migrate database.");
                using var contender = HostExclusivityLock.TryAcquire(TimeSpan.Zero, mutex);
                Check(contender is null, "Second writer accepted.");
                try { using var duplicate = HostServiceRuntime.Start(new HostDataRoot(root), default, mutex); throw new Exception("Second runtime accepted."); }
                catch (InvalidOperationException) { }
            }
            using var reacquired = HostExclusivityLock.TryAcquire(TimeSpan.Zero, mutex);
            Check(reacquired is not null, "Stop did not release mutex.");
            using var exclusiveFile = new FileStream(Path.Combine(root, "host.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
