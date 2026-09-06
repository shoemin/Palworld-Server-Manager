using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

// Explicit opt-in only. Real SCM service, real virtual account, real non-admin logons and DPAPI.
// Every OS object has a fresh trial prefix; cleanup failure fails the invocation.
public static partial class WindowsIntegration
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args[0] == "--windows-service" && args.Length == 4)
        {
            using var lifetime = new WindowsHostServiceLifetime(args[1], ct =>
            {
                var runtime = HostServiceRuntime.Start(new HostDataRoot(args[2]), ct, args[3]);
                try
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    var secrets = new WindowsSecureCredentialStore(args[2], identity.User!);
                    var expected = Encoding.UTF8.GetBytes("SYNTHETIC-SERVICE-SECRET-4bf5");
                    var previous = secrets.RetrieveAsync("service-integration").GetAwaiter().GetResult();
                    if (previous is null) secrets.StoreAsync("service-integration", expected).GetAwaiter().GetResult();
                    else Check(previous.SequenceEqual(expected), "Service credential changed across restart.");
                    var offline = secrets.RetrieveAsync("offline-integration").GetAwaiter().GetResult();
                    if (offline is not null) Check(offline.SequenceEqual(new byte[] { 8, 6, 4, 2 }), "Service cannot read offline-written credential.");
                    // A recycled PID must not make an earlier process's readiness look current.
                    File.Delete(Path.Combine(args[2], "tls-ready.json"));
                    File.Delete(Path.Combine(args[2], "tls-started.txt"));
                    File.WriteAllText(Path.Combine(args[2], "secure-store.txt"), offline is null ? "SERVICE PASS" : "OFFLINE PASS");
                    File.WriteAllText(Path.Combine(args[2], "identity.txt"), identity.User!.Value + "\n" + Environment.ProcessId);
                    return new NativeTlsServiceFixture(args[1], args[2], runtime, ct);
                }
                catch { runtime.Dispose(); throw; }
            });
            ServiceBase.Run(lifetime); return 0;
        }
        if (args[0] == "--windows-user" && args.Length == 6)
        {
            try { await UserProbe(args[1], args[2], args[3], args[4]); File.WriteAllText(args[5], "PASS"); return 0; }
            catch (Exception ex) { File.WriteAllText(args[5], ex.GetType().Name + ": " + ex.Message); return 1; }
        }
        if (args.Length != 1 || args[0] != "--windows-integration") throw new ArgumentException("Unknown Windows integration invocation.");
        using var admin = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(admin).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("FIELD EVIDENCE REQUIRED: explicit Windows integration requires an elevated Administrator token; no test was skipped as PASS.");
        await NativeTlsCacheTests.Lifecycle();
        Console.WriteLine("PASS integration: native TLS cache authority, nonexportability, reopen and retirement");
        await Suite(); return 0;
    }
    private static async Task UserProbe(string action, string serviceName, string credentialPath, string hostRoot)
    {
        using var identity = WindowsIdentity.GetCurrent();
        Check(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), "Probe token is Administrator.");
        Native.Check(Native.GetTokenInformation(identity.Token, 20, out var elevated, sizeof(int), out _));
        Check(elevated == 0, "Probe token is elevated.");
        var activation = new WindowsHostActivation(() => new WindowsHostActivation.ActivationService(serviceName));
        if (action is "authorized" or "unauthorized")
        {
            var result = await activation.RequestStartAsync();
            Check(result.Status == (action == "authorized" ? HostActivationStatus.StartRequested : HostActivationStatus.AccessDenied), "Unexpected non-admin activation result: " + result.Status);
            if (action == "authorized")
            {
                using var manager = Native.Manager(1);
                foreach (uint right in new uint[] { 0x20, 2, 0x10000, 0x20000, 0x40000, 0x80000, 0x40 })
                {
                    using var handle = Native.OpenService(manager, serviceName, right);
                    Check(handle.IsInvalid && Marshal.GetLastWin32Error() == 5, "Forbidden service right was granted: " + right);
                }
                try { using var file = File.OpenRead(Path.Combine(hostRoot, "host.db")); throw new Exception("Activation member read Host state."); }
                catch (UnauthorizedAccessException) { }
            }
        }
        else if (action == "ipc-authorized")
        {
            Check(await LocalIpcSpike.RequestAsync(serviceName, credentialPath) == identity.User!.Value, "Native SID did not match real OS user.");
            await LocalIpcSpike.RejectWrongPinAsync(serviceName);
        }
        else if (action == "ipc-denied") await LocalIpcSpike.RejectTransportAsync(serviceName);
        else if (action is "trusted-ipc" or "public-trust")
        {
            var serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", serviceName).Translate(typeof(SecurityIdentifier));
            var reader = new WindowsLocalHostTrustReader(hostRoot, serviceSid);
            var anchor = await reader.ReadAsync();
            if (action == "trusted-ipc")
                Check(await LocalTrustTests.Request(reader, credentialPath, anchor.HostId) == identity.User!.Value, "Pinned production channel did not retain the native user.");
            else
            {
                var path = Path.Combine(hostRoot, WindowsLocalHostTrustPublisher.DescriptorFileName);
                Check(File.ReadAllBytes(path).Length > 0, "Nonadmin could not read public trust.");
                foreach (var attempt in new Action[] { () => File.WriteAllBytes(path, new byte[] { 1 }), () => File.Delete(path) })
                {
                    try { attempt(); throw new Exception("Nonadmin modified public Host trust."); }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        else if (action == "native-key-denied")
        {
            try
            {
                using var native = CngKey.Open(serviceName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey | CngKeyOpenOptions.Silent);
                throw new Exception("Non-admin opened Host native private key.");
            }
            catch (CryptographicException) { }
            foreach (var attempt in new Action[] {
                () => { using var file = File.OpenRead(credentialPath); },
                () => File.WriteAllBytes(credentialPath, new byte[] { 1 }),
                () => File.Delete(credentialPath) })
            {
                try { attempt(); throw new Exception("Non-admin accessed native key file."); }
                catch (UnauthorizedAccessException) { }
            }
        }
        else if (action == "credential-create")
        {
            await ClientCredentialCeremonyTests.IntegrationProbe(credentialPath, complete: false);
        }
        else if (action == "credential-confirm") await ClientCredentialCeremonyTests.IntegrationProbe(credentialPath, complete: true);
        else if (action == "host-credential-denied")
        {
            foreach (var attempt in new Action[] {
                () => { using var file = File.OpenRead(credentialPath); },
                () => File.WriteAllBytes(credentialPath, new byte[] { 1 }),
                () => File.Delete(credentialPath) })
            {
                try { attempt(); throw new Exception("Non-admin accessed Host encrypted credential."); }
                catch (UnauthorizedAccessException) { }
            }
        }
        else if (action == "credential-denied")
        {
            var bytes = File.ReadAllBytes(credentialPath); // file readable, so this proves DPAPI isolation
            try { ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser); throw new Exception("User B decrypted user A credential."); }
            catch (CryptographicException) { }
            try { await new WindowsLocalPrincipalCredentialStore(new FakeKeys(), credentialPath).LoadAsync(); throw new Exception("User B retrieved user A credential."); }
            catch (CryptographicException) { }
            try { await new WindowsLocalPrincipalCredentialStore(new WindowsLocalPrincipalCryptography(), credentialPath).PrepareAsync(ClientCredentialCeremonyTests.IntegrationRotation); throw new Exception("User B retrieved user A pending key."); }
            catch (CryptographicException) { }
        }
        else if (action == "product-client") await ProductClientProbe(serviceName, credentialPath);
        else if (action == "offline-denied") await OfflineDeniedProbe(serviceName, credentialPath, hostRoot);
        else if (action.StartsWith("handoff-", StringComparison.Ordinal)) await HandoffProbe(action, serviceName, credentialPath, hostRoot);
        else throw new ArgumentException("Unknown probe action.");
    }
    private static async Task Suite()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var service = "PSMAstra" + suffix; var group = "PSMAstraG" + suffix;
        var userA = "psma" + suffix; var userB = "psmb" + suffix;
        var password = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var baseRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        var root = Path.GetFullPath(Path.Combine(baseRoot, "PSM Astra Test " + suffix));
        Check(root.StartsWith(baseRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Unsafe test root.");
        var hostRoot = Path.Combine(root, "Host"); var mutex = @"Global\" + service;
        var publicDirectory = Path.Combine(root, "PublicTrust");
        var platform = new WindowsHostPlatform(service, group, hostRoot);
        var tlsHostId = Guid.NewGuid();
        SecurityIdentifier? serviceSid = null;
        bool nativeGrantAdded = false;
        string? staleNativeName = null;
        bool installed = false, aCreated = false, bCreated = false;
        string? sidA = null, sidB = null;
        var cleanupErrors = new List<Exception>();
        try
        {
            Directory.CreateDirectory(root);
            Grant(root, new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute);
            var binaries = Path.Combine(root, "Service Binaries");
            CopyDirectory(AppContext.BaseDirectory, binaries);
            var executable = Path.Combine(binaries, "PalworldServerManager.SelfTest.exe");
            await platform.InstallForServiceAsync(executable, ["--windows-service", service, hostRoot, mutex]);
            installed = true;
            var descriptor = new RawSecurityDescriptor(platform.ReadServiceSecurityDescriptor(), 0);
            var groupSid = (SecurityIdentifier)new NTAccount(Environment.MachineName, group).Translate(typeof(SecurityIdentifier));
            serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", service).Translate(typeof(SecurityIdentifier));
            using (var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, mutex))
            {
                Check(lease is not null, "Native provisioning lacks offline lease.");
                nativeGrantAdded = WindowsNativeTlsProvisioning.EnsureCreatePermission(serviceSid);
                Check(!WindowsNativeTlsProvisioning.EnsureCreatePermission(serviceSid), "Native provisioning was not idempotent.");
                WindowsLocalHostTrustPublisher.Provision(publicDirectory, serviceSid);
                var publicPath = Path.Combine(publicDirectory, WindowsLocalHostTrustPublisher.DescriptorFileName);
                var missingTarget = Path.Combine(publicDirectory, "missing-target.json");
                File.CreateSymbolicLink(publicPath, missingTarget);
                try
                {
                    await SecureStoreTests.Reject<PalworldServerManager.Client.Platform.Contracts.LocalHostAuthenticationException>(() => new WindowsLocalHostTrustReader(publicDirectory, serviceSid).ReadAsync());
                    await SecureStoreTests.Reject<IOException>(() => new WindowsLocalHostTrustPublisher(publicDirectory, serviceSid).PublishAsync(new(tlsHostId, new string('A', 64))));
                }
                finally { File.Delete(publicPath); }
                Check(!File.Exists(missingTarget), "Rejected trust symlink touched its destination.");
            }
            File.WriteAllText(Path.Combine(hostRoot, "tls-config.json"), JsonSerializer.Serialize(new NativeTlsServiceFixture.Config(tlsHostId, groupSid.Value, publicDirectory)));
            var aces = descriptor.DiscretionaryAcl!.Cast<CommonAce>().ToArray();
            Check(aces.Length == 3 && aces.Single(a => a.SecurityIdentifier == groupSid).AccessMask == 0x14, "SCM DACL read-back failed.");
            Check(!await platform.IsEnabledAsync(), "Default boot-start not Manual.");
            await platform.SetEnabledAsync(true); Check(await platform.IsEnabledAsync(), "Automatic boot-start failed.");
            await platform.SetEnabledAsync(false); Check(!await platform.IsEnabledAsync(), "Manual boot-start failed.");
            Console.WriteLine("PASS integration: provision virtual-account service, exact DACL, Manual/Automatic/Manual");
            await platform.StartAsync();
            var firstTls = await NativeTlsServiceFixture.WaitReady(hostRoot);
            var identity = File.ReadAllLines(Path.Combine(hostRoot, "identity.txt"));
            Check(identity[0] == serviceSid.Value, "Service is not running as intended virtual account.");
            AssertLockDenied(mutex);
            Check(File.Exists(Path.Combine(hostRoot, "host.db")), "SCM workload did not open #40 database.");
            await platform.StopAsync(); AssertLockAvailable(mutex);
            string hostCredential;
            using (var offlineLease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, mutex))
            {
                Check(offlineLease is not null, "Offline store caller lacks exclusive lease.");
                var secrets = new WindowsSecureCredentialStore(hostRoot, serviceSid);
                var cache = new WindowsHostTlsCredentialCache(tlsHostId, serviceSid, secrets);
                using (var offlineCertificate = await cache.LoadAsync("tls-current"))
                using (var offlineKey = (ECDsaCng)offlineCertificate.GetECDsaPrivateKey()!)
                    Check(offlineKey.Key.KeyName == firstTls.KeyName, "Elevated offline caller did not reopen service-created key.");
                var pfx = await secrets.RetrieveAsync("tls-current") ?? throw new Exception("Missing TLS fixture authority.");
                try { await secrets.StoreAsync("tls-stale", pfx); }
                finally { CryptographicOperations.ZeroMemory(pfx); }
                using (var stale = await cache.LoadAsync("tls-stale"))
                using (var staleKey = (ECDsaCng)stale.GetECDsaPrivateKey()!) staleNativeName = staleKey.Key.KeyName!;
                Check((await secrets.RetrieveAsync("service-integration"))!.SequenceEqual(Encoding.UTF8.GetBytes("SYNTHETIC-SERVICE-SECRET-4bf5")), "Elevated offline caller could not read service-created blob.");
                await secrets.StoreAsync("offline-integration", new byte[] { 8, 6, 4, 2 });
                await SecureCredentialStoreContractTests.Run(() => new WindowsSecureCredentialStore(hostRoot, serviceSid));
                hostCredential = Directory.GetFiles(Path.Combine(hostRoot, "credentials"), "*.bin").First();
                var beforeLink = Directory.GetFiles(Path.Combine(hostRoot, "credentials"), "*.bin");
                await secrets.StoreAsync("reparse-test", new byte[] { 1 });
                var link = Directory.GetFiles(Path.Combine(hostRoot, "credentials"), "*.bin").Except(beforeLink).Single();
                File.Delete(link); File.CreateSymbolicLink(link, hostCredential);
                await SecureStoreTests.Reject<IOException>(() => secrets.RetrieveAsync("reparse-test"));
                await SecureStoreTests.Reject<IOException>(() => secrets.DeleteAsync("reparse-test"));
                File.Delete(link);
                Check(File.Exists(hostCredential), "Rejected symlink delete touched destination.");
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(hostRoot, "host.db"))).Contains("SYNTHETIC-SERVICE-SECRET-4bf5"), "Host database contains private secret.");
                // Update-style executable replacement at the same service path; not a release/package test.
                File.Copy(executable, executable + ".update-test");
                File.Move(executable + ".update-test", executable, true);
            }
            await platform.StartAsync();
            var secondTls = await NativeTlsServiceFixture.WaitReady(hostRoot);
            Check(secondTls.KeyName == firstTls.KeyName && secondTls.Pin == firstTls.Pin, "Restart changed native key or machine identity.");
            Check(!CngKey.Exists(staleNativeName!, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey), "Service startup retained stale native key.");
            Check(File.ReadAllText(Path.Combine(hostRoot, "secure-store.txt")) == "OFFLINE PASS", "Restarted service did not validate offline caller value.");
            identity = File.ReadAllLines(Path.Combine(hostRoot, "identity.txt"));
            using (var process = Process.GetProcessById(int.Parse(identity[1]))) { process.Kill(); Check(process.WaitForExit(30000), "Killed service did not exit."); }
            using (var controller = new ServiceController(service, ".")) controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            AssertLockAvailable(mutex);
            Console.WriteLine("PASS integration: virtual identity, database, lock contention, normal stop and abnormal termination");
            Native.CreateUser(userA, password); aCreated = true; sidA = Sid(userA);
            Native.CreateUser(userB, password); bCreated = true; sidB = Sid(userB);
            Native.AddMember(group, userA);
            var shared = Path.Combine(root, "Probe Results"); Directory.CreateDirectory(shared);
            Grant(shared, new SecurityIdentifier(sidA), FileSystemRights.FullControl);
            Grant(shared, new SecurityIdentifier(sidB), FileSystemRights.FullControl);
            var credential = Path.Combine(shared, "credential.bin");
            RunUser(executable, userB, password, "unauthorized", service, credential, hostRoot, shared);
            RunUser(executable, userA, password, "authorized", service, credential, hostRoot, shared);
            using (var controller = new ServiceController(service, ".")) controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            var activeTls = await NativeTlsServiceFixture.WaitReady(hostRoot);
            Check(activeTls.KeyName == firstTls.KeyName && activeTls.Pin == firstTls.Pin, "Abnormal restart changed machine credential.");
            RunUser(executable, userB, password, "ipc-denied", activeTls.Pipe, activeTls.Pin, hostRoot, shared);
            RunUser(executable, userA, password, "ipc-authorized", activeTls.Pipe, activeTls.Pin, hostRoot, shared);
            RunUser(executable, userA, password, "public-trust", service, activeTls.Pipe, publicDirectory, shared);
            RunUser(executable, userB, password, "public-trust", service, activeTls.Pipe, publicDirectory, shared);
            RunUser(executable, userA, password, "trusted-ipc", service, activeTls.Pipe, publicDirectory, shared);
            Console.WriteLine("PASS integration: service publishes protected public descriptor; two nonadmins read but cannot write/delete; client authenticates production local TLS");
            RunUser(executable, userA, password, "native-key-denied", activeTls.KeyName, activeTls.KeyFile, hostRoot, shared);
            RunUser(executable, userB, password, "native-key-denied", activeTls.KeyName, activeTls.KeyFile, hostRoot, shared);
            await platform.StopAsync();
            using (var offlineLease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, mutex))
            {
                Check(offlineLease is not null, "Native cache retirement lacks offline lease.");
                var cache = new WindowsHostTlsCredentialCache(tlsHostId, serviceSid, new WindowsSecureCredentialStore(hostRoot, serviceSid));
                await cache.ReconcileAsync([]);
                Check(!CngKey.Exists(firstTls.KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey), "Native key survived retirement.");
                using var rebuilt = await cache.LoadAsync("tls-current");
                Check(Convert.ToHexString(SHA256.HashData(rebuilt.RawData)) == firstTls.Pin, "Offline cache reconstruction changed identity.");
            }
            Console.WriteLine("PASS integration: actual service native TLS, normal/crash restart, offline reopen/rebuild/retirement, nonadmin provider and file denial");
            RunUser(executable, userA, password, "credential-create", service, credential, hostRoot, shared);
            RunUser(executable, userB, password, "credential-denied", service, credential, hostRoot, shared);
            RunUser(executable, userA, password, "credential-confirm", service, credential, hostRoot, shared);
            RunUser(executable, userB, password, "credential-denied", service, credential, hostRoot, shared);
            RunUser(executable, userA, password, "host-credential-denied", service, hostCredential, hostRoot, shared);
            RunUser(executable, userB, password, "host-credential-denied", service, hostCredential, hostRoot, shared);
            Console.WriteLine("PASS integration: service/elevated-offline credential interoperability, restart, real nonadmin read/write/delete denial");
            Console.WriteLine("PASS integration: real non-admin query/start, forbidden rights, unauthorized start denied, Host-data denied, cross-user DPAPI");
            await using (var spike = await LocalIpcSpike.StartAsync(groupSid))
            {
                RunUser(executable, userB, password, "ipc-denied", spike.PipeName, spike.PublicPin, hostRoot, shared);
                RunUser(executable, userA, password, "ipc-authorized", spike.PipeName, spike.PublicPin, hostRoot, shared);
                Native.AddMember(group, userB); // New logon must pick up membership; no principal authority is created.
                RunUser(executable, userB, password, "ipc-authorized", spike.PipeName, spike.PublicPin, hostRoot, shared);
                Check(spike.ObservedSids.Count == 2 && spike.ObservedSids.Contains(sidA!) && spike.ObservedSids.Contains(sidB!), "Two native users did not remain distinct, or rejected TLS delivered a request.");
                Console.WriteLine("PASS integration: Kestrel named-pipe TLS, group denial, two distinct native SIDs, wrong-pin requests never delivered");
            }
            await HandoffSuite(tlsHostId, root, executable, userA, userB, password, sidA!, shared);
            await OfflineSuite(platform, service, group, root, executable, userA, userB, password, sidA!, sidB!, shared, serviceSid);
            await ProductionHostSuite(binaries, sidA!, userA, userB, password, sidB!, shared);
            await platform.UninstallAsync(); installed = false;
            Check(File.Exists(Path.Combine(hostRoot, "host.db")), "Uninstall removed authoritative database.");
            Check(Sid(group) == groupSid.Value, "Uninstall removed activation group.");
            Console.WriteLine("PASS integration: uninstall preserves Host data and group");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Windows integration primary failure: " + ex);
            foreach (var reference in new[] { "tls-current", "tls-stale" })
            {
                var name = "PalworldServerManager.HostTls.v1." + tlsHostId.ToString("N") + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference)));
                if (!CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey)) continue;
                using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey);
                Console.WriteLine("Fixture native descriptor: " + new RawSecurityDescriptor(key.GetProperty("Security Descr", (CngPropertyOptions)5).GetValue()!, 0).GetSddlForm(AccessControlSections.Owner | AccessControlSections.Access));
                var file = new FileInfo(Path.Combine(baseRoot, "Microsoft", "Crypto", "Keys", key.UniqueName!));
                Console.WriteLine("Fixture key-file descriptor: " + file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access));
            }
            throw;
        }
        finally
        {
            void Cleanup(Action action) { try { action(); } catch (Exception ex) { cleanupErrors.Add(ex); } }
            if (installed) Cleanup(() => { platform.StopAsync().GetAwaiter().GetResult(); platform.UninstallAsync().GetAwaiter().GetResult(); });
            if (serviceSid is not null) Cleanup(() =>
            {
                using var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, mutex);
                Check(lease is not null, "Native cleanup lacks machine lease.");
                new WindowsHostTlsCredentialCache(tlsHostId, serviceSid, new WindowsSecureCredentialStore(hostRoot, serviceSid)).ReconcileAsync([]).GetAwaiter().GetResult();
            });
            if (serviceSid is not null) Cleanup(() =>
            {
                using var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, mutex);
                Check(lease is not null, "Fixture fallback cleanup lacks offline lease.");
                var requiredFallback = false;
                foreach (var reference in new[] { "tls-current", "tls-stale" })
                {
                    var name = "PalworldServerManager.HostTls.v1." + tlsHostId.ToString("N") + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference)));
                    if (!CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey)) continue;
                    using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey);
                    key.Delete();
                    Check(!CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey), "Rejected fixture key survived elevated cleanup.");
                    requiredFallback = true;
                    Console.WriteLine("Fixture-only elevated cleanup removed rejected native container; production reconciliation remains FAILED.");
                }
                Check(!requiredFallback, "Native reconciliation left a fixture key; fallback cleanup cannot convert this to PASS.");
            });
            if (nativeGrantAdded && serviceSid is not null) Cleanup(() =>
            {
                using var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, mutex);
                Check(lease is not null, "Native provisioning cleanup lacks offline lease.");
                WindowsNativeTlsProvisioning.RemoveCreatePermission(serviceSid);
                var directory = new DirectoryInfo(Path.Combine(baseRoot, "Microsoft", "Crypto", "Keys"));
                Check(!directory.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                    .Any(rule => rule.IdentityReference == serviceSid), "Unique native directory grant survived cleanup.");
            });
            // A failed install may have created the uniquely named group before its failure.
            if (platform.ActivationGroupCreated)
                Cleanup(() => { var code = Native.NetLocalGroupDel(null, group); if (code != 0) throw new Win32Exception((int)code); });
            if (aCreated) Cleanup(() => Native.RemoveUser(userA, sidA));
            if (bCreated) Cleanup(() => Native.RemoveUser(userB, sidB));
            Cleanup(() =>
            {
                var resolved = Path.GetFullPath(root);
                Check(resolved == Path.Combine(baseRoot, "PSM Astra Test " + suffix), "Cleanup path escaped unique test directory.");
                if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
            });
            if (cleanupErrors.Count > 0) throw new AggregateException("Windows integration cleanup FAILED.", cleanupErrors);
            Console.WriteLine("PASS integration cleanup: unique service/group/users/profiles/files removed");
        }
    }
    private static void AssertLockDenied(string mutex)
    {
        try { using var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, mutex); Check(lease is null, "Second writer acquired service lock."); }
        catch (InvalidOperationException ex) when (ex.InnerException is UnauthorizedAccessException) { /* kernel denied contender */ }
    }
    private static void AssertLockAvailable(string mutex)
    { using var lease = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(2), mutex); Check(lease is not null, "Service failed to release mutex."); }
    private static string Sid(string account) => ((SecurityIdentifier)new NTAccount(Environment.MachineName, account).Translate(typeof(SecurityIdentifier))).Value;
    private static void Grant(string directory, SecurityIdentifier sid, FileSystemRights rights)
    {
        var info = new DirectoryInfo(directory); var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
    private static void RunUser(string executable, string user, string password, string action, string service, string credential, string hostRoot, string results)
    {
        var output = Path.Combine(results, action + ".txt");
        var command = new StringBuilder(WindowsClientCommandLine.Build(new ClientLaunchTarget(executable, ["--windows-user", action, service, credential, hostRoot, output])));
        var startup = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>(), Flags = 1, ShowWindow = 0 };
        Native.Check(Native.CreateProcessWithLogonW(user, Environment.MachineName, password, 1, executable, command, 0x08000000, IntPtr.Zero, results, ref startup, out var process));
        try
        {
            var wait = Native.WaitForSingleObject(process.Process, 60000);
            if (wait != 0) { Native.TerminateProcess(process.Process, 1); Native.WaitForSingleObject(process.Process, 10000); throw new System.TimeoutException("Non-admin integration helper timed out."); }
            Native.Check(Native.GetExitCodeProcess(process.Process, out var code));
            Check(code == 0 && File.Exists(output) && File.ReadAllText(output) == "PASS", "User probe failed: " + action + "; " + (File.Exists(output) ? File.ReadAllText(output) : "No result file; exit " + code));
        }
        finally { Native.CloseHandle(process.Thread); Native.CloseHandle(process.Process); }
    }
    private static class Native
    {
        internal sealed class Handle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
        { public Handle() : base(true) { } protected override bool ReleaseHandle() => CloseServiceHandle(handle); }
        internal static void Check(bool value) { if (!value) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        internal static Handle Manager(uint access) { var result = OpenSCManager(null, null, access); if (result.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); return result; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern Handle OpenSCManager(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern Handle OpenService(Handle manager, string service, uint access);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returned);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct UserInfo
        { public string Name; public string Password; public uint Age; public uint Privilege; public string? Home; public string? Comment; public uint Flags; public string? Script; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MemberInfo { public string Name; }
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern uint NetUserAdd(string? server, uint level, ref UserInfo info, out uint error);
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern uint NetUserDel(string? server, string user);
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] internal static extern uint NetLocalGroupDel(string? server, string group);
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern uint NetLocalGroupAddMembers(string? server, string group, uint level, ref MemberInfo member, uint count);
        [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool DeleteProfile(string sid, string? profile, string? computer);
        internal static void CreateUser(string name, string password)
        { var info = new UserInfo { Name = name, Password = password, Privilege = 1, Flags = 0x10001 }; var result = NetUserAdd(null, 1, ref info, out _); if (result != 0) throw new Win32Exception((int)result); }
        internal static void AddMember(string group, string user)
        { var info = new MemberInfo { Name = Environment.MachineName + "\\" + user }; var result = NetLocalGroupAddMembers(null, group, 3, ref info, 1); if (result != 0) throw new Win32Exception((int)result); }
        internal static void RemoveUser(string user, string? sid)
        {
            // Delete only the profile created by these temporary logons; missing profile is fine.
            Exception? profileError = null;
            if (sid is not null && !DeleteProfile(sid, null, null) && Marshal.GetLastWin32Error() is not 2 and not 3)
                profileError = new Win32Exception(Marshal.GetLastWin32Error());
            var result = NetUserDel(null, user); if (result != 0) throw new Win32Exception((int)result);
            if (profileError is not null) throw profileError;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
        {
            public int Size; public string? Reserved; public string? Desktop; public string? Title;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public ushort ShowWindow, ReservedCount; public IntPtr ReservedBytes, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateProcessWithLogonW(string username, string domain, string password, uint logonFlags, string application, StringBuilder command, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
    }
}

