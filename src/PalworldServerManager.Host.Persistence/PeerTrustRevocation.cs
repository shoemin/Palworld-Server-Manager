using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed record PeerTrustRevocationResult(Guid PeerHostId,long Revision,long Incarnation,
    bool Changed,int InvalidatedGrants,int InvalidatedReplacements,long PreviousIncarnation);

public sealed partial class GrantPolicyRepository
{
    // Authenticated local-channel evidence only. A revision/incarnation is a stale-request
    // guard, not authority. No transport callback can hold up this local transaction.
    public PeerTrustRevocationResult RevokeLocalPeerTrust(LocalPrincipalMutationActor actor,long expectedRevision,
        Guid peerHostId,long expectedIncarnation,CancellationToken ct=default)
        =>RevokePeerTrust(LocalWriter(actor),expectedRevision,peerHostId,expectedIncarnation,ct);

    // Original completed TLS/negotiation proof, never a request-selected identity. The
    // originating local user's independent ceiling remains an outbound Host obligation.
    public PeerTrustRevocationResult RevokeRemotePeerTrust(PeerGrantMutationActor actor,long expectedRevision,
        Guid peerHostId,long expectedIncarnation,CancellationToken ct=default)
        =>RevokePeerTrust(PeerWriter(actor),expectedRevision,peerHostId,expectedIncarnation,ct);

    private PeerTrustRevocationResult RevokePeerTrust(GrantWriter writer,long? expectedRevision,
        Guid peerHostId,long expectedIncarnation,CancellationToken ct,PeerGrantMutationActor? receivedNotice=null)
    {
        Id(peerHostId);
        if(peerHostId==hostId||expectedIncarnation<=0)throw new ArgumentException("A current remote relationship is required.");
        ct.ThrowIfCancellationRequested();
        using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var before=Read(c,tx);
        if(receivedNotice is not null&&TryReceivedUnpair(c,tx,receivedNotice,out var previousIncarnation))
        {
            ct.ThrowIfCancellationRequested();tx.Commit();
            return new(peerHostId,before.Revision,previousIncarnation,false,0,0,expectedIncarnation);
        }
        writer.Require(c,tx,before);
        if(expectedRevision is {} expected)RequireRevision(expected,before.Revision);
        var credential=RevocationCredential(c,tx);
        if(receivedNotice is null)AuthorizeOrAudit(c,tx,writer,before.Revision,new("RevokePeerTrust",hostId,null,"Peer="+Id(peerHostId)),()=>
        {
            if(!before.Policy.CanUseHost(writer.Actual,HostCapability.ManageTrustedManagers,hostId))
                throw new UnauthorizedAccessException("Trust revocation refused.");
            return true;
        },ct);
        if(PeerRelationshipIncarnation.Read(c,tx,peerHostId)!=expectedIncarnation)
            throw new InvalidOperationException("Peer relationship changed; refresh before retrying.");

        // Exact expected rows protect unrelated state and metadata from late write/audit
        // effects, including tables whose updates do not advance AuthorizationRevision.
        var trust=RevocationRows(c,tx,"TrustedManagers",TrustRevocationColumns);
        if(!trust.TryGetValue(Id(peerHostId),out var target))throw new InvalidOperationException("Peer relationship unavailable.");
        var pending=RevocationRows(c,tx,"PendingCredentialReplacements",ReplacementRevocationColumns);
        var revoked=ActorRef.RemoteManager(peerHostId);
        var hostIds=before.HostGrants.Where(g=>g.GranteeActor==revoked||g.GrantedByActor==revoked)
            .SelectMany(g=>before.Policy.HostSubtree(g.GrantId)).ToHashSet();
        var serverIds=before.ServerGrants.Where(g=>g.GranteeActor==revoked||g.GrantedByActor==revoked)
            .SelectMany(g=>before.Policy.ServerSubtree(g.GrantId)).ToHashSet();
        var hs=before.HostGrants.Where(g=>hostIds.Contains(g.GrantId)&&g.InvalidatedUtc is null).ToArray();
        var ss=before.ServerGrants.Where(g=>serverIds.Contains(g.GrantId)&&g.InvalidatedUtc is null).ToArray();
        var candidates=pending.Values.Where(row=>Equals(row[1],Id(peerHostId))&&row[10] is DBNull).ToArray();
        var trustChanged=!Equals(target[1],"Revoked")||target[12] is DBNull;
        if(!trustChanged&&hs.Length==0&&ss.Length==0&&candidates.Length==0)
        {ct.ThrowIfCancellationRequested();tx.Commit();return new(peerHostId,before.Revision,expectedIncarnation,false,0,0,expectedIncarnation);}

        var now=time.GetUtcNow();var stamp=Stamp(now);
        if(trustChanged)
        {
            Execute(c,tx,"""
                UPDATE TrustedManagers SET State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL,
                    PendingTrustedPublicKeyFingerprint=NULL,PendingRotationId=NULL,PendingRotationExpiresUtc=NULL,
                    PendingReconfirmationRequired=0,PeerRecoveryRequired=0,RevokedUtc=$now WHERE PeerHostId=$peer;
                """,("$peer",Id(peerHostId)),("$now",stamp));
            target[1]="Revoked";for(var i=2;i<=5;i++)target[i]=DBNull.Value;
            target[6]=0L;target[7]=0L;target[12]=stamp;
        }
        var incarnation=PeerRelationshipIncarnation.Read(c,tx,peerHostId);
        if(trustChanged?incarnation<=expectedIncarnation:incarnation!=expectedIncarnation)
            throw new InvalidOperationException("Revocation incarnation did not advance correctly.");
        foreach(var row in candidates)
        {
            Execute(c,tx,"UPDATE PendingCredentialReplacements SET InvalidatedUtc=$now WHERE ReplacementId=$id AND InvalidatedUtc IS NULL;",
                ("$now",stamp),("$id",row[0]));row[10]=stamp;
        }
        foreach(var (table,ids) in new[]{("HostCapabilityGrants",hs.Select(g=>g.GrantId)),("ServerCapabilityGrants",ss.Select(g=>g.GrantId))})
            foreach(var id in ids)Execute(c,tx,$"UPDATE {table} SET InvalidatedUtc=$now WHERE GrantId=$id AND InvalidatedUtc IS NULL;",
                ("$now",stamp),("$id",Id(id)));
        var changed=checked(hs.Length+ss.Length);
        var receiptCheck=receivedNotice is null?null:WriteReceivedUnpair(c,tx,receivedNotice,incarnation,stamp);
        var audit=WriteSuccessAudit(c,tx,writer.Actual,hostId,null,"PeerTrustRevoked",now,
            $"Peer={Id(peerHostId)}; PreviousIncarnation={expectedIncarnation}; Incarnation={incarnation}; InvalidatedGrants={changed}; InvalidatedReplacements={candidates.Length}; Origin={(receivedNotice is null?"Administration":"ReceivedUnpair")}.");
        var after=Read(c,tx);
        // Only the authenticated peer revoking ITSELF intentionally loses Active proof.
        // Its complete expected tombstone and new incarnation are verified below. All
        // other actors retain current identity; callers cannot supply this exception.
        if(writer.Actual!=revoked)writer.Require(c,tx,after);
        RequireRevision(checked(before.Revision+changed+(trustChanged?1:0)),after.Revision);
        // Revoking a peer may intentionally remove the administrator's own delegated
        // capability. Authorization is checked before mutation; exact effects plus current
        // actor/relationship checks protect this commit without demanding the removed grant afterward.
        var expectedHosts=before.HostGrants.Select(g=>hostIds.Contains(g.GrantId)&&g.InvalidatedUtc is null?
            new HostCapabilityGrant(g.GrantId,g.GranteeActor,g.Capability,g.TargetHostId,g.Rights,g.GrantedByActor,g.DerivedFromGrantId,g.GrantedUtc,now):g);
        var expectedServers=before.ServerGrants.Select(g=>serverIds.Contains(g.GrantId)&&g.InvalidatedUtc is null?
            new ServerCapabilityGrant(g.GrantId,g.GranteeActor,g.Capability,g.Target,g.Rights,g.GrantedByActor,g.DerivedFromGrantId,g.GrantedUtc,now):g);
        if(!expectedHosts.OrderBy(g=>g.GrantId).SequenceEqual(after.HostGrants.OrderBy(g=>g.GrantId))||
            !expectedServers.OrderBy(g=>g.GrantId).SequenceEqual(after.ServerGrants.OrderBy(g=>g.GrantId)))
            throw new InvalidOperationException("Revocation grant effects changed before commit.");
        RequireRevocationRows(trust,RevocationRows(c,tx,"TrustedManagers",TrustRevocationColumns));
        RequireRevocationRows(pending,RevocationRows(c,tx,"PendingCredentialReplacements",ReplacementRevocationColumns));
        if(PeerRelationshipIncarnation.Read(c,tx,peerHostId)!=incarnation)
            throw new InvalidOperationException("Revocation relationship changed before commit.");
        if(RevocationCredential(c,tx)!=credential)throw new InvalidOperationException("Host credential changed before commit.");
        receiptCheck?.Invoke();audit();ct.ThrowIfCancellationRequested();tx.Commit();
        return new(peerHostId,after.Revision,incarnation,true,changed,candidates.Length,expectedIncarnation);
    }

    // Fixed schema-owned identifiers only; no caller-controlled SQL or generic write API.
    private const string TrustRevocationColumns="PeerHostId,State,CurrentTrustedPublicKeyFingerprint,PendingTrustedPublicKeyFingerprint,PendingRotationId,PendingRotationExpiresUtc,PendingReconfirmationRequired,PeerRecoveryRequired,DisplayName,MachineName,PairedUtc,CreatedUtc,RevokedUtc";
    private const string ReplacementRevocationColumns="ReplacementId,PeerHostId,ProposedKeyFingerprint,VerifiedUtc,ApprovedByOwnerLocalPrincipalId,ApprovedUtc,ExpiresUtc,ExpectedTrustState,ExpectedCurrentTrustedPublicKeyFingerprint,CreatedUtc,InvalidatedUtc";
    private static (string Reference,string Fingerprint) RevocationCredential(SqliteConnection c,SqliteTransaction tx)
    {
        using var cmd=Command(c,tx,"""
            SELECT s.CredentialRef,s.PublicKeyFingerprint FROM HostIdentity h
                JOIN SecureCredentialReferences s ON s.CredentialRef=h.CurrentCredentialRef
                WHERE h.Id=1 AND h.HostBootstrapState='Initialized' AND s.Purpose='HostTlsV1' AND s.RetiredUtc IS NULL;
            """);using var reader=cmd.ExecuteReader();
        if(!reader.Read()||reader.IsDBNull(1)||!HostTrustPlanning.Fingerprint(reader.GetString(1)))
            throw new InvalidDataException("Current Host credential unavailable.");
        return(reader.GetString(0),reader.GetString(1));
    }
    private static Dictionary<string,object[]> RevocationRows(SqliteConnection c,SqliteTransaction tx,string table,string columns)
    {
        using var cmd=Command(c,tx,$"SELECT {columns} FROM {table};");using var reader=cmd.ExecuteReader();
        var rows=new Dictionary<string,object[]>(StringComparer.Ordinal);
        while(reader.Read()){var values=new object[reader.FieldCount];reader.GetValues(values);rows.Add(reader.GetString(0),values);}
        return rows;
    }
    private static void RequireRevocationRows(Dictionary<string,object[]> expected,Dictionary<string,object[]> actual)
    {
        if(expected.Count!=actual.Count||expected.Any(pair=>!actual.TryGetValue(pair.Key,out var row)||!pair.Value.SequenceEqual(row)))
            throw new InvalidOperationException("Revocation state changed before commit.");
    }
}
