using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class GrantPolicyRepository
{
    // Trusted completed TLS proof only. Unlike receiving an acknowledgment, this cannot
    // overwrite a superseded receipt. Used before/after attesting it on a fresh connection.
    public void RequireRecordedRecoveryCompletion(PeerGrantMutationActor actor,Guid approvalId,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(actor);Id(approvalId);ct.ThrowIfCancellationRequested();
        if(actor.HostId!=hostId||actor.PeerHostId==Guid.Empty||actor.PeerHostId==hostId||actor.Incarnation<=0||
            !HostTrustPlanning.Fingerprint(actor.PeerFingerprint)||!HostTrustPlanning.Fingerprint(actor.LocalFingerprint))throw CompletionRefused();
        using var c=Open(true);using var tx=c.BeginTransaction(deferred:true);Read(c,tx);
        if(RevocationCredential(c,tx).Fingerprint!=actor.LocalFingerprint||PeerRelationshipIncarnation.Read(c,tx,actor.PeerHostId)!=actor.Incarnation)
            throw CompletionRefused();
        var key=Id(actor.PeerHostId);var trusts=RevocationRows(c,tx,"TrustedManagers",TrustRevocationColumns);
        if(!trusts.TryGetValue(key,out var trust))throw CompletionRefused();
        RequireCompletionPeerKey(trust,actor.PeerFingerprint);
        if(!Equals(trust[2],actor.PeerFingerprint)||!Equals(trust[7],0L))throw CompletionRefused();
        var receipts=RevocationRows(c,tx,"PeerRecoveryCompletionReceipts",RecoveryReceiptColumns);
        if(!receipts.TryGetValue(key,out var receipt)||!Equals(receipt[1],Id(approvalId))||!Equals(receipt[3],actor.Incarnation)||
            !Equals(receipt[4],actor.PeerFingerprint)||!Equals(receipt[5],actor.LocalFingerprint))throw CompletionRefused();
        ParseTime((string)receipt[6]);ct.ThrowIfCancellationRequested();
    }
}
