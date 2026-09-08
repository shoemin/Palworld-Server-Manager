using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed record PendingPeerRecoveryCompletion(Guid PeerHostId,Guid ApprovalId,string ApprovedPeerFingerprint,long Incarnation);
public sealed record PeerRecoveryConfirmationResult(Guid PeerHostId,Guid ApprovalId,long Revision,long Incarnation,bool Changed);

public sealed partial class GrantPolicyRepository
{
    // Trusted completed TLS proof only. The future Host sender must correlate a positive
    // wire reply before calling confirmation; neither seam is a public request adapter.
    public PendingPeerRecoveryCompletion? ReadPendingRecoveryCompletion(PeerGrantMutationActor actor,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();using var c=Open(true);using var tx=c.BeginTransaction(deferred:true);
        Read(c,tx);var marker=RequireRecoveryConfirmation(c,tx,actor);
        ct.ThrowIfCancellationRequested();
        return marker is null||marker[8] is not DBNull?null:
            new(actor.PeerHostId,ParseId((string)marker[1]),(string)marker[5],actor.Incarnation);
    }

    public PeerRecoveryConfirmationResult ConfirmAuthenticatedRecoveryCompletion(PeerGrantMutationActor actor,Guid approvalId,CancellationToken ct=default)
    {
        Id(approvalId);ct.ThrowIfCancellationRequested();using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var before=Read(c,tx);var marker=RequireRecoveryConfirmation(c,tx,actor);
        if(marker is null||!Equals(marker[1],Id(approvalId)))throw CompletionRefused();
        if(marker[8] is not DBNull)
        {ct.ThrowIfCancellationRequested();tx.Commit();return new(actor.PeerHostId,approvalId,before.Revision,actor.Incarnation,false);}
        var rows=ConfirmationTables.ToDictionary(name=>name,name=>ReadConfirmationRows(c,tx,name));
        var markers=RevocationRows(c,tx,"PeerReplacementCompletions",ReplacementCompletionColumns);
        var credential=RevocationCredential(c,tx);var now=time.GetUtcNow();var stamp=Stamp(now);
        Execute(c,tx,"UPDATE PeerReplacementCompletions SET ConfirmedUtc=$now WHERE PeerHostId=$peer;",("$now",stamp),("$peer",Id(actor.PeerHostId)));
        markers[Id(actor.PeerHostId)][8]=stamp;
        var audit=WriteSuccessAudit(c,tx,ActorRef.RemoteManager(actor.PeerHostId),hostId,null,"PeerRecoveryCompletionConfirmed",now,
            $"Peer={Id(actor.PeerHostId)}; Approval={Id(approvalId)}; Incarnation={actor.Incarnation}.");
        var after=Read(c,tx);RequireRevision(before.Revision,after.Revision);
        if(!before.HostGrants.OrderBy(g=>g.GrantId).SequenceEqual(after.HostGrants.OrderBy(g=>g.GrantId))||
            !before.ServerGrants.OrderBy(g=>g.GrantId).SequenceEqual(after.ServerGrants.OrderBy(g=>g.GrantId)))
            throw new InvalidOperationException("Recovery confirmation changed grants.");
        foreach(var table in ConfirmationTables)RequireRevocationRows(rows[table],ReadConfirmationRows(c,tx,table));
        RequireRevocationRows(markers,RevocationRows(c,tx,"PeerReplacementCompletions",ReplacementCompletionColumns));
        if(RevocationCredential(c,tx)!=credential)throw CompletionRefused();
        var final=RequireRecoveryConfirmation(c,tx,actor);
        if(final is null||!final.SequenceEqual(markers[Id(actor.PeerHostId)]))throw CompletionRefused();
        audit();ct.ThrowIfCancellationRequested();tx.Commit();
        return new(actor.PeerHostId,approvalId,after.Revision,actor.Incarnation,true);
    }

    private object[]? RequireRecoveryConfirmation(SqliteConnection c,SqliteTransaction tx,PeerGrantMutationActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if(actor.HostId!=hostId||actor.PeerHostId==Guid.Empty||actor.PeerHostId==hostId||actor.Incarnation<=0||
            !HostTrustPlanning.Fingerprint(actor.PeerFingerprint)||!HostTrustPlanning.Fingerprint(actor.LocalFingerprint))throw CompletionRefused();
        if(RevocationCredential(c,tx).Fingerprint!=actor.LocalFingerprint||PeerRelationshipIncarnation.Read(c,tx,actor.PeerHostId)!=actor.Incarnation)
            throw CompletionRefused();
        var peer=Id(actor.PeerHostId);var trust=RevocationRows(c,tx,"TrustedManagers",TrustRevocationColumns);
        if(!trust.TryGetValue(peer,out var target))throw CompletionRefused();
        RequireCompletionPeerKey(target,actor.PeerFingerprint); // Fixed intrinsic path may retain independent recovery.
        var markers=RevocationRows(c,tx,"PeerReplacementCompletions",ReplacementCompletionColumns);
        if(!markers.TryGetValue(peer,out var marker))return null;
        if(marker[9] is not DBNull||!Equals(marker[4],actor.Incarnation)||!Equals(marker[5],actor.PeerFingerprint)||!Equals(marker[5],target[2]))
            throw CompletionRefused();
        var approval=ParseId((string)marker[1]);ParseTime((string)marker[7]);
        if(marker[8] is string confirmed)ParseTime(confirmed);
        var candidates=RevocationRows(c,tx,"PendingCredentialReplacements",ReplacementRevocationColumns);
        var proofs=RevocationRows(c,tx,"PeerReplacementBindingEvidence",ReplacementEvidenceColumns);
        if(!candidates.TryGetValue(Id(approval),out var candidate)||!proofs.TryGetValue(Id(approval),out var evidence)||
            !Equals(candidate[1],peer)||!Equals(candidate[2],marker[5])||candidate[4] is not string owner||
            !Equals(candidate[5],marker[7])||!Equals(evidence[1],marker[2])||!Equals(evidence[2],marker[6])||!Equals(evidence[3],candidate[3]))
            throw CompletionRefused();
        ParseId(owner);
        // Candidate invalidation and original local fingerprint are history: qualified
        // own recovery invalidates the candidate and changes the current local key,
        // while preserving the approval's original local fingerprint in this marker.
        return marker;
    }

    // Fixed schema-owned tables only. Capture complete rows even where a field mutation
    // does not increment AuthorizationRevision; these rows are never authority inputs.
    private static readonly string[] ConfirmationTables=["HostIdentity","SecureCredentialReferences","HostCredentialRotations",
        "LocalPrincipals","TrustedManagers","TrustedManagerPairings","PeerRelationshipIncarnations",
        "PendingCredentialReplacements","PeerReplacementBindingEvidence","PeerLocalBindingEvidence","PeerRecoveryCompletionReceipts",
        "PeerUnpairReceipts","HostCapabilityGrants","ServerCapabilityGrants","AuthorizationRevision",
        "DefaultGrantTemplateState","HostDefaultGrants","ServerDefaultGrants"];
    private static Dictionary<string,object[]> ReadConfirmationRows(SqliteConnection c,SqliteTransaction tx,string table)
    {
        using var cmd=Command(c,tx,$"SELECT * FROM {table};");using var reader=cmd.ExecuteReader();
        var rows=new Dictionary<string,object[]>(StringComparer.Ordinal);
        while(reader.Read())
        {
            var values=new object[reader.FieldCount];reader.GetValues(values);
            var key=table=="ServerDefaultGrants"?string.Join('\0',values.Take(3)):Convert.ToString(values[0],System.Globalization.CultureInfo.InvariantCulture)!;
            rows.Add(key,values);
        }
        return rows;
    }
}
