using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

// Trusted completed TLS/negotiation evidence from Host composition, NEVER a request DTO.
public sealed record PeerGrantMutationActor(Guid HostId,Guid PeerHostId,string PeerFingerprint,string LocalFingerprint,long Incarnation);
public sealed record CreatorGrantResult(Guid CreationEventId,long Revision,IReadOnlyList<Guid> GrantIds);

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
    private sealed record CreationInventory(string Name,string? Path,int GamePort,int RestPort,string? Import,string Created);
    private static CreationInventory? ReadCreationInventory(SqliteConnection c,SqliteTransaction tx,ServerRef target)
    {
        using var cmd=Command(c,tx,"""
            SELECT DisplayName,InstallPath,GamePort,RestApiPort,ImportProvenance,CreatedUtc FROM ServerInventory
                WHERE AuthoritativeHostId=$host AND ServerProfileId=$server;
            """,("$host",Id(target.AuthoritativeHostId)),("$server",Id(target.ServerProfileId)));
        using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),r.IsDBNull(1)?null:r.GetString(1),r.GetInt32(2),r.GetInt32(3),
            r.IsDBNull(4)?null:r.GetString(4),r.GetString(5)):null;
    }
    // The callback is trusted Host code that persists its ALREADY confirmed successful
    // creation, only on this connection/transaction. No external IO, nested commit or wire
    // success flag. Actual executor and local-user outbound checks remain Host obligations.
    public CreatorGrantResult CommitConfirmedRemoteCreation(PeerGrantMutationActor creator,long expectedRevision,ServerRef target,
        Action<SqliteConnection,SqliteTransaction> recordConfirmedCreation,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(creator);ArgumentNullException.ThrowIfNull(target);ArgumentNullException.ThrowIfNull(recordConfirmedCreation);
        ct.ThrowIfCancellationRequested();using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var before=Read(c,tx);RequirePeer(c,tx,creator,before);RequireRevision(expectedRevision,before.Revision);
        if(target.AuthoritativeHostId!=hostId||!before.Policy.CanUseHost(ActorRef.RemoteManager(creator.PeerHostId),HostCapability.CreateServer,hostId))
            throw new UnauthorizedAccessException("Authorized remote creator required.");
        if(ReadCreationInventory(c,tx,target) is not null||before.ServerGrants.Any(g=>g.Target==target))
            throw new UnauthorizedAccessException("Creator grants require a new server identity.");
        recordConfirmedCreation(c,tx);ct.ThrowIfCancellationRequested();
        var inventory=ReadCreationInventory(c,tx,target)??throw new InvalidOperationException("Host creation confirmation did not register the new server.");
        var confirmed=Read(c,tx);RequirePeer(c,tx,creator,confirmed);RequireRevision(before.Revision,confirmed.Revision);
        var grants=confirmed.Policy.ExpandRemoteCreatorGrants(creator.PeerHostId,target,time.GetUtcNow());
        var creationEvent=Guid.NewGuid();var actual=ActorRef.RemoteManager(creator.PeerHostId);
        foreach(var grant in grants)
        {
            Insert(c,tx,grant);
            Audit(c,tx,actual,grant,"CreatorGrantApplied",grant.GrantedUtc,1,"ConfirmedServerCreation:"+Id(creationEvent));
        }
        var after=Read(c,tx);RequirePeer(c,tx,creator,after);RequireRevision(checked(before.Revision+grants.Count),after.Revision);
        if(ReadCreationInventory(c,tx,target)!=inventory)throw new UnauthorizedAccessException("Confirmed server changed before commit.");
        foreach(var grant in grants)
            if(after.ServerGrants.Single(g=>g.GrantId==grant.GrantId)!=grant||!after.Policy.IsOwner(grant.GrantedByActor))
                throw new UnauthorizedAccessException("Creator grant changed before commit.");
        ct.ThrowIfCancellationRequested();tx.Commit();
        return new(creationEvent,after.Revision,Array.AsReadOnly(grants.Select(g=>g.GrantId).ToArray()));
    }
}
