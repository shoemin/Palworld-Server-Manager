using System.Net;
using System.Security.Authentication;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Settings;
using PalworldServerManager.Host;
using ActorRef = PalworldServerManager.Core.Authorization.ActorRef;
using ServerCapability = PalworldServerManager.Core.Authorization.ServerCapability;
using Target = PalworldServerManager.Core.Authorization.ServerRef;

namespace PalworldServerManager.SelfTest;

internal static partial class LocalOwnerPairingRpcTests
{
    private static PalworldServerManager.Contracts.Wire.ServerRef SchemaServer(Fixture f, Guid? id = null)
    {
        var server = new PalworldServerManager.Contracts.Wire.ServerRef { AuthoritativeHostId = f.State.State.HostId.ToString("D"), ServerProfileId = (id ?? Guid.NewGuid()).ToString("D") };
        f.State.State.Execute($"INSERT INTO ServerInventory (ServerProfileId,AuthoritativeHostId,DisplayName,InstallPath,CreatedUtc) VALUES ('{server.ServerProfileId}','{server.AuthoritativeHostId}','schema-only','deliberately-no-file-access','{DateTimeOffset.UtcNow:O}');");
        return server;
    }
    private static Guid SchemaGrant(Fixture issuer, ActorRef grantee, PalworldServerManager.Contracts.Wire.ServerRef server, ServerCapability capability)
    {
        var repo = NetworkGrants(issuer); var id = Guid.NewGuid();
        repo.IssueServer(NetworkOwner(issuer), repo.Read().Revision, id, grantee, capability,
            new Target(Guid.Parse(server.AuthoritativeHostId), Guid.Parse(server.ServerProfileId)), new(false, false), null);
        return id;
    }
    private static async Task<LocalSecurityRpcTests.Client> SchemaClient(Fixture f, bool feature = true)
    {
        var c = f.Client();
        try
        {
            var hello = new Handshake { Protocol = new() { Major = 1, Minor = 0 }, Capabilities = { FeatureCapability.LocalPrincipalSecurity } };
            if (feature) { hello.Capabilities.Add(FeatureCapability.SettingsSchema); hello.Capabilities.Add(FeatureCapability.LocalRemoteSettingsSchema); }
            await c.Call<Handshake,LocalHandshakeReply>("Negotiate", hello);
            await c.Authenticate(f.State.State.HostId, f.Principal, f.Key); return c;
        }
        catch { c.Dispose(); throw; }
    }
    private static Task<SettingsSchemaReply> LocalSchema(LocalSecurityRpcTests.Client c, PalworldServerManager.Contracts.Wire.ServerRef server)
        => c.Call<SettingsSchemaRequest,SettingsSchemaReply>("GetSettingsSchema", new() { Server = server });
    private static Task<SettingsSchemaReply> RemoteSchema(LocalSecurityRpcTests.Client c, Fixture receiver,
        PalworldServerManager.Contracts.Wire.ServerRef server, Uri? address = null)
        => c.Call<LocalRemoteSettingsSchemaRequest,SettingsSchemaReply>("GetRemoteSettingsSchema", new()
            { Server = server, Destination = Contact(receiver.State.State.HostId, address ?? receiver.Generation.Endpoints!.Value.Peer) });
    private static void SchemaCheck(SettingsSchemaReply reply)
    {
        Check(SettingsSchemaValidation.IsValid(reply) && reply.Definitions.Count == 95 && reply.CalculateSize() <= 16 * 1024);
        Check(reply.Definitions.Count(d => d.EditorKind == SettingsEditorKind.Password) == 2);
        Check(reply.Definitions.All(d => !d.HasCatalogDefault && !d.HasRestartRequired && !d.HasMinimum && !d.HasMaximum && !d.HasStep));
    }
    public static async Task SchemaCatalogAndClosedReplyValidation()
    {
        await ProtocolTests.SchemaEvolution();
        Check((int)FeatureCapability.SettingsSchema == 23 && (int)FeatureCapability.LocalRemoteSettingsSchema == 24);
        var known = PalworldSettingCatalog.KnownDefinitions();
        Check(known.Count == 95 && known.All(d => d.IsKnown) && known.Select(d => d.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 95);
        try { ((IList<PalworldSettingDefinition>)known).Clear(); throw new Exception("Mutable catalog list."); } catch (NotSupportedException) { }
        var request = new SettingsSchemaRequest { Server = new() { AuthoritativeHostId = Guid.NewGuid().ToString("D"), ServerProfileId = Guid.NewGuid().ToString("D") } };
        var reply = new SettingsSchemaReply { Server = request.Server.Clone() };
        reply.Definitions.AddRange(known.Select(SettingsContractMapping.Definition)); SchemaCheck(reply);
        PeerSettingsSchemaRpcClient.ValidateReply(reply, request);
        foreach (var mutate in new Action<SettingsSchemaReply>[] {
            r => r.Server.ServerProfileId = Guid.NewGuid().ToString("D"), r => r.Server.AuthoritativeHostId = Guid.NewGuid().ToString("D"),
            r => r.Server.ServerProfileId = "bad", r => r.Definitions.Add(r.Definitions[0].Clone()),
            r => r.Definitions[0].EditorKind = (SettingsEditorKind)999, r => r.Definitions[0].Sensitivity = (SettingsSensitivity)999,
            r => r.Definitions.Single(d => d.Key == "AdminPassword").CatalogDefault = "private",
            r => r.Definitions.First(d => d.EditorKind == SettingsEditorKind.Raw).CatalogDefault = "unclassified",
            r => r.Definitions[0].Description = new string('x', 17000), r => r.Definitions[0].Description = "broken\uD800" })
        {
            var bad = reply.Clone(); mutate(bad);
            try { PeerSettingsSchemaRpcClient.ValidateReply(bad, request); throw new Exception("Bad schema reply accepted."); }
            catch (AuthenticationException) { }
        }
        Check(reply.Definitions.All(d => !d.HasCatalogDefault)); // Caller mutation did not reach the catalog.
    }
    public static async Task SchemaLocalOwnerAndExactTarget()
    {
        await using var f = new Fixture(); await f.Start(new RefusingFactory()); var server = SchemaServer(f);
        using var c = await SchemaClient(f); SchemaCheck(await LocalSchema(c, server));
        var missing = server.Clone(); missing.ServerProfileId = Guid.NewGuid().ToString("D");
        await Refused(LocalSchema(c, missing), StatusCode.FailedPrecondition);
        var foreign = server.Clone(); foreign.AuthoritativeHostId = Guid.NewGuid().ToString("D");
        await Refused(LocalSchema(c, foreign), StatusCode.InvalidArgument);
        var bad = server.Clone(); bad.ServerProfileId = "bad"; await Refused(LocalSchema(c, bad), StatusCode.InvalidArgument);
        using var old = await SchemaClient(f, false); await Refused(LocalSchema(old, server), StatusCode.FailedPrecondition);
        f.State.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");
        await Refused(LocalSchema(c, server), StatusCode.Unauthenticated);
    }
    public static async Task SchemaLocalViewAndEditRemainIndependent()
    {
        await using var f = new Fixture(false); await f.Start(new RefusingFactory()); var server = SchemaServer(f);
        using var c = await SchemaClient(f); await Refused(LocalSchema(c, server), StatusCode.PermissionDenied);
        SchemaGrant(f, ActorRef.LocalPrincipal(f.Principal), server, ServerCapability.EditSettings);
        await Refused(LocalSchema(c, server), StatusCode.PermissionDenied);
        var view = SchemaGrant(f, ActorRef.LocalPrincipal(f.Principal), server, ServerCapability.ViewServer);
        SchemaCheck(await LocalSchema(c, server));
        var other = SchemaServer(f); await Refused(LocalSchema(c, other), StatusCode.PermissionDenied);
        var repo = NetworkGrants(f); repo.InvalidateServerSubtree(NetworkOwner(f), repo.Read().Revision, view);
        await Refused(LocalSchema(c, server), StatusCode.PermissionDenied);
    }
    public static async Task SchemaRemoteIndependentCeilings()
    {
        await using var a = new Fixture(false); await using var b = new Fixture();
        await a.Start(new RefusingFactory()); await b.Start(new RefusingFactory()); await NetworkPair(a, b);
        var server = SchemaServer(b); using var c = await SchemaClient(a);
        var contacts = 0;
        await using var app = WindowsHostComposition.BuildPeerApplication(b.State.Runtime, b.State.Certificate.Value, new(IPAddress.Loopback, 0));
        app.Use(async (context, next) => { Interlocked.Increment(ref contacts); await next(context); }); await app.StartAsync();
        try
        {
            var address = new Uri(app.Urls.Single());
            using var unsupported = await SchemaClient(a, false);
            await Refused(RemoteSchema(unsupported, b, server, address), StatusCode.FailedPrecondition); Check(contacts == 0);
            await Refused(RemoteSchema(c, b, server, address), StatusCode.PermissionDenied); Check(contacts == 0);
            SchemaGrant(a, ActorRef.LocalPrincipal(a.Principal), server, ServerCapability.EditSettings);
            await Refused(RemoteSchema(c, b, server, address), StatusCode.PermissionDenied); Check(contacts == 0);
            SchemaGrant(a, ActorRef.LocalPrincipal(a.Principal), server, ServerCapability.ViewServer);
            await Refused(RemoteSchema(c, b, server, address), StatusCode.PermissionDenied); Check(contacts > 0);
            SchemaGrant(b, ActorRef.RemoteManager(a.State.State.HostId), server, ServerCapability.EditSettings);
            await Refused(RemoteSchema(c, b, server, address), StatusCode.PermissionDenied);
            var machine = SchemaGrant(b, ActorRef.RemoteManager(a.State.State.HostId), server, ServerCapability.ViewServer);
            SchemaCheck(await RemoteSchema(c, b, server, address));
            var collision = server.Clone(); collision.AuthoritativeHostId = a.State.State.HostId.ToString("D");
            await Refused(RemoteSchema(c, b, collision, address), StatusCode.InvalidArgument);
            var repo = NetworkGrants(b); repo.InvalidateServerSubtree(NetworkOwner(b), repo.Read().Revision, machine);
            await Refused(RemoteSchema(c, b, server, address), StatusCode.PermissionDenied);
            SchemaGrant(b, ActorRef.RemoteManager(a.State.State.HostId), server, ServerCapability.ViewServer);
            b.State.State.Execute($"UPDATE TrustedManagers SET State='PeerBound' WHERE PeerHostId='{a.State.State.HostId:D}';");
            await Refused(RemoteSchema(c, b, server, address), StatusCode.Unauthenticated);
        }
        finally { await app.StopAsync(); }
    }
    public static async Task SchemaLocalRevocationBeforeOutboundRead()
    {
        await using var a = new Fixture(false); await using var b = new Fixture();
        await a.Start(new RefusingFactory()); await b.Start(new RefusingFactory()); await NetworkPair(a, b);
        var server = SchemaServer(b); var grant = SchemaGrant(a, ActorRef.LocalPrincipal(a.Principal), server, ServerCapability.ViewServer);
        SchemaGrant(b, ActorRef.RemoteManager(a.State.State.HostId), server, ServerCapability.ViewServer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var reads = 0;
        await using var app = WindowsHostComposition.BuildPeerApplication(b.State.Runtime, b.State.Certificate.Value, new(IPAddress.Loopback, 0));
        app.Use(async (context, next) => { if (context.Request.Path.Value!.EndsWith("/Negotiate", StringComparison.Ordinal)) { entered.TrySetResult(); await release.Task; }
            if (context.Request.Path.Value!.EndsWith("/GetSettingsSchema", StringComparison.Ordinal)) Interlocked.Increment(ref reads); await next(context); });
        await app.StartAsync(); using var c = await SchemaClient(a); var pending = RemoteSchema(c, b, server, new(app.Urls.Single()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var repo = NetworkGrants(a);
            repo.InvalidateServerSubtree(NetworkOwner(a), repo.Read().Revision, grant); release.TrySetResult();
            await Refused(pending, StatusCode.PermissionDenied); Check(reads == 0);
        }
        finally { release.TrySetResult(); await app.StopAsync(); }
    }
    public static async Task SchemaChangedPrincipalCannotReceiveRemoteReply()
    {
        await using var a = new Fixture(); await using var b = new Fixture();
        await a.Start(new RefusingFactory()); await b.Start(new RefusingFactory()); await NetworkPair(a, b);
        var server = SchemaServer(b); SchemaGrant(b, ActorRef.RemoteManager(a.State.State.HostId), server, ServerCapability.ViewServer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = WindowsHostComposition.BuildPeerApplication(b.State.Runtime, b.State.Certificate.Value, new(IPAddress.Loopback, 0));
        app.Use(async (context, next) => { if (context.Request.Path.Value!.EndsWith("/GetSettingsSchema", StringComparison.Ordinal)) { entered.TrySetResult(); await release.Task; } await next(context); });
        await app.StartAsync(); using var c = await SchemaClient(a); var pending = RemoteSchema(c, b, server, new(app.Urls.Single()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); a.State.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;"); release.TrySetResult();
            await Refused(pending, StatusCode.Unauthenticated);
        }
        finally { release.TrySetResult(); await app.StopAsync(); }
    }
    public static async Task SchemaNoRemoteAdvertisementWithoutGeneration()
    {
        await using var f = new LocalSecurityRpcTests.Fixture(true); await f.Start(); using var c = f.Connect();
        var reply = await c.Call<Handshake,LocalHandshakeReply>("Negotiate", new() { Protocol = new() { Major = 1, Minor = 0 },
            Capabilities = { FeatureCapability.LocalPrincipalSecurity, FeatureCapability.SettingsSchema, FeatureCapability.LocalRemoteSettingsSchema } });
        Check(reply.Handshake.Capabilities.Contains(FeatureCapability.SettingsSchema) && !reply.Handshake.Capabilities.Contains(FeatureCapability.LocalRemoteSettingsSchema));
    }
}
