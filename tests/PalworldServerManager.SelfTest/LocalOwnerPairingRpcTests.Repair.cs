using System.Security.Authentication;
using Grpc.Core;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using HostCapability = PalworldServerManager.Core.Authorization.HostCapability;
using ActorRef = PalworldServerManager.Core.Authorization.ActorRef;

namespace PalworldServerManager.SelfTest;

internal static partial class LocalOwnerPairingRpcTests
{
    private static GrantPolicyRepository RepairGrants(Fixture local) => new(local.State.State.Database, local.State.State.HostId);
    private static long RepairIncarnation(Fixture local, Fixture peer) => HostDatabase.QueryScalarLong(local.State.State.Writer,
        $"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{peer.State.State.HostId:D}';");
    private static long RepairEvents(Fixture local, string name) => HostDatabase.QueryScalarLong(local.State.State.Writer,
        $"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='{name}';");
    private static void RepairCheck(bool value, string detail)
    { if (!value) throw new Exception("Native re-pair qualification: " + detail); }
    private static void RepairRefuse(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is InvalidOperationException or AuthenticationException or UnauthorizedAccessException) { return; }
        throw new Exception("Native re-pair qualification: expected canonical refusal.");
    }
    internal static async Task NativeRevocationAndRepair(IPairingKeyExchangeFactory provider)
    {
        await using var a = new Fixture(); await using var b = new Fixture();
        var ga = RepairGrants(a); var gb = RepairGrants(b);
        await a.Start(provider, ga.CreateDefaultActivationHook()); await b.Start(provider, gb.CreateDefaultActivationHook());
        using var localA = a.Client(); using var localB = b.Client();
        await NegotiateActivation(localA, true); await NegotiateActivation(localB, true);
        await localA.Authenticate(a.State.State.HostId, a.Principal, a.Key);
        await localB.Authenticate(b.State.State.HostId, b.Principal, b.Key);
        // Pair/activate below use real protected local RPCs. Approval/revoke still use
        // canonical Host commands with separately signature-authenticated local proof;
        // this does not qualify their not-yet-exposed public wire commands.
        using var authA = a.Authentication(); using var authB = b.Authentication();
        var ownerA = authA.GetCurrentPrincipal().MutationActor; var ownerB = authB.GetCurrentPrincipal().MutationActor;
        ga.ConfigureDefaults(ownerA, ga.Read().Revision, new([new(HostCapability.CreateServer, new(true, false))], []));
        gb.ConfigureDefaults(ownerB, gb.Read().Revision, new([new(HostCapability.CreateServer, new(true, false))], []));
        var first = await Create(localB);
        var bound = await Pair(localA, b.Generation.Endpoints!.Value.Pairing, first.Code);
        RepairCheck(bound.LocalResult == PeerPairingResult.PeerBound && bound.RemoteResult == PeerPairingResult.PeerBound, "initial real PAKE");
        RepairCheck(ga.Read().HostGrants.Count == 0 && gb.Read().HostGrants.Count == 0, "PAKE grants nothing");
        await Activate(localA, b.State.State.HostId, b.Generation.Endpoints.Value.Peer);
        var oldA = ga.Read().HostGrants.Single(); var oldB = gb.Read().HostGrants.Single();
        var initialA = RepairIncarnation(a, b); var initialB = RepairIncarnation(b, a);
        RepairCheck(a.State.State.Repository.Read(b.State.State.HostId)!.State == "Active" &&
            b.State.State.Repository.Read(a.State.State.HostId)!.State == "Active", "initial activation commits both");

        var user = Guid.NewGuid();
        a.State.State.Execute($"INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc) VALUES ('{user:D}','repair-fixture-user','repair-fixture-public',0,'Active','fixture');");
        // The unrelated user identity is explicit fixture setup; both grants use canonical writers.
        var peerProof = new PeerGrantMutationActor(a.State.State.HostId, b.State.State.HostId, b.State.Pin, a.State.Pin, initialA);
        var child = ga.IssueRemoteHost(peerProof, ga.Read().Revision, Guid.NewGuid(), ActorRef.LocalPrincipal(user),
            HostCapability.CreateServer, a.State.State.HostId, new(false, false), oldA.GrantId).GrantId;
        var unrelated = ga.IssueHost(ownerA, ga.Read().Revision, Guid.NewGuid(), ActorRef.LocalPrincipal(user),
            HostCapability.ManageHostSettings, a.State.State.HostId, new(false, false), null).GrantId;
        RepairRefuse(() => ga.ApprovePeerReplacement(ownerA, ga.Read().Revision, Guid.NewGuid()));
        var revokedA = ga.RevokeLocalPeerTrust(ownerA, ga.Read().Revision, b.State.State.HostId, initialA);
        var revokedB = gb.RevokeLocalPeerTrust(ownerB, gb.Read().Revision, a.State.State.HostId, initialB);
        RepairCheck(revokedA.Changed && revokedB.Changed && revokedA.Incarnation != initialA && revokedB.Incarnation != initialB, "fresh revoke incarnations");
        RepairCheck(ga.Read().HostGrants.Single(g => g.GrantId == oldA.GrantId).InvalidatedUtc is not null &&
            ga.Read().HostGrants.Single(g => g.GrantId == child).InvalidatedUtc is not null &&
            ga.Read().HostGrants.Single(g => g.GrantId == unrelated).InvalidatedUtc is null, "exact dependent forest only");
        RepairRefuse(() => ga.RequireRemoteHostCapability(peerProof, HostCapability.CreateServer, a.State.State.HostId));
        await Refused(Activate(localA, b.State.State.HostId, b.Generation.Endpoints.Value.Peer), StatusCode.Unavailable);
        await Refused(Activate(localB, a.State.State.HostId, a.Generation.Endpoints!.Value.Peer), StatusCode.Unavailable);

        await Task.Delay(1100); // New explicit invitation, beyond the source cooldown.
        var invitation = await Create(localB);
        var repaired = await Pair(localA, b.Generation.Endpoints.Value.Pairing, invitation.Code);
        RepairCheck(repaired.LocalResult == PeerPairingResult.ReplacementRequired && repaired.RemoteResult == PeerPairingResult.ReplacementRequired,
            "fresh PAKE cannot silently restore revoked identity");
        var approvalA = Guid.Parse(repaired.LocalReplacementId);
        using var candidate = b.State.State.Writer.CreateCommand();
        candidate.CommandText = $"SELECT ReplacementId FROM PendingCredentialReplacements WHERE PeerHostId='{a.State.State.HostId:D}' AND InvalidatedUtc IS NULL AND ApprovedUtc IS NULL;";
        var approvalB = Guid.Parse((string)candidate.ExecuteScalar()!);
        RepairCheck(a.State.State.Repository.Read(b.State.State.HostId)!.State == "Revoked" &&
            b.State.State.Repository.Read(a.State.State.HostId)!.State == "Revoked", "verified candidates carry no Active authority");
        var acceptedA = ga.ApprovePeerReplacement(authA.GetCurrentPrincipal().MutationActor, ga.Read().Revision, approvalA);
        RepairCheck(acceptedA.Changed && acceptedA.CreatedDefaultGrants == 1 &&
            b.State.State.Repository.Read(a.State.State.HostId)!.State == "Revoked", "one Owner approval does not approve the other Host");
        await Refused(Activate(localA, b.State.State.HostId, b.Generation.Endpoints.Value.Peer), StatusCode.Unavailable);
        var acceptedB = gb.ApprovePeerReplacement(authB.GetCurrentPrincipal().MutationActor, gb.Read().Revision, approvalB);
        RepairCheck(acceptedB.Changed && acceptedB.CreatedDefaultGrants == 1, "second independent Owner approval");
        foreach (var (local, remote, grants, old, owner) in new[] { (a, b, ga, oldA, ownerA), (b, a, gb, oldB, ownerB) })
        {
            var current = grants.Read().HostGrants.Single(g => g.GranteeActor == ActorRef.RemoteManager(remote.State.State.HostId) && g.InvalidatedUtc is null);
            RepairCheck(current.GrantId != old.GrantId && current.DerivedFromGrantId is null &&
                current.GrantedByActor == ActorRef.LocalPrincipal(owner.LocalPrincipalId) && current.Capability == HostCapability.CreateServer &&
                current.TargetHostId == local.State.State.HostId && current.Rights == new DelegationRights(true, false), "fresh exact configured Owner root");
            RepairCheck(grants.Read().HostGrants.Single(g => g.GrantId == old.GrantId).InvalidatedUtc is not null, "old root never resurrects");
        }
        RepairCheck(await a.Generation.ConfirmRecoveryAsync(b.State.State.HostId, b.Generation.Endpoints.Value.Peer) == PeerRecoveryCompletionExchange.Confirmed,
            "actual A completion");
        RepairCheck(await b.Generation.ConfirmRecoveryAsync(a.State.State.HostId, a.Generation.Endpoints.Value.Peer) == PeerRecoveryCompletionExchange.Confirmed,
            "actual B completion");
        RepairCheck(await a.Generation.ConfirmRecoveryAsync(b.State.State.HostId, b.Generation.Endpoints.Value.Peer) == PeerRecoveryCompletionExchange.NoPending,
            "exact duplicate completion is read only");
        await Activate(localA, b.State.State.HostId, b.Generation.Endpoints.Value.Peer);
        await Activate(localB, a.State.State.HostId, a.Generation.Endpoints.Value.Peer);
        foreach (var (local, remote, grants, oldInc) in new[] { (a, b, ga, initialA), (b, a, gb, initialB) })
        {
            var inc = RepairIncarnation(local, remote);
            RepairCheck(inc != oldInc, "new relationship cannot reuse original proof");
            grants.RequireRemoteHostCapability(new(local.State.State.HostId, remote.State.State.HostId, remote.State.Pin, local.State.Pin, inc),
                HostCapability.CreateServer, local.State.State.HostId);
            RepairCheck(RepairEvents(local, "PeerRecoveryCompletionReceived") == 1 && RepairEvents(local, "PeerRecoveryCompletionConfirmed") == 1,
                "one receipt and confirmation audit on each Host");
            RepairCheck(RepairEvents(local, "PeerTrustRevoked") == 1 && RepairEvents(local, "PeerCredentialReplacementApproved") == 1,
                "one revocation and explicit Owner approval audit on each Host");
            RepairCheck(HostDatabase.QueryScalarLong(local.State.State.Writer,
                $"SELECT COUNT(*) FROM AuditEvents WHERE EventKind IN ('PeerTrustRevoked','PeerCredentialReplacementApproved') AND ActorKind='LocalPrincipal' AND ActorLocalPrincipalId='{local.Principal:D}' AND ActorPeerHostId IS NULL;") == 2,
                "actual local Owner identified by both security audits");
            RepairCheck(HostDatabase.QueryScalarLong(local.State.State.Writer,
                $"SELECT COUNT(*) FROM LocalPrincipals WHERE IsOwner=1 AND State='Active' AND LocalPrincipalId='{local.Principal:D}';") == 1,
                "current local Owner remains unchanged");
            RepairCheck(HostDatabase.QueryScalarLong(local.State.State.Writer,
                "SELECT COUNT(*) FROM PeerReplacementCompletions WHERE ConfirmedUtc IS NOT NULL AND InvalidatedUtc IS NULL;") == 1, "durable completed original approval");
        }
        RepairCheck(ga.Read().HostGrants.Single(g => g.GrantId == child).InvalidatedUtc is not null &&
            ga.Read().HostGrants.Single(g => g.GrantId == unrelated).InvalidatedUtc is null, "repair preserves invalidated child and unrelated authority");
        RepairRefuse(() => ga.RequireRemoteHostCapability(peerProof, HostCapability.CreateServer, a.State.State.HostId));
        RepairRefuse(() => ga.RequireLocalHostCapability(new(a.State.State.HostId, user, "repair-fixture-user", "repair-fixture-public"),
            HostCapability.CreateServer, a.State.State.HostId));
        Console.WriteLine("PASS genuine native revoke/re-pair: local Owner RPC PAKE, independent canonical Owner approvals, fresh configured roots, old lineage refusal and actual two-sided completion.");
    }
}
