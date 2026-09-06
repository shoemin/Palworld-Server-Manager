using System.Net;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static class LocalTrustTests
{
    public static async Task Schema()
    {
        var id = Guid.NewGuid(); var rotation = Guid.NewGuid();
        var publication = new LocalHostTrustPublication(id, new string('a', 64), new string('b', 64), rotation);
        var anchor = LocalHostTrustAnchor.Parse(publication.ToJson());
        Check(anchor.HostId == id && anchor.PendingRotationId == rotation && anchor.CurrentFingerprint == new string('A', 64), "Public descriptor round trip changed identity.");
        foreach (var invalid in new[]
        {
            "{}", "[]", "null", Encoding.UTF8.GetString(publication.ToJson()).Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            Encoding.UTF8.GetString(publication.ToJson()).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            Encoding.UTF8.GetString(publication.ToJson()).Replace(rotation.ToString(), Guid.Empty.ToString()),
            Encoding.UTF8.GetString(publication.ToJson()).Replace("\"pendingRotationId\":\"" + rotation + "\"", "\"pendingRotationId\":null")
        }) await SecureStoreTests.Reject<LocalHostAuthenticationException>(() => Task.FromResult(LocalHostTrustAnchor.Parse(Encoding.UTF8.GetBytes(invalid))));
        await SecureStoreTests.Reject<ArgumentException>(() => Task.FromResult(new LocalHostTrustPublication(id, "not-a-key").ToJson()));
        await SecureStoreTests.Reject<ArgumentException>(() => Task.FromResult(new LocalHostTrustPublication(id, new string('A', 64), new string('B', 64)).ToJson()));
    }
    public static async Task FilesAndTls()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PSMTrustTest" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "Public");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(identity.User!);
        foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }.Distinct())
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(parent).Create(acl);
        // Real provisioning is separately elevated; this fixture uses its own identity.
        var publisher = new WindowsLocalHostTrustPublisher(root, identity.User!);
        var reader = new WindowsLocalHostTrustReader(root, identity.User!);
        var id = Guid.NewGuid();
        try
        {
            await SecureStoreTests.Reject<LocalHostTrustUnavailableException>(() => reader.ReadAsync());
            new DirectoryInfo(root).Create(acl);
            await SecureStoreTests.Reject<LocalHostTrustUnavailableException>(() => reader.ReadAsync());
            await using var endpoint = await LocalIpcSpike.StartAsync(identity.User!);
            var good = new LocalHostTrustPublication(id, endpoint.PublicKeyPin);
            await publisher.PublishAsync(good);
            Check((await reader.ReadAsync()).HostId == id, "Protected public descriptor unreadable.");
            Check(await Request(reader, endpoint.PipeName, id) == identity.User!.Value, "Production local TLS transport lost native SID.");
            var delivered = endpoint.ObservedSids.Count;
            await RejectAuthentication(() => Request(reader, endpoint.PipeName, Guid.NewGuid()));
            await publisher.PublishAsync(good with { CurrentHostCredentialFingerprint = new string('0', 64) });
            await RejectAuthentication(() => Request(reader, endpoint.PipeName, id));
            Check(endpoint.ObservedSids.Count == delivered, "Sensitive request reached an untrusted endpoint.");
            await publisher.PublishAsync(new(id, new string('0', 64), endpoint.PublicKeyPin, Guid.NewGuid()));
            Check(await Request(reader, endpoint.PipeName, id) == identity.User!.Value, "Staged public-key pin was rejected.");
            await publisher.PublishAsync(good);
            Check(await Request(reader, endpoint.PipeName, id) == identity.User!.Value, "Fresh connection did not read cutover descriptor.");
            var file = new FileInfo(Path.Combine(root, WindowsLocalHostTrustPublisher.DescriptorFileName));
            var descriptor = file.GetAccessControl().GetSecurityDescriptorBinaryForm();
            var bad = file.GetAccessControl();
            bad.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow));
            file.SetAccessControl(bad);
            try
            {
                await SecureStoreTests.Reject<LocalHostAuthenticationException>(() => reader.ReadAsync());
                await SecureStoreTests.Reject<UnauthorizedAccessException>(() => publisher.PublishAsync(good));
            }
            finally { var restore = new FileSecurity(); restore.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access); file.SetAccessControl(restore); }
            Check((await reader.ReadAsync()).CurrentFingerprint == endpoint.PublicKeyPin, "File ACL fixture failed to restore baseline.");
            var parentInfo = new DirectoryInfo(parent); var parentDescriptor = parentInfo.GetAccessControl().GetSecurityDescriptorBinaryForm();
            var unsafeParent = parentInfo.GetAccessControl();
            unsafeParent.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
            parentInfo.SetAccessControl(unsafeParent);
            try { await SecureStoreTests.Reject<LocalHostAuthenticationException>(() => reader.ReadAsync()); }
            finally { var restore = new DirectorySecurity(); restore.SetSecurityDescriptorBinaryForm(parentDescriptor, AccessControlSections.Access); parentInfo.SetAccessControl(restore); }
            Check((await reader.ReadAsync()).HostId == id, "Parent ACL fixture failed to restore baseline.");
            // A malformed publication cannot clobber the last valid artifact, and a reserved
            // orphan created with the same protected ACL is retired on the next publication.
            await SecureStoreTests.Reject<ArgumentException>(() => publisher.PublishAsync(good with { CurrentHostCredentialFingerprint = "invalid" }));
            var orphan = Path.Combine(root, "trust-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.Copy(file.FullName, orphan);
            var orphanAcl = new FileSecurity(); orphanAcl.SetSecurityDescriptorBinaryForm(file.GetAccessControl().GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            new FileInfo(orphan).SetAccessControl(orphanAcl);
            await publisher.PublishAsync(good);
            Check(!File.Exists(orphan) && (await reader.ReadAsync()).HostId == id, "Public publication orphan was not retired safely.");
            await RejectPlainEndpoint(reader, id);
            await RejectPlainEndpoint(reader, id, stall: true);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await SecureStoreTests.Reject<OperationCanceledException>(() => publisher.PublishAsync(good, canceled.Token));
            try { await Request(reader, "PSMMissing" + Guid.NewGuid().ToString("N"), id); throw new Exception("Absent endpoint unexpectedly connected."); }
            catch (HttpRequestException ex) { Check(HasCause<LocalHostEndpointUnavailableException>(ex) && !HasCause<LocalHostAuthenticationException>(ex), "Absent endpoint misclassified as authentication failure."); }
        }
        finally { Directory.Delete(parent, true); }
    }
    private static async Task RejectPlainEndpoint(ILocalHostTrustReader reader, Guid id, bool stall = false)
    {
        var name = "PSMPlain" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            var hello = new byte[8192];
            var count = await pipe.ReadAsync(hello, timeout.Token);
            Check(count > 0 && !Encoding.UTF8.GetString(hello, 0, count).Contains("SYNTHETIC-PRIVATE-REQUEST", StringComparison.Ordinal), "Application body preceded TLS authentication.");
            if (stall) { await Task.Delay(TimeSpan.FromSeconds(12), timeout.Token); return; }
            await pipe.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n\r\n"), timeout.Token);
            pipe.Disconnect();
        });
        try { await Request(reader, name, id); throw new Exception("Plain endpoint was accepted."); }
        catch (HttpRequestException ex) { Check(HasCause<LocalHostAuthenticationException>(ex) && !HasCause<LocalHostEndpointUnavailableException>(ex), "Connected TLS failure lacked an explicit authentication error."); }
        await server;
    }
    internal static async Task<string> Request(ILocalHostTrustReader reader, string pipe, Guid id)
    {
        using var client = new HttpClient(new WindowsLocalHostHttpTransportFactory(reader, pipe).CreateHandler(id));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost/probe")
        {
            Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("SYNTHETIC-PRIVATE-REQUEST")
        };
        using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
    private static async Task RejectAuthentication(Func<Task<string>> request)
    {
        try { await request(); throw new Exception("Untrusted endpoint was accepted."); }
        catch (HttpRequestException ex) { Check(HasCause<LocalHostAuthenticationException>(ex) && !HasCause<LocalHostEndpointUnavailableException>(ex), "Trust failure was misclassified as dormancy."); }
    }
    private static bool HasCause<T>(Exception exception) where T : Exception => exception is T || (exception.InnerException is { } inner && HasCause<T>(inner));
}
