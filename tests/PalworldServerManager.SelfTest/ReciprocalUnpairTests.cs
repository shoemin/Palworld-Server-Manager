using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.SelfTest;

internal static partial class PeerTrustRevocationTests
{
    public static Task ReciprocalNoticeIsSelfOnlyAndAtomic()
    {
        using var r=new Rig();var proof=r.Peer;var other=r.F.Repository.Read(r.Other);var revision=r.Revision;
        Check(!r.Repo.Read().Policy.CanUseHost(ActorRef.RemoteManager(r.F.PeerId),HostCapability.ManageTrustedManagers,r.F.HostId));
        var result=r.Repo.ReceiveAuthenticatedPeerUnpair(proof);
        Check(result.Changed&&result.PeerHostId==proof.PeerHostId&&result.InvalidatedGrants==5&&result.InvalidatedReplacements==1&&result.Revision==revision+6);
        Check(r.F.Repository.Read(r.F.PeerId)!.State=="Revoked"&&r.F.Repository.Read(r.Other)==other&&r.F.Count("PeerUnpairReceipts")==1);
        Check(r.Repo.Read().Policy.IsOwner(ActorRef.LocalPrincipal(r.F.OwnerId))&&!r.F.Repository.RecognizesTransportFingerprint(proof.PeerFingerprint));
        Check(HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked' AND ActorPeerHostId='{r.F.PeerId:D}' AND ActorLocalPrincipalId IS NULL AND Summary LIKE '%Origin=ReceivedUnpair%';")==1);
        return Task.CompletedTask;
    }
    public static Task ReciprocalDuplicatePreservesNewPendingIntent()
    {
        using var r=new Rig();var proof=r.Peer;var first=r.Repo.ReceiveAuthenticatedPeerUnpair(proof);
        var candidate=r.F.Repository.RecordVerifiedBinding(r.F.PeerId,proof.PeerFingerprint,proof.LocalFingerprint);
        Check(candidate.Disposition==PeerBindingDisposition.ReplacementRequired);
        var snapshot=r.Snapshot();var reopened=new GrantPolicyRepository(r.F.Database,r.F.HostId,r.F.Time);
        var duplicate=reopened.ReceiveAuthenticatedPeerUnpair(proof);
        Check(!duplicate.Changed&&duplicate.Incarnation==first.Incarnation&&duplicate.InvalidatedGrants==0&&duplicate.InvalidatedReplacements==0&&r.Snapshot()==snapshot);
        Check(!r.Repo.ReceiveAuthenticatedPeerUnpair(proof).Changed&&r.Snapshot()==snapshot);
        Check(HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM PendingCredentialReplacements WHERE ReplacementId='{candidate.ReplacementId:D}' AND InvalidatedUtc IS NULL;")==1);
        return Task.CompletedTask;
    }
    public static Task ReciprocalReceiptNeverRestoresOrdinaryOrLaterTrust()
    {
        using var r=new Rig();var proof=r.Peer;var first=r.Repo.ReceiveAuthenticatedPeerUnpair(proof);var snapshot=r.Snapshot();
        Reject<AuthenticationException>(()=>r.Repo.RequireRemoteHostCapability(proof,HostCapability.ManageTrustedManagers,r.F.HostId));
        Reject<AuthenticationException>(()=>r.Repo.RevokeRemotePeerTrust(proof,r.Revision,r.Other,OtherIncarnation(r)));
        foreach(var bad in new[]{proof with{HostId=r.Other},proof with{PeerHostId=r.Other},proof with{PeerFingerprint=new('F',64)},
            proof with{LocalFingerprint=new('F',64)},proof with{Incarnation=first.Incarnation}})
        {Reject<AuthenticationException>(()=>r.Repo.ReceiveAuthenticatedPeerUnpair(bad));Check(r.Snapshot()==snapshot);}
        r.F.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{new string('F',64)}' WHERE CredentialRef='current';");
        snapshot=r.Snapshot();Reject<AuthenticationException>(()=>r.Repo.ReceiveAuthenticatedPeerUnpair(proof));Check(r.Snapshot()==snapshot);
        r.F.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{proof.LocalFingerprint}' WHERE CredentialRef='current';");
        // Explicit artificial restoration is negative ABA evidence, not positive repair.
        r.F.Execute($"UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{proof.PeerFingerprint}' WHERE PeerHostId='{r.F.PeerId:D}';");
        Check(r.F.Count("PeerUnpairReceipts")==0);snapshot=r.Snapshot();
        Reject<AuthenticationException>(()=>r.Repo.ReceiveAuthenticatedPeerUnpair(proof));Check(r.Snapshot()==snapshot);
        var fresh=r.Peer;var next=r.Repo.ReceiveAuthenticatedPeerUnpair(fresh);
        Check(next.Changed&&next.Incarnation>first.Incarnation&&r.F.Count("PeerUnpairReceipts")==1);
        Reject<AuthenticationException>(()=>r.Repo.ReceiveAuthenticatedPeerUnpair(proof));
        return Task.CompletedTask;
    }
    public static Task ReciprocalReceiptAndAuditFaultsRollback()
    {
        var faults=new[]{
            "DELETE FROM PeerUnpairReceipts;",
            "UPDATE PeerUnpairReceipts SET SourceIncarnation=SourceIncarnation+1;",
            "UPDATE PeerUnpairReceipts SET RevokedIncarnation=$otherInc;",
            "UPDATE PeerUnpairReceipts SET PeerHostId='$other';",
            "UPDATE PeerUnpairReceipts SET PeerFingerprint='$pin';",
            "UPDATE PeerUnpairReceipts SET LocalFingerprint='$pin';",
            "UPDATE PeerUnpairReceipts SET ReceivedUtc='changed';",
            "DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='$pin' WHERE PeerHostId='$peer';"
        };
        foreach(var fault in faults)
        {
            using var r=new Rig();var sql=fault.Replace("$otherInc",OtherIncarnation(r).ToString()).Replace("$other",r.Other.ToString("D"))
                .Replace("$peer",r.F.PeerId.ToString("D")).Replace("$pin",new string('F',64));
            r.F.Execute($"CREATE TRIGGER ReceiptFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerTrustRevoked' BEGIN {sql} END;");
            var snapshot=r.Snapshot();var refused=false;
            try{r.Repo.ReceiveAuthenticatedPeerUnpair(r.Peer);}
            catch(Exception e)when(e is AuthenticationException or InvalidOperationException or InvalidDataException or SqliteException or StaleAuthorizationRevisionException){refused=true;}
            if(!refused||r.Snapshot()!=snapshot)throw new Exception("Unpair receipt fault did not roll back: "+fault);
        }
        return Task.CompletedTask;
    }
    public static async Task ReciprocalConcurrencyAndLateCancellation()
    {
        using(var r=new Rig())
        {
            var proof=r.Peer;var results=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>r.Repo.ReceiveAuthenticatedPeerUnpair(proof))));
            Check(results.Count(r=>r.Changed)==1&&results.Select(r=>r.Incarnation).Distinct().Count()==1&&r.F.Count("PeerUnpairReceipts")==1);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked';")==1);
        }
        using(var r=new Rig())using(var ct=new CancellationTokenSource())
        {
            var snapshot=r.Snapshot();var repo=new GrantPolicyRepository(r.F.Database,r.F.HostId,new CancelClock(ct,r.F.Time.Now));
            Reject<OperationCanceledException>(()=>repo.ReceiveAuthenticatedPeerUnpair(r.Peer,ct.Token));Check(r.Snapshot()==snapshot);
        }
    }
    public static Task ReciprocalSchemaUpgradeAndIncarnationCascade()
    {
        using var f=new PeerTrustTests.Fixture(schemaVersion:12);f.Repository.RecordVerifiedBinding(f.PeerId,new('B',64),new('A',64));
        f.Execute("UPDATE TrustedManagers SET State='Active';");var before=f.Repository.Read(f.PeerId);var grants=new GrantPolicyRepository(f.Database,f.HostId,f.Time).Read();var audits=f.Count("AuditEvents");
        var runner=new HostSchemaMigrationRunner(HostSchema.AllMigrations().Where(m=>m.Version<=13));
        Check(runner.Migrate(f.Writer)==1&&runner.Migrate(f.Writer)==0&&f.Count("PeerUnpairReceipts")==0&&f.Repository.Read(f.PeerId)==before&&f.Count("AuditEvents")==audits);
        // The assertions above qualify the historical 12-to-13 migration. Current writers
        // execute only after the database has reached the current complete schema.
        HostSchemaMigrationRunner.Default().Migrate(f.Writer);
        var repo=new GrantPolicyRepository(f.Database,f.HostId,f.Time);Check(repo.Read().Revision==grants.Revision&&repo.Read().HostGrants.Count==0&&repo.Read().ServerGrants.Count==0);
        var proof=new PeerGrantMutationActor(f.HostId,f.PeerId,new('B',64),new('A',64),HostDatabase.QueryScalarLong(f.Writer,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{f.PeerId:D}';"));
        Check(repo.ReceiveAuthenticatedPeerUnpair(proof).Changed&&f.Count("PeerUnpairReceipts")==1);
        f.Execute($"UPDATE TrustedManagerPairings SET BoundUtc=BoundUtc WHERE PeerHostId='{f.PeerId:D}';");
        Check(f.Count("PeerUnpairReceipts")==0&&f.Repository.Read(f.PeerId)!.State=="Revoked");
        Reject<AuthenticationException>(()=>repo.ReceiveAuthenticatedPeerUnpair(proof));return Task.CompletedTask;
    }
    public static Task ReciprocalNonActiveAndAbsentEvidenceRefuse()
    {
        foreach(var state in new[]{"State='PeerBound'","PeerRecoveryRequired=1"})
        {
            using var r=new Rig(false);r.F.Execute($"UPDATE TrustedManagers SET {state} WHERE PeerHostId='{r.F.PeerId:D}';");var snapshot=r.Snapshot();
            Reject<AuthenticationException>(()=>r.Repo.ReceiveAuthenticatedPeerUnpair(r.Peer));Check(r.Snapshot()==snapshot);
        }
        using(var r=new Rig())
        {
            var proof=r.Peer;r.Revoke();var snapshot=r.Snapshot();
            Reject<AuthenticationException>(()=>r.Repo.ReceiveAuthenticatedPeerUnpair(proof));Check(r.Snapshot()==snapshot&&r.F.Count("PeerUnpairReceipts")==0);
        }
        return Task.CompletedTask;
    }
}
