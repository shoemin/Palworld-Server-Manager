using System.Security.Authentication;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static partial class PeerTrustRevocationTests
{
    public static Task CommittedUnpairGuardRequiresExactTransition()
    {
        using var r=new Rig(false);var proof=r.Peer;
        var fake=new PeerTrustRevocationResult(proof.PeerHostId,r.Revision,proof.Incarnation+1,true,0,0,proof.Incarnation);
        var snapshot=r.Snapshot();Reject<AuthenticationException>(()=>r.Repo.RequireCommittedUnpair(proof,fake));Check(r.Snapshot()==snapshot);
        var revoked=r.Revoke();Check(revoked.PreviousIncarnation==proof.Incarnation&&revoked.Incarnation>proof.Incarnation);
        r.Repo.RequireCommittedUnpair(proof,revoked);snapshot=r.Snapshot();
        foreach(var bad in new[]{revoked with{Changed=false},revoked with{PeerHostId=r.Other},revoked with{PreviousIncarnation=revoked.Incarnation},revoked with{Incarnation=revoked.Incarnation+1}})
            Reject<AuthenticationException>(()=>r.Repo.RequireCommittedUnpair(proof,bad));
        foreach(var bad in new[]{proof with{HostId=r.Other},proof with{PeerHostId=r.Other},proof with{LocalFingerprint=new('F',64)},proof with{PeerFingerprint="bad"},proof with{Incarnation=proof.Incarnation+1}})
            Reject<AuthenticationException>(()=>r.Repo.RequireCommittedUnpair(bad,revoked));
        using var canceled=new CancellationTokenSource();canceled.Cancel();Reject<OperationCanceledException>(()=>r.Repo.RequireCommittedUnpair(proof,revoked,canceled.Token));Check(r.Snapshot()==snapshot);
        r.F.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{new string('F',64)}' WHERE CredentialRef='current';");
        Reject<AuthenticationException>(()=>r.Repo.RequireCommittedUnpair(proof,revoked));
        r.F.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{proof.LocalFingerprint}' WHERE CredentialRef='current';");
        r.F.Execute($"UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{proof.PeerFingerprint}' WHERE PeerHostId='{proof.PeerHostId:D}';");
        Reject<AuthenticationException>(()=>r.Repo.RequireCommittedUnpair(proof,revoked));
        var later=r.Revoke();Reject<AuthenticationException>(()=>r.Repo.RequireCommittedUnpair(proof,later));return Task.CompletedTask;
    }
}
