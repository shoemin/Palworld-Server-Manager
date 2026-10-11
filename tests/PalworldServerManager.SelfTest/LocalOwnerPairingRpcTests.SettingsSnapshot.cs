using System.Net;
using System.Text;
using System.Text.Json;
using PalworldServerManager.Client.Platform.Contracts;
using System.Security.Authentication;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Platform.Contracts;
using ActorRef = PalworldServerManager.Core.Authorization.ActorRef;
using ServerCapability = PalworldServerManager.Core.Authorization.ServerCapability;

namespace PalworldServerManager.SelfTest;

internal static partial class LocalOwnerPairingRpcTests
{
    private sealed class SnapshotFiles : IManagedServerSettingsReader
    {
        internal int Reads;
        internal string Name = new('界', 12000);
        internal Action? OnRead;
        internal byte[]? Last;
        public ManagedServerSettingsFiles Read(Guid profile, string? path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Reads); OnRead?.Invoke();
            Last = Encoding.UTF8.GetBytes("[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(ServerName=\"" + Name + "\",AdminPassword=\"stored-private-token\",FuturePassword=\"unknown-private-token\")\n");
            return new(Last, Encoding.UTF8.GetBytes("[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(ServerName=\"default\",ServerPassword=\"default-private-token\")\n"));
        }
    }
    private static SnapshotFiles ConfigureSnapshot(Fixture f)
    {
        var files = new SnapshotFiles();
        f.SettingsReader = new(f.State.State.Database, f.State.State.HostId, files);
        f.State.Runtime.ConfigureSettings(f.SettingsReader); return files;
    }
    private static PalworldServerManager.Contracts.Wire.ServerRef SnapshotServer(Fixture f) => SchemaServer(f);
    private static async Task<LocalSecurityRpcTests.Client> SnapshotClient(Fixture f, bool feature = true, bool unboundedSend = false)
    {
        var c = new LocalSecurityRpcTests.Client(f.State.State.HostId, f.Pipe,
            new LocalSecurityRpcTests.Reader(LocalHostTrustAnchor.Parse(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, hostId = f.State.State.HostId, currentHostCredentialFingerprint = f.CurrentPin, pendingHostCredentialFingerprint = (string?)null, pendingRotationId = (Guid?)null }))), unboundedSend,
            SettingsSnapshotValidation.MaximumResponseBytes);
        try
        {
            var hello = new Handshake { Protocol = new() { Major = 1, Minor = 20 }, Capabilities = { FeatureCapability.LocalPrincipalSecurity } };
            if (feature) { hello.Capabilities.Add(FeatureCapability.SettingsSnapshot); hello.Capabilities.Add(FeatureCapability.LocalRemoteSettingsSnapshot); }
            await c.Call<Handshake, LocalHandshakeReply>("Negotiate", hello);
            await c.Authenticate(f.State.State.HostId, f.Principal, f.Key); return c;
        }
        catch { c.Dispose(); throw; }
    }
    private static Task<SettingsSnapshot> LocalSnapshot(LocalSecurityRpcTests.Client c, PalworldServerManager.Contracts.Wire.ServerRef server)
        => c.Call<SettingsSnapshotRequest, SettingsSnapshot>("GetServerSettings", new() { Server = server }, "palworld.manager.v1.LocalSettingsProtocol");
    private static Task<SettingsSnapshot> RemoteSnapshot(LocalSecurityRpcTests.Client c, Fixture receiver,
        PalworldServerManager.Contracts.Wire.ServerRef server, Uri? address = null)
        => c.Call<LocalRemoteSettingsSnapshotRequest, SettingsSnapshot>("GetRemoteServerSettings", new()
            { Server = server, Destination = Contact(receiver.State.State.HostId, address ?? receiver.Generation.Endpoints!.Value.Peer) }, "palworld.manager.v1.LocalSettingsProtocol");
    private static void SnapshotCheck(SettingsSnapshot reply)
    {
        Check(SettingsSnapshotValidation.IsValidReply(reply, reply.Server) && reply.CalculateSize() > 16 * 1024);
        Check(reply.Entries.Single(e => e.Definition.Key == "ServerName").Configured.Value == new string('界', 12000));
        Check(reply.Entries.All(e => e.Effective is null));
        Check(reply.Entries.Where(e => e.Definition.Sensitivity != SettingsSensitivity.Nonsecret).All(e => e.Configured is null && e.InstallationDefault is null));
        Check(!Encoding.UTF8.GetString(reply.ToByteArray()).Contains("private-token", StringComparison.Ordinal));
    }
    public static async Task SnapshotLocalOwnerAndExactTarget()
    {
        await using var f = new Fixture(); ConfigureSnapshot(f); await f.Start(new RefusingFactory()); var server = SnapshotServer(f);
        using var c = await SnapshotClient(f); SnapshotCheck(await LocalSnapshot(c, server));
        var missing = server.Clone(); missing.ServerProfileId = Guid.NewGuid().ToString("D");
        await Refused(LocalSnapshot(c, missing), StatusCode.FailedPrecondition);
        var foreign = server.Clone(); foreign.AuthoritativeHostId = Guid.NewGuid().ToString("D");
        await Refused(LocalSnapshot(c, foreign), StatusCode.InvalidArgument);
        var bad = server.Clone(); bad.ServerProfileId = "bad"; await Refused(LocalSnapshot(c, bad), StatusCode.InvalidArgument);
        using var old = await SnapshotClient(f, false); await Refused(LocalSnapshot(old, server), StatusCode.FailedPrecondition);
        f.State.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");
        await Refused(LocalSnapshot(c, server), StatusCode.Unauthenticated);
    }
    public static async Task SnapshotLocalViewAndEditRemainIndependent()
    {
        await using var f = new Fixture(false); ConfigureSnapshot(f); await f.Start(new RefusingFactory()); var server = SnapshotServer(f);
        using var c = await SnapshotClient(f); await Refused(LocalSnapshot(c, server), StatusCode.PermissionDenied);
        var onlyView = SchemaGrant(f, ActorRef.LocalPrincipal(f.Principal), server, ServerCapability.ViewServer);
        SnapshotCheck(await LocalSnapshot(c, server));
        var initialPolicy = NetworkGrants(f); initialPolicy.InvalidateServerSubtree(NetworkOwner(f), initialPolicy.Read().Revision, onlyView);
        SchemaGrant(f, ActorRef.LocalPrincipal(f.Principal), server, ServerCapability.EditSettings);
        await Refused(LocalSnapshot(c, server), StatusCode.PermissionDenied);
        var view = SchemaGrant(f, ActorRef.LocalPrincipal(f.Principal), server, ServerCapability.ViewServer);
        SnapshotCheck(await LocalSnapshot(c, server));
        var other = SnapshotServer(f); await Refused(LocalSnapshot(c, other), StatusCode.PermissionDenied);
        var repo = NetworkGrants(f); repo.InvalidateServerSubtree(NetworkOwner(f), repo.Read().Revision, view);
        await Refused(LocalSnapshot(c, server), StatusCode.PermissionDenied);
    }
    public static async Task SnapshotRemoteIndependentCeilings()
    {
        await using var a = new Fixture(false); await using var b = new Fixture();
        ConfigureSnapshot(a); ConfigureSnapshot(b); await a.Start(new RefusingFactory()); await b.Start(new RefusingFactory()); await NetworkPair(a, b);
        var server = SnapshotServer(b); using var c = await SnapshotClient(a);
        var contacts = 0;
        await using var app = WindowsHostComposition.BuildPeerApplication(b.State.Runtime, b.State.Certificate.Value, new(IPAddress.Loopback, 0));
        app.Use(async (context, next) => { Interlocked.Increment(ref contacts); await next(context); }); await app.StartAsync();
        try
        {
            var address = new Uri(app.Urls.Single());
            using var unsupported = await SnapshotClient(a, false);
            await Refused(RemoteSnapshot(unsupported, b, server, address), StatusCode.FailedPrecondition); Check(contacts == 0);
            await Refused(RemoteSnapshot(c, b, server, address), StatusCode.PermissionDenied); Check(contacts == 0);
            var localEdit = SchemaGrant(a, ActorRef.LocalPrincipal(a.Principal), server, ServerCapability.EditSettings);
            await Refused(RemoteSnapshot(c, b, server, address), StatusCode.PermissionDenied); Check(contacts == 0);
            SchemaGrant(a, ActorRef.LocalPrincipal(a.Principal), server, ServerCapability.ViewServer);
            await Refused(RemoteSnapshot(c, b, server, address), StatusCode.PermissionDenied); Check(contacts > 0);
            var peerEdit = SchemaGrant(b, ActorRef.RemoteManager(a.State.State.HostId), server, ServerCapability.EditSettings);
            await Refused(RemoteSnapshot(c, b, server, address), StatusCode.PermissionDenied);
            var machine = SchemaGrant(b, ActorRef.RemoteManager(a.State.State.HostId), server, ServerCapability.ViewServer);
            SnapshotCheck(await RemoteSnapshot(c, b, server, address));
            var localPolicy = NetworkGrants(a); localPolicy.InvalidateServerSubtree(NetworkOwner(a), localPolicy.Read().Revision, localEdit);
            var peerPolicy = NetworkGrants(b); peerPolicy.InvalidateServerSubtree(NetworkOwner(b), peerPolicy.Read().Revision, peerEdit);
            SnapshotCheck(await RemoteSnapshot(c, b, server, address)); // View alone on both Hosts is sufficient.
            var collision = server.Clone(); collision.AuthoritativeHostId = a.State.State.HostId.ToString("D");
            await Refused(RemoteSnapshot(c, b, collision, address), StatusCode.InvalidArgument);
            var repo = NetworkGrants(b); repo.InvalidateServerSubtree(NetworkOwner(b), repo.Read().Revision, machine);
            await Refused(RemoteSnapshot(c, b, server, address), StatusCode.PermissionDenied);
            SchemaGrant(b, ActorRef.RemoteManager(a.State.State.HostId), server, ServerCapability.ViewServer);
            b.State.State.Execute($"UPDATE TrustedManagers SET State='PeerBound' WHERE PeerHostId='{a.State.State.HostId:D}';");
            await Refused(RemoteSnapshot(c, b, server, address), StatusCode.Unauthenticated);
        }
        finally { await app.StopAsync(); }
    }
    public static async Task SnapshotLocalRevocationBeforeOutboundRead()
    {
        await using var a = new Fixture(false); await using var b = new Fixture();
        ConfigureSnapshot(a); ConfigureSnapshot(b); await a.Start(new RefusingFactory()); await b.Start(new RefusingFactory()); await NetworkPair(a, b);
        var server = SnapshotServer(b); var grant = SchemaGrant(a, ActorRef.LocalPrincipal(a.Principal), server, ServerCapability.ViewServer);
        SchemaGrant(b, ActorRef.RemoteManager(a.State.State.HostId), server, ServerCapability.ViewServer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var reads = 0;
        await using var app = WindowsHostComposition.BuildPeerApplication(b.State.Runtime, b.State.Certificate.Value, new(IPAddress.Loopback, 0));
        app.Use(async (context, next) => { if (context.Request.Path.Value!.EndsWith("/Negotiate", StringComparison.Ordinal)) { entered.TrySetResult(); await release.Task; }
            if (context.Request.Path.Value!.EndsWith("/GetServerSettings", StringComparison.Ordinal)) Interlocked.Increment(ref reads); await next(context); });
        await app.StartAsync(); using var c = await SnapshotClient(a); var pending = RemoteSnapshot(c, b, server, new(app.Urls.Single()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var repo = NetworkGrants(a);
            repo.InvalidateServerSubtree(NetworkOwner(a), repo.Read().Revision, grant); release.TrySetResult();
            await Refused(pending, StatusCode.PermissionDenied); Check(reads == 0);
        }
        finally { release.TrySetResult(); try { await pending; } catch (RpcException) { } await app.StopAsync(); }
    }
    public static async Task SnapshotChangedPrincipalCannotReceiveRemoteReply()
    {
        await using var a = new Fixture(); await using var b = new Fixture();
        ConfigureSnapshot(a); ConfigureSnapshot(b); await a.Start(new RefusingFactory()); await b.Start(new RefusingFactory()); await NetworkPair(a, b);
        var server = SnapshotServer(b); SchemaGrant(b, ActorRef.RemoteManager(a.State.State.HostId), server, ServerCapability.ViewServer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = WindowsHostComposition.BuildPeerApplication(b.State.Runtime, b.State.Certificate.Value, new(IPAddress.Loopback, 0));
        app.Use(async (context, next) => { if (context.Request.Path.Value!.EndsWith("/GetServerSettings", StringComparison.Ordinal)) { entered.TrySetResult(); await release.Task; } await next(context); });
        await app.StartAsync(); using var c = await SnapshotClient(a); var pending = RemoteSnapshot(c, b, server, new(app.Urls.Single()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); a.State.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;"); release.TrySetResult();
            await Refused(pending, StatusCode.Unauthenticated);
        }
        finally { release.TrySetResult(); try { await pending; } catch (RpcException) { } await app.StopAsync(); }
    }
    public static async Task SnapshotNoRemoteAdvertisementWithoutGeneration()
    {
        await using var f = new LocalSecurityRpcTests.Fixture(true); await f.Start(); using var c = f.Connect();
        var reply = await c.Call<Handshake,LocalHandshakeReply>("Negotiate", new() { Protocol = new() { Major = 1, Minor = 0 },
            Capabilities = { FeatureCapability.LocalPrincipalSecurity, FeatureCapability.SettingsSnapshot, FeatureCapability.LocalRemoteSettingsSnapshot } });
        Check(!reply.Handshake.Capabilities.Contains(FeatureCapability.SettingsSnapshot) && !reply.Handshake.Capabilities.Contains(FeatureCapability.LocalRemoteSettingsSnapshot));
    }

    public static async Task SnapshotRevisionBoundsAndSanitizedFailures()
    {
        await using var f = new Fixture(); var files = ConfigureSnapshot(f); await f.Start(new RefusingFactory()); var server = SnapshotServer(f);
        using var c = await SnapshotClient(f, unboundedSend: true);
        var first = await LocalSnapshot(c, server); SnapshotCheck(first);
        Check((await LocalSnapshot(c, server)).Revision == first.Revision);
        files.Name = new string('界', 1_000_000);
        var large = await LocalSnapshot(c, server);
        Check(large.CalculateSize() > 2_900_000 && SettingsSnapshotValidation.IsValidReply(large, server));
        Check(long.Parse(large.Revision) > long.Parse(first.Revision) && files.Last!.All(b => b == 0));
        files.Name = new string('界', 1_048_576);
        await Refused(LocalSnapshot(c, server), StatusCode.Internal);
        Check(files.Last!.All(b => b == 0));
        var oversized = server.Clone(); oversized.ServerProfileId = new string('x', 17000);
        await Refused(LocalSnapshot(c, oversized), StatusCode.ResourceExhausted);
        await Refused(c.Call<LocalPrincipalRequest, LocalChallenge>("IssueChallenge", new() { LocalPrincipalId = new string('x', 17000) }), StatusCode.ResourceExhausted);
        files.OnRead = () => throw new IOException("C:/private-path/stored-private-token");
        try { await LocalSnapshot(c, server); throw new Exception("Unsafe error accepted."); }
        catch (RpcException ex) { Check(ex.StatusCode == StatusCode.Internal && !ex.ToString().Contains("private-token") && !ex.ToString().Contains("private-path")); }
    }
    public static async Task SnapshotClosedReplyAndBindingValidation()
    {
        await ProtocolTests.SchemaEvolution();
        Check((int)FeatureCapability.SettingsSnapshot == 25 && (int)FeatureCapability.LocalRemoteSettingsSnapshot == 26);
        await using var f = new Fixture(); var files = ConfigureSnapshot(f); await f.Start(new RefusingFactory()); var server = SnapshotServer(f);
        using var c = await SnapshotClient(f); var good = await LocalSnapshot(c, server); var request = new SettingsSnapshotRequest { Server = server };
        PeerSettingsSnapshotRpcClient.ValidateReply(good, request);
        foreach (var change in new Action<SettingsSnapshot>[] {
            r => r.Revision = "0", r => r.Revision = "01", r => r.Revision = "9223372036854775808",
            r => r.Server.ServerProfileId = Guid.NewGuid().ToString("D"), r => r.Server.AuthoritativeHostId = Guid.NewGuid().ToString("D"),
            r => r.Entries.Add(r.Entries[0].Clone()), r => r.Entries[0].Definition.Sensitivity = (SettingsSensitivity)999,
            r => r.Entries.Single(e => e.Definition.Key == "AdminPassword").Configured = new() { Value = "secret", Validation = SettingsValidationCode.Valid },
            r => r.Entries.Single(e => e.Definition.Key == "ServerName").Configured.Value = "broken\uD800",
            r => r.Entries.Single(e => e.Definition.Key == "ServerName").Configured.Value = new string('x', 1_048_576) })
        {
            var bad = good.Clone(); change(bad);
            try { PeerSettingsSnapshotRpcClient.ValidateReply(bad, request); throw new Exception("Invalid snapshot accepted."); }
            catch (AuthenticationException) { }
        }
        using (var extra = new MemoryStream())
        {
            using (var output = new CodedOutputStream(extra, leaveOpen: true))
            { output.WriteTag(100, WireFormat.WireType.LengthDelimited); output.WriteBytes(ByteString.CopyFrom(new byte[SettingsSnapshotValidation.MaximumResponseBytes])); }
            var oversized = good.Clone(); oversized.MergeFrom(extra.ToArray());
            Check(!SettingsSnapshotValidation.IsValidReply(oversized, server));
        }
        try { f.State.Runtime.ConfigureSettings(f.SettingsReader!); throw new Exception("Reader rebound."); } catch (InvalidOperationException) { }
        await using var other = new Fixture();
        try { other.State.Runtime.ConfigureSettings(f.SettingsReader!); throw new Exception("Foreign reader bound."); } catch (InvalidOperationException) { }
        var reads = files.Reads;
        files.OnRead = () => f.State.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");
        await Refused(LocalSnapshot(c, server), StatusCode.Unauthenticated); Check(files.Reads == reads + 1 && files.Last!.All(b => b == 0));
    }

    public static async Task SnapshotGenerationDrainCancelsOutboundRead()
    {
        await using var a = new Fixture(); await using var b = new Fixture();
        ConfigureSnapshot(a); var files = ConfigureSnapshot(b);
        await a.Start(new RefusingFactory()); await b.Start(new RefusingFactory()); await NetworkPair(a, b);
        var server = SnapshotServer(b); SchemaGrant(b, ActorRef.RemoteManager(a.State.State.HostId), server, ServerCapability.ViewServer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = WindowsHostComposition.BuildPeerApplication(b.State.Runtime, b.State.Certificate.Value, new(IPAddress.Loopback, 0));
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Value!.EndsWith("/GetServerSettings", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } catch (OperationCanceledException) { }
                return;
            }
            await next(context);
        });
        await app.StartAsync(); using var c = await SnapshotClient(a);
        var pending = RemoteSnapshot(c, b, server, new(app.Urls.Single()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await a.Generation.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Stopped generation returned settings."); }
            catch (RpcException ex) { Check(ex.StatusCode is StatusCode.Cancelled or StatusCode.Unavailable); }
            Check(files.Reads == 0);
        }
        finally { await a.Generation.StopAsync(); try { await pending; } catch (RpcException) { } await app.StopAsync(); }
    }

    public static async Task SnapshotDirectPeerRequiresNegotiationAndCurrentProof()
    {
        await using var a = new Fixture(); await using var b = new Fixture();
        ConfigureSnapshot(a); var files = ConfigureSnapshot(b); files.Name = "small";
        await a.Start(new RefusingFactory()); await b.Start(new RefusingFactory()); await NetworkPair(a, b);
        var server = SnapshotServer(b); SchemaGrant(b, ActorRef.RemoteManager(a.State.State.HostId), server, ServerCapability.ViewServer);
        await b.State.Start();
        var request = new SettingsSnapshotRequest { Server = server };
        using (var unnegotiated = new PeerSecurityRpcTests.RawClient(a.State, b.State))
            await Refused(unnegotiated.Settings.GetServerSettingsAsync(request, deadline: DateTime.UtcNow.AddSeconds(5)).ResponseAsync, StatusCode.FailedPrecondition);
        using (var old = new PeerSecurityRpcTests.RawClient(a.State, b.State))
        {
            var hello = PeerSecurityRpcRuntime.Hello(a.State.State.HostId); hello.Handshake.Capabilities.Remove(FeatureCapability.SettingsSnapshot);
            await old.Negotiate(hello);
            await Refused(old.Settings.GetServerSettingsAsync(request, deadline: DateTime.UtcNow.AddSeconds(5)).ResponseAsync, StatusCode.FailedPrecondition);
        }
        Check(files.Reads == 0);
        using var c = new PeerSecurityRpcTests.RawClient(a.State, b.State, unboundedSend: true);
        await c.Negotiate(PeerSecurityRpcRuntime.Hello(a.State.State.HostId));
        var good = await c.Settings.GetServerSettingsAsync(request, deadline: DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
        Check(SettingsSnapshotValidation.IsValidReply(good, server) && good.Entries.Single(e => e.Definition.Key == "ServerName").Configured.Value == "small");
        var oversized = request.Clone(); oversized.Server.ServerProfileId = new string('x', 17000);
        await Refused(c.Settings.GetServerSettingsAsync(oversized, deadline: DateTime.UtcNow.AddSeconds(5)).ResponseAsync, StatusCode.ResourceExhausted);
        var oversizedHello = PeerSecurityRpcRuntime.Hello(a.State.State.HostId); oversizedHello.Handshake.ProductVersion = new string('x', 17000);
        await Refused(c.Negotiate(oversizedHello), StatusCode.ResourceExhausted);
        b.State.State.Execute($"UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{new string('A', 64)}' WHERE PeerHostId='{a.State.State.HostId:D}';");
        await Refused(c.Settings.GetServerSettingsAsync(request, deadline: DateTime.UtcNow.AddSeconds(5)).ResponseAsync, StatusCode.Unauthenticated);
        Check(files.Reads == 1);
    }
}
