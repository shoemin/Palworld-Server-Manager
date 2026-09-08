using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed record PeerReplacementApprovalResult(Guid PeerHostId,Guid ReplacementId,long Revision,long Incarnation,
    bool Changed,int InvalidatedGrants,int CreatedDefaultGrants);

public sealed partial class GrantPolicyRepository
{
    private const string ReplacementCompletionColumns="PeerHostId,ReplacementId,SourceIncarnation,ApprovalIncarnation,CurrentIncarnation,ApprovedPeerFingerprint,LocalFingerprintAtApproval,ApprovedUtc,ConfirmedUtc,InvalidatedUtc";
    private const string ReplacementEvidenceColumns="ReplacementId,SourceIncarnation,LocalFingerprint,VerifiedUtc";
    private const string LocalBindingColumns="PeerHostId,Incarnation,LocalFingerprint,BoundUtc";
    private const string PairingColumns="PeerHostId,BoundUtc,ExpiresUtc,LocalBoundPublicKeyFingerprint";
    private const string UnpairReceiptColumns="PeerHostId,SourceIncarnation,RevokedIncarnation,PeerFingerprint,LocalFingerprint,ReceivedUtc";

    // Actual current local Owner evidence only. No network or receiver acknowledgement
    // participates in this transaction; the completion marker is security state only.
    public PeerReplacementApprovalResult ApprovePeerReplacement(LocalPrincipalMutationActor owner,long expectedRevision,
        Guid replacementId,CancellationToken ct=default)
    {
        Id(replacementId);ct.ThrowIfCancellationRequested();using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var before=Read(c,tx);RequireLocal(c,tx,owner,before);var actor=ActorRef.LocalPrincipal(owner.LocalPrincipalId);
        if(!before.Policy.IsOwner(actor))throw new UnauthorizedAccessException("Local Owner approval required.");
        RequireRevision(expectedRevision,before.Revision);var credential=RevocationCredential(c,tx);
        var trust=RevocationRows(c,tx,"TrustedManagers",TrustRevocationColumns);
        var candidates=RevocationRows(c,tx,"PendingCredentialReplacements",ReplacementRevocationColumns);
        var evidence=RevocationRows(c,tx,"PeerReplacementBindingEvidence",ReplacementEvidenceColumns);
        var bindings=RevocationRows(c,tx,"PeerLocalBindingEvidence",LocalBindingColumns);
        var pairings=RevocationRows(c,tx,"TrustedManagerPairings",PairingColumns);
        var receipts=RevocationRows(c,tx,"PeerUnpairReceipts",UnpairReceiptColumns);
        var completions=RevocationRows(c,tx,"PeerReplacementCompletions",ReplacementCompletionColumns);
        if(!candidates.TryGetValue(Id(replacementId),out var candidate))throw new InvalidOperationException("Replacement request unavailable.");
        var peer=ParseId((string)candidate[1]);if(peer==hostId||!trust.TryGetValue(Id(peer),out var target))throw new InvalidOperationException("Replacement peer unavailable.");
        var source=PeerRelationshipIncarnation.Read(c,tx,peer);
        if(candidate[5] is not DBNull)
        {
            if(candidate[10] is not DBNull||!completions.TryGetValue(Id(peer),out var marker)||!Equals(marker[1],Id(replacementId))||
                marker[9] is not DBNull||!Equals(marker[4],source)||!Equals(target[1],"Active")||!Equals(target[2],marker[5]))
                throw new InvalidOperationException("Approved replacement is no longer current.");
            ct.ThrowIfCancellationRequested();tx.Commit();return new(peer,replacementId,before.Revision,source,false,0,0);
        }
        var now=time.GetUtcNow();var stamp=Stamp(now);var proposed=candidate[2] as string;
        if(candidate[4] is not DBNull||candidate[10] is not DBNull||!HostTrustPlanning.Fingerprint(proposed)||Equals(proposed,target[2])||
            !Equals(candidate[7],target[1])||!Equals(candidate[8],target[2])||target[3] is not DBNull||
            ParseTime((string)candidate[6])<=now||ParseTime((string)candidate[6])!=ParseTime((string)candidate[3])+PeerTrustRepository.PendingLifetime||
            !Equals(candidate[9],candidate[3])||!evidence.TryGetValue(Id(replacementId),out var proof)||
            !Equals(proof[1],source)||!Equals(proof[2],credential.Fingerprint)||!Equals(proof[3],candidate[3]))
            throw new InvalidOperationException("Fresh non-conflicting verified replacement required.");
        var peerActor=ActorRef.RemoteManager(peer);
        var hostIds=before.HostGrants.Where(g=>g.GranteeActor==peerActor||g.GrantedByActor==peerActor).SelectMany(g=>before.Policy.HostSubtree(g.GrantId)).ToHashSet();
        var serverIds=before.ServerGrants.Where(g=>g.GranteeActor==peerActor||g.GrantedByActor==peerActor).SelectMany(g=>before.Policy.ServerSubtree(g.GrantId)).ToHashSet();
        var oldHosts=before.HostGrants.Where(g=>hostIds.Contains(g.GrantId)&&g.InvalidatedUtc is null).ToArray();
        var oldServers=before.ServerGrants.Where(g=>serverIds.Contains(g.GrantId)&&g.InvalidatedUtc is null).ToArray();
        var invalidated=checked(oldHosts.Length+oldServers.Length);
        foreach(var row in candidates.Values.Where(row=>Equals(row[1],Id(peer))&&row[5] is DBNull&&row[10] is DBNull))
        {
            if(Equals(row[0],Id(replacementId)))
            {
                Execute(c,tx,"UPDATE PendingCredentialReplacements SET ApprovedByOwnerLocalPrincipalId=$owner,ApprovedUtc=$now WHERE ReplacementId=$id;",
                    ("$owner",Id(owner.LocalPrincipalId)),("$now",stamp),("$id",row[0]));row[4]=Id(owner.LocalPrincipalId);row[5]=stamp;
            }
            else
            {
                Execute(c,tx,"UPDATE PendingCredentialReplacements SET InvalidatedUtc=$now WHERE ReplacementId=$id;",("$now",stamp),("$id",row[0]));row[10]=stamp;
            }
        }
        foreach(var (table,ids) in new[]{("HostCapabilityGrants",oldHosts.Select(g=>g.GrantId)),("ServerCapabilityGrants",oldServers.Select(g=>g.GrantId))})
            foreach(var id in ids)Execute(c,tx,$"UPDATE {table} SET InvalidatedUtc=$now WHERE GrantId=$id;",("$now",stamp),("$id",Id(id)));
        Execute(c,tx,"DELETE FROM PeerReplacementCompletions WHERE PeerHostId=$peer; DELETE FROM TrustedManagerPairings WHERE PeerHostId=$peer;",("$peer",Id(peer)));
        pairings.Remove(Id(peer));receipts.Remove(Id(peer));
        Execute(c,tx,"""
            UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint=$fp,
                PendingTrustedPublicKeyFingerprint=NULL,PendingRotationId=NULL,PendingRotationExpiresUtc=NULL,
                PendingReconfirmationRequired=0,PairedUtc=$now,RevokedUtc=NULL WHERE PeerHostId=$peer;
            """,("$fp",proposed),("$now",stamp),("$peer",Id(peer)));
        target[1]="Active";target[2]=proposed!;for(var i=3;i<=5;i++)target[i]=DBNull.Value;
        target[6]=0L;target[10]=stamp;target[12]=DBNull.Value; // PeerRecoveryRequired is deliberately preserved.
        var incarnation=PeerRelationshipIncarnation.Read(c,tx,peer);
        if(incarnation<=source)throw new InvalidOperationException("Replacement incarnation did not advance.");
        Execute(c,tx,"""
            INSERT INTO PeerLocalBindingEvidence (PeerHostId,Incarnation,LocalFingerprint,BoundUtc) VALUES ($peer,$inc,$local,$now)
            ON CONFLICT(PeerHostId) DO UPDATE SET Incarnation=excluded.Incarnation,LocalFingerprint=excluded.LocalFingerprint,BoundUtc=excluded.BoundUtc;
            INSERT INTO PeerReplacementCompletions (PeerHostId,ReplacementId,SourceIncarnation,ApprovalIncarnation,CurrentIncarnation,ApprovedPeerFingerprint,LocalFingerprintAtApproval,ApprovedUtc)
            VALUES ($peer,$replacement,$source,$inc,$inc,$fp,$local,$now);
            """,("$peer",Id(peer)),("$inc",incarnation),("$local",credential.Fingerprint),("$now",stamp),
            ("$replacement",Id(replacementId)),("$source",source),("$fp",proposed));
        bindings[Id(peer)]=[Id(peer),incarnation,credential.Fingerprint,stamp];
        completions[Id(peer)]=[Id(peer),Id(replacementId),source,incarnation,incarnation,proposed!,credential.Fingerprint,stamp,DBNull.Value,DBNull.Value];
        var context=new ApprovedReplacementContext(owner,peer,replacementId,incarnation,proposed!,credential.Fingerprint,now);
        var defaultsCheck=ApplyDefaults(c,tx,new(hostId,peer,now,actor),context);
        var issued=Read(c,tx);var oldHostIds=before.HostGrants.Select(g=>g.GrantId).ToHashSet();var oldServerIds=before.ServerGrants.Select(g=>g.GrantId).ToHashSet();
        var newHosts=issued.HostGrants.Where(g=>!oldHostIds.Contains(g.GrantId)).ToArray();var newServers=issued.ServerGrants.Where(g=>!oldServerIds.Contains(g.GrantId)).ToArray();
        var created=checked(newHosts.Length+newServers.Length);
        var audit=WriteSuccessAudit(c,tx,actor,hostId,null,"PeerCredentialReplacementApproved",now,
            $"Peer={Id(peer)}; Replacement={Id(replacementId)}; PreviousIncarnation={source}; Incarnation={incarnation}; InvalidatedGrants={invalidated}; CreatedDefaults={created}.");
        var after=Read(c,tx);RequireLocal(c,tx,owner,after);
        if(!after.Policy.IsOwner(actor))throw new UnauthorizedAccessException("Current replacement Owner required.");
        RequireRevision(checked(before.Revision+invalidated+1+created),after.Revision);
        var expectedHosts=before.HostGrants.Select(g=>hostIds.Contains(g.GrantId)&&g.InvalidatedUtc is null?
            new HostCapabilityGrant(g.GrantId,g.GranteeActor,g.Capability,g.TargetHostId,g.Rights,g.GrantedByActor,g.DerivedFromGrantId,g.GrantedUtc,now):g).Concat(newHosts);
        var expectedServers=before.ServerGrants.Select(g=>serverIds.Contains(g.GrantId)&&g.InvalidatedUtc is null?
            new ServerCapabilityGrant(g.GrantId,g.GranteeActor,g.Capability,g.Target,g.Rights,g.GrantedByActor,g.DerivedFromGrantId,g.GrantedUtc,now):g).Concat(newServers);
        if(!expectedHosts.OrderBy(g=>g.GrantId).SequenceEqual(after.HostGrants.OrderBy(g=>g.GrantId))||!expectedServers.OrderBy(g=>g.GrantId).SequenceEqual(after.ServerGrants.OrderBy(g=>g.GrantId)))
            throw new InvalidOperationException("Replacement grant effects changed before commit.");
        foreach(var (expected,table,columns) in new[]{(trust,"TrustedManagers",TrustRevocationColumns),(candidates,"PendingCredentialReplacements",ReplacementRevocationColumns),
            (evidence,"PeerReplacementBindingEvidence",ReplacementEvidenceColumns),(bindings,"PeerLocalBindingEvidence",LocalBindingColumns),(pairings,"TrustedManagerPairings",PairingColumns),
            (receipts,"PeerUnpairReceipts",UnpairReceiptColumns),(completions,"PeerReplacementCompletions",ReplacementCompletionColumns)})
            RequireRevocationRows(expected,RevocationRows(c,tx,table,columns));
        if(RevocationCredential(c,tx)!=credential||PeerRelationshipIncarnation.Read(c,tx,peer)!=incarnation)
            throw new InvalidOperationException("Replacement credential or relationship changed before commit.");
        defaultsCheck();audit();ct.ThrowIfCancellationRequested();tx.Commit();
        return new(peer,replacementId,after.Revision,incarnation,true,invalidated,created);
    }
}
