using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Win32.SafeHandles;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Cli;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static partial class WindowsIntegration
{
    // This test runs the shipped executable and its fixed composition. Never adopt an installed
    // product, and never install it on a developer workstation as part of ordinary self-tests.
    private static async Task ProductionHostSuite(string binaries, string intendedSid)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || Environment.GetEnvironmentVariable("RUNNER_OS") != "Windows")
            throw new InvalidOperationException("FIELD EVIDENCE REQUIRED: fixed-product integration requires a disposable GitHub Windows runner.");
        var location = OfflineHostLocation.Product(); var platform = new WindowsHostPlatform();
        var productRoot = Path.GetFullPath(Path.GetDirectoryName(location.HostRoot)!);
        var expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PalworldServerManager");
        Check(productRoot == expectedRoot && !Directory.Exists(productRoot) && !File.Exists(productRoot), "Product data already exists; fixture refuses adoption.");
        using (var manager = Native.Manager(1))
        using (var existing = Native.OpenService(manager, location.ServiceName, 4))
            Check(existing.IsInvalid && Marshal.GetLastWin32Error() == 1060, "Product service already exists or cannot be checked; fixture refuses adoption.");
        try { _ = Sid(location.ActivationGroup); throw new Exception("Product group already exists; fixture refuses adoption."); }
        catch (IdentityNotMappedException) { }
        var installed = false; var nativeGrantOwned = false; SecurityIdentifier? serviceSid = null; Guid hostId = Guid.Empty;
        var cleanup = new List<Exception>();
        async Task<string> Offline(int expected, params string[] args)
        {
            var start = new ProcessStartInfo(Path.Combine(binaries, "PalworldServerManager.Host.Cli.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
            if (process.ExitCode != expected)
            {
                // Fixture-only public ACL evidence; never read credential/handoff/database contents.
                foreach (var item in new[] { new DirectoryInfo(productRoot), new DirectoryInfo(location.HostRoot) })
                    if (item.Exists) Console.WriteLine("Product fixture ACL " + item.Name + ": " + item.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access));
                if (serviceSid is not null) platform.ValidateOfflineDataRoot(serviceSid);
                throw new Exception("Production offline executable failed: " + await error);
            }
            return await output;
        }
        async Task FailStartup()
        {
            using var controller = new System.ServiceProcess.ServiceController(location.ServiceName, ".");
            try { controller.Start(); } catch (Win32Exception ex) when (ex.NativeErrorCode is 1053 or 1067) { }
            catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1053 or 1067 }) { }
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (await platform.GetStateAsync() != HostServiceState.Stopped && DateTime.UtcNow < deadline) await Task.Delay(100);
            Check(await platform.GetStateAsync() == HostServiceState.Stopped, "Failed production startup left a running service.");
            AssertLockAvailable(location.MutexName);
            using var pipe = new NamedPipeClientStream(".", WindowsHostComposition.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(200); throw new Exception("Failed production startup exposed IPC."); }
            catch (TimeoutException) { }
        }
        async Task<int> Ready()
        {
            await platform.StartAsync();
            var reader = new WindowsLocalHostTrustReader(location.PublicTrustRoot, serviceSid!);
            using var client = new LocalSecurityRpcTests.Client(hostId, WindowsHostComposition.PipeName, reader);
            var hello = await client.Negotiate();
            Check(hello.Host.HostId == hostId.ToString("D") && !hello.Initialized, "Production service changed semantic identity or created Owner authority.");
            try { await client.Call<LocalPrincipalRequest, LocalChallenge>("IssueChallenge", new() { LocalPrincipalId = Guid.NewGuid().ToString("D") }); throw new Exception("Uninitialized production Host authorized a principal."); }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated) { }
            using var pipe = new NamedPipeClientStream(".", WindowsHostComposition.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000); Native.Check(ProductionGetPipePid(pipe.SafePipeHandle, out var pid));
            using var process = Process.GetProcessById((int)pid);
            Check(Path.GetFullPath(process.MainModule!.FileName) == Path.Combine(binaries, "PalworldServerManager.Host.exe"), "Listener is not the shipped Host executable.");
            using var netstat = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netstat.exe"), "-ano")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            var rows = await netstat.StandardOutput.ReadToEndAsync(); await netstat.WaitForExitAsync();
            Check(netstat.ExitCode == 0 && !rows.Split('\n').Select(row => row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Any(parts => parts.Length >= 5 && parts[0] == "TCP" && parts[^1] == pid.ToString()), "Production Host opened a TCP endpoint.");
            AssertLockDenied(location.MutexName); return (int)pid;
        }
        try
        {
            await platform.InstallAsync(Path.Combine(binaries, "PalworldServerManager.Host.exe")); installed = true;
            serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", location.ServiceName).Translate(typeof(SecurityIdentifier));
            var keyDirectory = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto", "Keys"));
            Check(!keyDirectory.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(rule => rule.IdentityReference == serviceSid), "Product native grant already exists; fixture refuses adoption.");
            var originalAcl = platform.ReadServiceSecurityDescriptor();
            await FailStartup(); // no privileged bootstrap/private credential
            nativeGrantOwned = true; // from this point any matching grant is created by our offline invocation
            using (var result = JsonDocument.Parse(await Offline(0, "bootstrap", "--owner-sid", intendedSid)))
                hostId = result.RootElement.GetProperty("hostId").GetGuid();
            var database = new HostDatabase(new HostDataRoot(location.HostRoot));
            var state = new HostCredentialStateRepository(database, hostId);
            var publisher = new WindowsLocalHostTrustPublisher(location.PublicTrustRoot, serviceSid);
            var store = new WindowsSecureCredentialStore(location.HostRoot, serviceSid);
            var cache = new WindowsHostTlsCredentialCache(hostId, serviceSid, store);
            var expected = HostTrustPlanning.Build(state.Read()).Publication!.CurrentFingerprint;
            using (var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero))
            {
                Check(lease is not null, "Stale-publication fixture lacks offline lease.");
                await publisher.PublishAsync(new(hostId, new string('A', 64)));
            }
            var first = await Ready();
            Check((await new WindowsLocalHostTrustReader(location.PublicTrustRoot, serviceSid).ReadAsync()).CurrentFingerprint == expected,
                "Production startup did not reconcile authoritative trust before RPC.");
            await Offline(1, "rotate-owner");
            await platform.StopAsync(); AssertLockAvailable(location.MutexName);
            var second = await Ready(); Check(second != first, "Normal restart reused a live service process.");
            using (var process = Process.GetProcessById(second)) { process.Kill(); await process.WaitForExitAsync(); }
            var stoppedDeadline = DateTime.UtcNow.AddSeconds(10);
            while (await platform.GetStateAsync() != HostServiceState.Stopped && DateTime.UtcNow < stoppedDeadline) await Task.Delay(100);
            var third = await Ready(); Check(third != second, "Crash restart did not replace the terminated process.");
            Check(platform.ReadServiceSecurityDescriptor().SequenceEqual(originalAcl), "Service startup changed the activation ACL.");
            await platform.StopAsync(); AssertLockAvailable(location.MutexName);
            using (var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero))
            {
                Check(lease is not null, "Mismatched-public-metadata fixture lacks offline lease.");
                using var connection = database.OpenConnection();
                try { HostDatabase.Execute(connection, "UPDATE SecureCredentialReferences SET PublicKeyFingerprint='" + new string('B', 64) + "' WHERE CredentialRef=(SELECT CurrentCredentialRef FROM HostIdentity);"); }
                finally { Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection); }
            }
            await FailStartup(); // valid-looking public metadata cannot override the actual private key
            using (var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero))
            {
                Check(lease is not null, "Metadata restoration lacks offline lease.");
                using var connection = database.OpenConnection();
                try { using var command = connection.CreateCommand(); command.CommandText = "UPDATE SecureCredentialReferences SET PublicKeyFingerprint=$pin WHERE CredentialRef=(SELECT CurrentCredentialRef FROM HostIdentity);"; command.Parameters.AddWithValue("$pin", expected); command.ExecuteNonQuery(); }
                finally { Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection); }
            }
            var databaseFile = new FileInfo(database.DatabasePath); var originalFileAcl = databaseFile.GetAccessControl();
            var unsafeAcl = databaseFile.GetAccessControl();
            unsafeAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            using (var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero))
            { Check(lease is not null, "ACL fixture lacks offline lease."); databaseFile.SetAccessControl(unsafeAcl); }
            try { await FailStartup(); }
            finally
            {
                await platform.StopAsync();
                using var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero);
                Check(lease is not null, "ACL restoration lacks offline lease."); databaseFile.SetAccessControl(originalFileAcl);
            }
            await Ready(); await platform.StopAsync();
            var current = state.Read().CurrentReference!;
            using (var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero))
            {
                Check(lease is not null, "Missing-credential fixture lacks offline lease.");
                await store.DeleteAsync(current);
            }
            await FailStartup(); // cached native key must not replace missing authoritative DPAPI material
            await Offline(0, "recover-machine", "--reason", "loss");
            await Ready(); await platform.StopAsync();
            Check(!state.Read().Initialized, "Service/recovery granted Owner authority before intended-user completion.");
            Console.WriteLine("PASS integration: shipped Host/Host.Cli executables, independent SCM startup, trust reconciliation, normal/crash restart, lease ownership, unsafe root/mismatched or missing credential refusal, no TCP, unchanged activation ACL");
        }
        finally
        {
            void Clean(Action action) { try { action(); } catch (Exception ex) { cleanup.Add(ex); } }
            if (installed) Clean(() => { platform.StopAsync().GetAwaiter().GetResult(); platform.UninstallAsync().GetAwaiter().GetResult(); });
            if (serviceSid is not null) Clean(() =>
            {
                using var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero);
                Check(lease is not null, "Product fixture cleanup lacks offline lease.");
                if (hostId != Guid.Empty) new WindowsHostTlsCredentialCache(hostId, serviceSid, new WindowsSecureCredentialStore(location.HostRoot, serviceSid)).ReconcileAsync([]).GetAwaiter().GetResult();
                if (nativeGrantOwned) WindowsNativeTlsProvisioning.RemoveCreatePermission(serviceSid);
            });
            if (platform.ActivationGroupCreated) Clean(() => { var code = Native.NetLocalGroupDel(null, location.ActivationGroup); if (code != 0) throw new Win32Exception((int)code); });
            // Only this fixture's absent-at-entry fixed product directory; never a computed ancestor.
            if (installed || platform.ActivationGroupCreated) Clean(() =>
            {
                Check(Path.GetFullPath(productRoot) == expectedRoot, "Unsafe product fixture cleanup target.");
                if (Directory.Exists(productRoot)) Directory.Delete(productRoot, true);
            });
            if (cleanup.Count != 0) throw new AggregateException("Production Host fixture cleanup failed.", cleanup);
        }
    }
    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeServerProcessId", SetLastError = true)]
    private static extern bool ProductionGetPipePid(SafePipeHandle pipe, out uint pid);
}
