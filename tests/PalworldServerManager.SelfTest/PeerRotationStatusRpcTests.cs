using System.Security.Authentication;
using Grpc.Core;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Windows;
using Fixture = PalworldServerManager.SelfTest.PeerSecurityRpcTests.Fixture;
using RawClient = PalworldServerManager.SelfTest.PeerSecurityRpcTests.RawClient;

namespace PalworldServerManager.SelfTest;

internal static class PeerRotationStatusRpcTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Rotation status RPC assertion failed."); }
    private static async Task Refused<T>(Task<T> task, StatusCode status)
    { try { await task; } catch (RpcException ex) when (ex.StatusCode == status) { return; } throw new Exception("Expected rotation RPC refusal: " + status); }
    private static LocalPrincipalMutationActor Owner(Fixture f) => new(f.State.HostId, f.State.OwnerId, "native-owner", "fixture-public");
    private static HostRotationProposal Stage(Fixture receiver, Fixture sender, string next)
    {
        receiver.Bind(sender); sender.Bind(receiver);
        receiver.State.Execute("UPDATE TrustedManagers SET State='Active';"); sender.State.Execute("UPDATE TrustedManagers SET State='Active';");
        var credentials = sender.Runtime.Credentials; var owner = Owner(sender);
        var rotation = credentials.PrepareRoutineRotation(owner, Guid.NewGuid()); credentials.RecordCreated(rotation.NewReference, next);
        credentials.BeginRoutineRotationStaging(owner, rotation.RotationId); var proposal = credentials.PrepareRoutineRotationProposal(owner, rotation.RotationId);
        receiver.Runtime.Repository.StagePeerRotation(proposal, sender.Pin, receiver.Pin); return proposal;
    }
    private static PeerRotationStatusRpcClient Client(Fixture f) => WindowsHostComposition.CreatePeerRotationStatusClient(f.Runtime, f.Certificate.Value);
    private static PeerRotationStatusRequest Request(Fixture receiver, Fixture sender) => PeerRotationStatusWire.Wire(receiver.Runtime.Repository.BeginPeerRotationStatusQuery(sender.State.HostId).Request);
    private static Task<PeerRotationStatusReply> Read(RawClient client, PeerRotationStatusRequest request) => client.Rpc.ReadRotationStatusAsync(request, deadline: DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
    private static void NoAuthority(Fixture a, Fixture b, int existingAHostGrants = 0)
    { Check(a.State.Count("HostCapabilityGrants") == existingAHostGrants && a.State.Count("ServerCapabilityGrants") == 0 && b.State.Count("HostCapabilityGrants") == 0 && b.State.Count("ServerCapabilityGrants") == 0 && a.State.Count("ActivationRpcEffects") == 0 && b.State.Count("ActivationRpcEffects") == 0); }
    public static async Task ActualRenewalAbortAndFreshRetry()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); using var next = new PeerTlsTests.Certificate();
        var proposal = Stage(a, b, WindowsPeerTls.PublicFingerprint(next.Value)); await b.Start();
        a.State.Execute($"""
            INSERT INTO HostCapabilityGrants (GrantId,TargetHostId,Capability,GranteeActorKind,GranteePeerHostId,
                GrantedByActorKind,GrantedByLocalPrincipalId,CanDelegate,CanDelegateOnwardDelegation,CreatedUtc)
                VALUES ('custom','{a.State.HostId:D}','ViewHost','RemoteManager','{b.State.HostId:D}','LocalPrincipal','{a.State.OwnerId:D}',1,0,'retained');
            """);
        var before = a.Runtime.Repository.Read(b.State.HostId)!; a.State.Time.Now += TimeSpan.FromMinutes(30);
        Check(await Client(a).CheckAsync(b.State.HostId, b.Address) == PeerRotationStatusExchange.Unchanged);
        Check(a.Runtime.Repository.Read(b.State.HostId)!.PendingReconfirmationRequired);
        Check(await Client(a).CheckAsync(b.State.HostId, b.Address, Owner(a)) == PeerRotationStatusExchange.Renewed);
        var renewed = a.Runtime.Repository.Read(b.State.HostId)!;
        Check(renewed.CurrentFingerprint == b.Pin && renewed.PendingFingerprint == proposal.NewFingerprint && renewed.PendingRotationId == proposal.RotationId && renewed.PendingRotationExpiresUtc > before.PendingRotationExpiresUtc);
        Check(await Client(a).CheckAsync(b.State.HostId, b.Address, Owner(a)) == PeerRotationStatusExchange.Unchanged); // No renewal of unexpired state.
        var address = b.Address; await b.Stop();
        await Refused(Client(a).CheckAsync(b.State.HostId, address), StatusCode.Unavailable);
        Check(a.Runtime.Repository.Read(b.State.HostId)!.PendingRotationExpiresUtc == renewed.PendingRotationExpiresUtc);
        b.Runtime.Credentials.AbortRoutineRotation(Owner(b), proposal.RotationId); await b.Start();
        Check(await Client(a).CheckAsync(b.State.HostId, b.Address) == PeerRotationStatusExchange.Cleared);
        Check(a.Runtime.Repository.Read(b.State.HostId)!.CurrentFingerprint == b.Pin && a.Runtime.Repository.Read(b.State.HostId)!.PendingRotationId is null);
        NoAuthority(a, b, 1);
        Check(HostDatabase.QueryScalarLong(a.State.Writer, "SELECT COUNT(*) FROM HostCapabilityGrants WHERE GrantId='custom' AND CanDelegate=1 AND InvalidatedUtc IS NULL AND CreatedUtc='retained';") == 1);
    }
    public static async Task ActualNewProofPromotesWithoutStatusClaim()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); using var next = new PeerTlsTests.Certificate(); var pin = WindowsPeerTls.PublicFingerprint(next.Value);
        var proposal = Stage(a, b, pin); a.State.Time.Now += TimeSpan.FromHours(1);
        var unproven = new PeerSecurityRpcTests.UnprovenTransport(pin);
        try { await new PeerRotationStatusRpcClient(a.Runtime, unproven).CheckAsync(b.State.HostId, new Uri("https://127.0.0.1:1/")); throw new Exception("Expected incomplete TLS refusal."); }
        catch (AuthenticationException ex) when (ex.Message == "Fixture handshake did not complete.") { }
        Check(unproven.Admitted && a.Runtime.Repository.Read(b.State.HostId)!.CurrentFingerprint == b.Pin && a.State.Count("TrustedManagerCredentialHistory") == 0);
        var rotation = b.Runtime.Credentials.Read().Rotations.Single(r => r.RotationId == proposal.RotationId);
        // Synthetic global cutover metadata; the listener's actual New private-key proof is real.
        b.State.Execute($"UPDATE HostIdentity SET CurrentCredentialRef='{rotation.NewReference}'; UPDATE HostCredentialRotations SET State='CutOver' WHERE RotationId='{rotation.RotationId:D}';");
        await b.Start(next.Value);
        Check(await Client(a).CheckAsync(b.State.HostId, b.Address) == PeerRotationStatusExchange.NewCredentialObserved);
        var trust = a.Runtime.Repository.Read(b.State.HostId)!;
        Check(trust.CurrentFingerprint == pin && trust.PendingFingerprint is null && trust.PendingRotationId == proposal.RotationId && a.State.Count("TrustedManagerCredentialHistory") == 1);
        NoAuthority(a, b);
    }
    public static async Task NegotiationActiveAndFreshTrustGates()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); Stage(a, b, new string('C', 64)); await b.Start();
        var request = Request(a, b);
        using (var client = new RawClient(a, b)) await Refused(Read(client, request), StatusCode.FailedPrecondition);
        using (var client = new RawClient(a, b))
        {
            var legacy = PeerSecurityRpcRuntime.Hello(a.State.HostId); legacy.Handshake.Protocol.Minor = 2; legacy.Handshake.Capabilities.Remove(FeatureCapability.PeerRotationStatus);
            await client.Negotiate(legacy); await Refused(Read(client, request), StatusCode.FailedPrecondition);
        }
        using (var client = new RawClient(a, b))
        {
            await client.Negotiate(PeerSecurityRpcRuntime.Hello(a.State.HostId));
            Check((await Read(client, request)).State == PeerRotationLiveState.Staging);
            b.State.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=1;");
            await Refused(Read(client, request), StatusCode.Unauthenticated);
            b.State.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=0,State='PeerBound';");
            await Refused(Read(client, request), StatusCode.Unauthenticated); // Existing TLS still cannot turn PeerBound into ordinary trust maintenance.
            b.State.Execute("UPDATE TrustedManagers SET State='Active';");
        }
        using (var client = new RawClient(a, b))
        {
            await client.Negotiate(PeerSecurityRpcRuntime.Hello(a.State.HostId));
            var wrong = request.Clone(); wrong.HostId = a.State.HostId.ToString("D"); await Refused(Read(client, wrong), StatusCode.Unauthenticated);
            wrong = request.Clone(); wrong.QueryId = "invalid"; await Refused(Read(client, wrong), StatusCode.InvalidArgument);
            wrong = request.Clone(); wrong.NewFingerprint = new string('D', 64); await Refused(Read(client, wrong), StatusCode.Unauthenticated);
        }
        NoAuthority(a, b);
    }
    public static async Task WireClosedEnumsAndLocalCredentialChange()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); Stage(a, b, new string('C', 64));
        var query = a.Runtime.Repository.BeginPeerRotationStatusQuery(b.State.HostId, Owner(a));
        var reply = new PeerRotationStatusReply { Request = PeerRotationStatusWire.Wire(query.Request), State = (PeerRotationLiveState)99 };
        try { PeerRotationStatusWire.Durable(reply); throw new Exception("Expected closed wire enum refusal."); } catch (ArgumentException) { }
        reply.State = PeerRotationLiveState.Unspecified;
        try { PeerRotationStatusWire.Durable(reply); throw new Exception("Expected unspecified wire enum refusal."); } catch (ArgumentException) { }
        a.State.Time.Now += TimeSpan.FromMinutes(30); var before = a.Runtime.Repository.Read(b.State.HostId)!;
        a.State.Execute("UPDATE SecureCredentialReferences SET PublicKeyFingerprint='" + new string('D', 64) + "' WHERE CredentialRef='current';");
        try { a.Runtime.Repository.CompletePeerRotationStatusQuery(query, new(query.Request, RoutineRotationLiveState.Staging), b.Pin, a.Pin); throw new Exception("Expected stale actual local credential refusal."); } catch (AuthenticationException) { }
        Check(a.Runtime.Repository.Read(b.State.HostId)!.PendingRotationExpiresUtc == before.PendingRotationExpiresUtc);
        await b.Start();
        try { await Client(a).CheckAsync(b.State.HostId, b.Address); throw new Exception("Expected stale local transport refusal."); } catch (AuthenticationException) { }
        Check(a.Runtime.Repository.Read(b.State.HostId)!.PendingFingerprint == before.PendingFingerprint); NoAuthority(a, b);
    }
    public static async Task ActualReplyForgeryAndStaleOwnerAreRefused()
    {
        for (var variant = 0; variant < 4; variant++)
        {
            await using var a = new Fixture(); await using var b = new Fixture(); Stage(a, b, new string('C', 64)); await b.Start();
            a.State.Time.Now += TimeSpan.FromMinutes(30); var retained = a.Runtime.Repository.Read(b.State.HostId)!;
            var transport = new PeerReplyFaultTransport<PeerRotationStatusReply>(new WindowsPeerHttpTransportFactory(a.Certificate.Value), "ReadRotationStatus", PeerRotationStatusReply.Parser, reply =>
            {
                if (variant == 0) reply.Request.QueryId = Guid.NewGuid().ToString("D");
                if (variant == 1) reply.State = (PeerRotationLiveState)99;
                if (variant == 2) reply.State = PeerRotationLiveState.CutOver;
                if (variant == 3) a.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");
            });
            var client = new PeerRotationStatusRpcClient(a.Runtime, transport);
            if (variant == 2) Check(await client.CheckAsync(b.State.HostId, b.Address, Owner(a)) == PeerRotationStatusExchange.Unchanged);
            else
            {
                var refused = false;
                try { await client.CheckAsync(b.State.HostId, b.Address, Owner(a)); }
                catch (AuthenticationException) when (variant is 0 or 3) { refused = true; }
                catch (ArgumentException) when (variant == 1) { refused = true; }
                Check(refused);
            }
            Check(transport.Altered == 1);
            var after = a.Runtime.Repository.Read(b.State.HostId)!;
            Check(after.CurrentFingerprint == retained.CurrentFingerprint && after.PendingFingerprint == retained.PendingFingerprint && after.PendingRotationExpiresUtc == retained.PendingRotationExpiresUtc && a.State.Count("TrustedManagerCredentialHistory") == 0);
            NoAuthority(a, b);
        }
    }
}
