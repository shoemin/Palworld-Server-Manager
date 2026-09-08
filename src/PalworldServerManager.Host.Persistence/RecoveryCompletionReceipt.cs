using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public enum RecoveryCompletionDisposition { Recorded=1,AlreadyRecorded=2,KeyMismatch=3 }
public sealed record PeerRecoveryCompletionResult(Guid PeerHostId,Guid ApprovalId,long Revision,long Incarnation,
    bool RecoveryCleared,RecoveryCompletionDisposition Disposition);

public sealed partial class GrantPolicyRepository
{
    private const string RecoveryReceiptColumns="PeerHostId,ApprovalId,SourceIncarnation,ResultIncarnation,PeerFingerprint,LocalFingerprint,ReceivedUtc";

    // A fixed intrinsic security receipt, using original completed connection evidence.
    // This is not ordinary peer authorization and cannot issue grants or promote rotation.
    public PeerRecoveryCompletionResult ReceiveAuthenticatedRecoveryCompletion(PeerGrantMutationActor actor,Guid approvalId,
        string acknowledgedFingerprint,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(actor);Id(approvalId);
        if(!HostTrustPlanning.Fingerprint(acknowledgedFingerprint))throw new ArgumentException("Approved key fingerprint required.");
        if(actor.HostId!=hostId||actor.PeerHostId==Guid.Empty||actor.PeerHostId==hostId||actor.Incarnation<=0||
            !HostTrustPlanning.Fingerprint(actor.PeerFingerprint)||!HostTrustPlanning.Fingerprint(actor.LocalFingerprint))throw CompletionRefused();
        ct.ThrowIfCancellationRequested();using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var before=Read(c,tx);var credential=RevocationCredential(c,tx);
        if(credential.Fingerprint!=actor.LocalFingerprint)throw CompletionRefused();
        var tables=new[]{("TrustedManagers",TrustRevocationColumns),("PendingCredentialReplacements",ReplacementRevocationColumns),
            ("PeerReplacementBindingEvidence",ReplacementEvidenceColumns),("PeerLocalBindingEvidence",LocalBindingColumns),
            ("TrustedManagerPairings",PairingColumns),("PeerUnpairReceipts",UnpairReceiptColumns),
            ("PeerReplacementCompletions",ReplacementCompletionColumns),("PeerRecoveryCompletionReceipts",RecoveryReceiptColumns),
            ("PeerRelationshipIncarnations","PeerHostId,Incarnation")};
        var rows=tables.ToDictionary(t=>t.Item1,t=>RevocationRows(c,tx,t.Item1,t.Item2));
        var key=Id(actor.PeerHostId);
        if(!rows["TrustedManagers"].TryGetValue(key,out var target))throw CompletionRefused();
        RequireCompletionPeerKey(target,actor.PeerFingerprint);
        var source=PeerRelationshipIncarnation.Read(c,tx,actor.PeerHostId);
        var receipts=rows["PeerRecoveryCompletionReceipts"];
        if(acknowledgedFingerprint==credential.Fingerprint&&Equals(target[7],0L)&&receipts.TryGetValue(key,out var prior)&&
            Equals(prior[1],Id(approvalId))&&Equals(prior[3],source)&&Equals(prior[4],actor.PeerFingerprint)&&Equals(prior[5],actor.LocalFingerprint)&&
            (Equals(prior[2],actor.Incarnation)||Equals(prior[3],actor.Incarnation)))
        {
            ct.ThrowIfCancellationRequested();tx.Commit();
            return new(actor.PeerHostId,approvalId,before.Revision,source,false,RecoveryCompletionDisposition.AlreadyRecorded);
        }
        if(actor.Incarnation!=source)throw CompletionRefused();
        var now=time.GetUtcNow();var stamp=Stamp(now);var resultIncarnation=source;
        var mismatch=acknowledgedFingerprint!=credential.Fingerprint;
        var cleared=!mismatch&&Equals(target[7],1L);
        if(cleared)
        {
            // Canonical timestamp before generic recovery/incarnation invalidation triggers.
            foreach(var candidate in rows["PendingCredentialReplacements"].Values.Where(r=>Equals(r[1],key)&&r[5] is DBNull&&r[10] is DBNull))
            {
                Execute(c,tx,"UPDATE PendingCredentialReplacements SET InvalidatedUtc=$now WHERE ReplacementId=$id;",("$now",stamp),("$id",candidate[0]));
                candidate[10]=stamp;
            }
            Execute(c,tx,"UPDATE TrustedManagers SET PeerRecoveryRequired=0 WHERE PeerHostId=$peer;",("$peer",key));
            target[7]=0L;resultIncarnation=PeerRelationshipIncarnation.Read(c,tx,actor.PeerHostId);
            if(resultIncarnation<=source)throw new InvalidOperationException("Recovery completion incarnation did not advance.");
            rows["PeerRelationshipIncarnations"][key]=[key,resultIncarnation];
            rows["PeerUnpairReceipts"].Remove(key);receipts.Remove(key);
            if(rows["PeerReplacementCompletions"].TryGetValue(key,out var marker)&&marker[9] is DBNull&&
                Equals(marker[4],source)&&Equals(marker[5],target[2]))
            {
                // Same approved peer binding; only this qualified recovery-clear transition
                // carries the outgoing marker. Approval/source/local-key fields stay history.
                Execute(c,tx,"UPDATE PeerReplacementCompletions SET CurrentIncarnation=$inc WHERE PeerHostId=$peer;",
                    ("$inc",resultIncarnation),("$peer",key));marker[4]=resultIncarnation;
            }
        }
        if(!mismatch)
        {
            Execute(c,tx,"""
                INSERT INTO PeerRecoveryCompletionReceipts (PeerHostId,ApprovalId,SourceIncarnation,ResultIncarnation,PeerFingerprint,LocalFingerprint,ReceivedUtc)
                VALUES ($peer,$approval,$source,$result,$remote,$local,$now)
                ON CONFLICT(PeerHostId) DO UPDATE SET ApprovalId=excluded.ApprovalId,SourceIncarnation=excluded.SourceIncarnation,
                    ResultIncarnation=excluded.ResultIncarnation,PeerFingerprint=excluded.PeerFingerprint,LocalFingerprint=excluded.LocalFingerprint,ReceivedUtc=excluded.ReceivedUtc;
                """,("$peer",key),("$approval",Id(approvalId)),("$source",source),("$result",resultIncarnation),
                ("$remote",actor.PeerFingerprint),("$local",actor.LocalFingerprint),("$now",stamp));
            receipts[key]=[key,Id(approvalId),source,resultIncarnation,actor.PeerFingerprint,actor.LocalFingerprint,stamp];
        }
        var audit=WriteSuccessAudit(c,tx,ActorRef.RemoteManager(actor.PeerHostId),hostId,null,
            mismatch?"PeerRecoveryCompletionKeyMismatch":"PeerRecoveryCompletionReceived",now,
            $"Peer={key}; Approval={Id(approvalId)}; PreviousIncarnation={source}; Incarnation={resultIncarnation}; RecoveryCleared={cleared}.");
        var after=Read(c,tx);RequireRevision(checked(before.Revision+(cleared?1:0)),after.Revision);
        if(!before.HostGrants.OrderBy(g=>g.GrantId).SequenceEqual(after.HostGrants.OrderBy(g=>g.GrantId))||
            !before.ServerGrants.OrderBy(g=>g.GrantId).SequenceEqual(after.ServerGrants.OrderBy(g=>g.GrantId)))
            throw new InvalidOperationException("Recovery receipt changed grants.");
        foreach(var (table,columns) in tables)RequireRevocationRows(rows[table],RevocationRows(c,tx,table,columns));
        if(RevocationCredential(c,tx)!=credential||PeerRelationshipIncarnation.Read(c,tx,actor.PeerHostId)!=resultIncarnation)
            throw CompletionRefused();
        audit();ct.ThrowIfCancellationRequested();tx.Commit();
        return new(actor.PeerHostId,approvalId,after.Revision,resultIncarnation,cleared,
            mismatch?RecoveryCompletionDisposition.KeyMismatch:RecoveryCompletionDisposition.Recorded);
    }

    private static AuthenticationException CompletionRefused()=>new("Current recovery-completion connection proof required.");
    private static void RequireCompletionPeerKey(object[] trust,string actual)
    {
        if(!Equals(trust[1],"Active")||!HostTrustPlanning.Fingerprint(trust[2] as string)||
            (!Equals(trust[2],actual)&&!Equals(trust[3],actual)))throw CompletionRefused();
        if(trust[4] is string rotation)ParseId(rotation);
        if(trust[3] is DBNull)
        {
            if(trust[5] is not DBNull||!Equals(trust[6],0L))throw Corrupt();
        }
        else if(!HostTrustPlanning.Fingerprint(trust[3] as string)||Equals(trust[2],trust[3])||trust[4] is DBNull||trust[5] is DBNull)
            throw Corrupt();
        // A durably staged key remains live-valid after lapse, as in canonical observation.
        // This receipt preserves all staging/reconfirmation metadata and never promotes it.
    }
}
