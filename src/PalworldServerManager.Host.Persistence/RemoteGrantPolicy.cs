using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

// Trusted completed TLS/negotiation evidence from Host composition, NEVER a request DTO.
public sealed record PeerGrantMutationActor(Guid HostId,Guid PeerHostId,string PeerFingerprint,string LocalFingerprint,long Incarnation);

public sealed partial class GrantPolicyRepository
{
    private void RequirePeer(SqliteConnection c,SqliteTransaction tx,PeerGrantMutationActor actor,AuthorizationSnapshot snapshot)
    {
        if(actor.HostId!=hostId||actor.PeerHostId==Guid.Empty||actor.PeerHostId==hostId||actor.Incarnation<=0||
            !HostTrustPlanning.Fingerprint(actor.PeerFingerprint)||!HostTrustPlanning.Fingerprint(actor.LocalFingerprint)||
            !snapshot.Policy.IsActive(ActorRef.RemoteManager(actor.PeerHostId)))throw new AuthenticationException("Current peer identity required.");
        using var cmd=Command(c,tx,"""
            SELECT COUNT(*) FROM TrustedManagers t CROSS JOIN HostIdentity h
                JOIN SecureCredentialReferences s ON s.CredentialRef=h.CurrentCredentialRef
            WHERE t.PeerHostId=$peer AND t.State='Active' AND t.PeerRecoveryRequired=0
                AND (t.CurrentTrustedPublicKeyFingerprint=$remote OR
                    (t.PendingTrustedPublicKeyFingerprint=$remote AND t.PendingRotationId IS NOT NULL AND t.PendingRotationExpiresUtc IS NOT NULL))
                AND h.Id=1 AND h.HostId=$host AND h.HostBootstrapState='Initialized'
                AND s.PublicKeyFingerprint=$local AND s.Purpose='HostTlsV1' AND s.RetiredUtc IS NULL;
            """,("$peer",Id(actor.PeerHostId)),("$remote",actor.PeerFingerprint),("$host",Id(hostId)),("$local",actor.LocalFingerprint));
        if(Convert.ToInt32(cmd.ExecuteScalar())!=1||PeerRelationshipIncarnation.Read(c,tx,actor.PeerHostId)!=actor.Incarnation)
            throw new AuthenticationException("Current peer identity required.");
    }

    // These evidence adapters are private: request data cannot inject a validator or actor.
    private sealed record GrantWriter(ActorRef Actual,Action<SqliteConnection,SqliteTransaction,AuthorizationSnapshot> Require);
    private GrantWriter LocalWriter(LocalPrincipalMutationActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new(ActorRef.LocalPrincipal(actor.LocalPrincipalId),(c,tx,snapshot)=>RequireLocal(c,tx,actor,snapshot));
    }
    private GrantWriter PeerWriter(PeerGrantMutationActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new(ActorRef.RemoteManager(actor.PeerHostId),(c,tx,snapshot)=>RequirePeer(c,tx,actor,snapshot));
    }
    private void RequireIncomingTarget(Guid targetHostId)
    {
        if(targetHostId!=hostId)throw new UnauthorizedAccessException("Incoming grants must target this Host.");
    }
    // Host-only, authenticated completed-transport evidence, never a request-body actor.
    // Outgoing local-user permission checks and RPC integration remain Host obligations.
    public GrantMutationResult IssueRemoteHost(PeerGrantMutationActor actor,long expectedRevision,Guid grantId,ActorRef grantee,
        HostCapability capability,Guid targetHostId,DelegationRights rights,Guid? sourceGrantId,CancellationToken ct=default)
        =>Issue(PeerWriter(actor),expectedRevision,(policy,utc)=>
        {
            RequireIncomingTarget(targetHostId);
            return policy.IssueHost(ActorRef.RemoteManager(actor.PeerHostId),grantId,grantee,capability,targetHostId,rights,sourceGrantId,utc);
        },ct);
    public GrantMutationResult IssueRemoteServer(PeerGrantMutationActor actor,long expectedRevision,Guid grantId,ActorRef grantee,
        ServerCapability capability,ServerRef target,DelegationRights rights,Guid? sourceGrantId,CancellationToken ct=default)
        =>Issue(PeerWriter(actor),expectedRevision,(policy,utc)=>
        {
            ArgumentNullException.ThrowIfNull(target);RequireIncomingTarget(target.AuthoritativeHostId);
            return policy.IssueServer(ActorRef.RemoteManager(actor.PeerHostId),grantId,grantee,capability,target,rights,sourceGrantId,utc);
        },ct);
    public PresetGrantResult ApplyRemotePreset(PeerGrantMutationActor actor,long expectedRevision,RolePreset preset,CancellationToken ct=default)
        =>ApplyPreset(PeerWriter(actor),expectedRevision,preset,ct);
}
