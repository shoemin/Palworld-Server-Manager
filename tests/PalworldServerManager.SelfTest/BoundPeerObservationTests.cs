using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class BoundPeerObservationTests
{
    private static readonly string Local=new('A',64),Old=new('B',64),New=new('C',64),Other=new('D',64);
    private static void Check(bool value){if(!value)throw new Exception("Bound peer observation assertion failed.");}
    private static void Reject<T>(Action action)where T:Exception
    {try{action();}catch(T){return;}throw new Exception("Expected bound observation refusal: "+typeof(T).Name);}
    private sealed class Rig:IDisposable
    {
        internal readonly PeerTrustTests.Fixture F=new();
        internal readonly Guid Rotation=Guid.NewGuid();
        internal Rig(bool pending=true,bool expired=false)
        {
            F.Execute($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{F.PeerId:D}','Active','{Old}','{F.Time.Now:O}');");
            if(pending)F.Execute($"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{New}',PendingRotationId='{Rotation:D}',PendingRotationExpiresUtc='{F.Time.Now.AddMinutes(expired?-1:5):O}' WHERE PeerHostId='{F.PeerId:D}';");
        }
        internal PeerGrantMutationActor Proof(bool pending=false)=>new(F.HostId,F.PeerId,pending?New:Old,Local,
            HostDatabase.QueryScalarLong(F.Writer,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{F.PeerId:D}';"));
        internal PeerTrustRecord Trust=>F.Repository.Read(F.PeerId)!;
        internal long Revision=>new GrantPolicyRepository(F.Database,F.HostId,F.Time).Read().Revision;
        public void Dispose()=>F.Dispose();
    }
    public static Task CurrentLapsedAndPendingPreserveIdentityAndExactEffects()
    {
        using var r=new Rig();var proof=r.Proof();var before=r.Trust;var revision=r.Revision;var audits=r.F.Count("AuditEvents");
        var current=r.F.Repository.ObserveActivePeerCredential(proof);
        Check(!current.Promoted&&current.Trust==before&&r.Revision==revision&&r.F.Count("AuditEvents")==audits);
        r.F.Time.Now+=TimeSpan.FromMinutes(6);
        var lapsed=r.F.Repository.ObserveActivePeerCredential(proof);
        Check(!lapsed.Promoted&&lapsed.Trust==before with{PendingReconfirmationRequired=true}&&r.Revision==revision+1);
        Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerRotationReconfirmationRequired' AND ActorKind IS NULL AND ActorLocalPrincipalId IS NULL AND ActorPeerHostId IS NULL;")==1);
        Check(!r.F.Repository.ObserveActivePeerCredential(proof).Promoted&&r.Revision==revision+1);
        var pending=proof with{PeerFingerprint=New};var promoted=r.F.Repository.ObserveActivePeerCredential(pending);
        Check(promoted.Promoted&&promoted.Trust==before with{CurrentFingerprint=New,PendingFingerprint=null,PendingRotationExpiresUtc=null,PendingReconfirmationRequired=false});
        Check(promoted.Trust.PendingRotationId==r.Rotation&&r.Proof().Incarnation==proof.Incarnation&&r.Revision==revision+2);
        Check(r.F.Count("TrustedManagerCredentialHistory")==1&&r.F.Count("HostCapabilityGrants")==0&&r.F.Count("ServerCapabilityGrants")==0);
        Check(HostDatabase.QueryScalarText(r.F.Writer,"SELECT PriorPublicKeyFingerprint FROM TrustedManagerCredentialHistory;")==Old);
        using(var cmd=r.F.Writer.CreateCommand())
        {
            cmd.CommandText="SELECT ActorKind,ActorPeerHostId,AffectedHostId,Summary FROM AuditEvents WHERE EventKind='PeerCredentialPromoted';";using var reader=cmd.ExecuteReader();
            Check(reader.Read()&&reader.GetString(0)=="RemoteManager"&&reader.GetString(1)==r.F.PeerId.ToString("D")&&reader.GetString(2)==r.F.HostId.ToString("D")&&reader.GetString(3)==$"PeerCredentialPromoted: peer {r.F.PeerId:D}.");
        }
        Reject<AuthenticationException>(()=>r.F.Repository.ObserveActivePeerCredential(proof));
        Check(!r.F.Repository.ObserveActivePeerCredential(pending).Promoted&&r.Revision==revision+2&&r.F.Count("AuditEvents")==audits+2);
        return Task.CompletedTask;
    }
    public static Task WrongConnectionAndInactiveTrustNeverObserve()
    {
        using var r=new Rig(expired:true);var before=r.Trust;var revision=r.Revision;var actor=r.Proof(true);
        foreach(var bad in new[]{actor with{HostId=Guid.NewGuid()},actor with{PeerHostId=r.F.HostId},actor with{PeerFingerprint=Other},actor with{LocalFingerprint=Other},actor with{Incarnation=actor.Incarnation+1},actor with{Incarnation=0}})
            Reject<AuthenticationException>(()=>r.F.Repository.ObserveActivePeerCredential(bad));
        Reject<ArgumentException>(()=>r.F.Repository.ObserveActivePeerCredential(actor with{PeerFingerprint="malformed"}));
        Reject<ArgumentNullException>(()=>r.F.Repository.ObserveActivePeerCredential(null!));
        Check(r.Trust==before&&r.Revision==revision&&r.F.Count("AuditEvents")==0&&r.F.Count("TrustedManagerCredentialHistory")==0);
        foreach(var change in new[]{"State='PeerBound'","PeerRecoveryRequired=1","State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL"})
        {
            using var inactive=new Rig(pending:false);var proof=inactive.Proof();inactive.F.Execute($"UPDATE TrustedManagers SET {change} WHERE PeerHostId='{inactive.F.PeerId:D}';");
            Reject<AuthenticationException>(()=>inactive.F.Repository.ObserveActivePeerCredential(proof));Check(inactive.F.Count("AuditEvents")==0);
        }
        return Task.CompletedTask;
    }
    public static Task AuditHistoryAndLateProofMutationsRollBack()
    {
        foreach(var promote in new[]{false,true})foreach(var mode in Enumerable.Range(0,10))
        {
            using var r=new Rig(expired:true);var proof=r.Proof(promote);var before=r.Trust;var revision=r.Revision;
            var sql=mode switch
            {
                0=>"SELECT RAISE(ABORT,'observation audit unavailable');",
                1=>"DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId;",
                2=>"UPDATE AuditEvents SET Summary='changed' WHERE AuditEventId=NEW.AuditEventId;",
                3=>promote?"DELETE FROM TrustedManagerCredentialHistory;":$"UPDATE TrustedManagers SET PendingRotationExpiresUtc='{r.F.Time.Now.AddMinutes(20):O}' WHERE PeerHostId='{r.F.PeerId:D}';",
                4=>promote?$"UPDATE TrustedManagerCredentialHistory SET PriorPublicKeyFingerprint='{Other}';":$"UPDATE TrustedManagers SET PendingRotationId='{Guid.NewGuid():D}' WHERE PeerHostId='{r.F.PeerId:D}';",
                5=>$"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{Other}' WHERE CredentialRef='current';",
                6=>$"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';",
                7=>$"DELETE FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.F.PeerId:D}'; INSERT INTO PeerRelationshipIncarnations (PeerHostId) VALUES ('{r.F.PeerId:D}');",
                8=>$"UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE LocalPrincipalId='{r.F.OwnerId:D}';",
                _=>$"UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{Other}' WHERE PeerHostId='{r.F.PeerId:D}';"
            };
            r.F.Execute($"CREATE TRIGGER ObservationFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind IN ('PeerCredentialPromoted','PeerRotationReconfirmationRequired') BEGIN {sql} END;");
            Action apply=()=>r.F.Repository.ObserveActivePeerCredential(proof);
            if(mode==0)Reject<SqliteException>(apply);
            else if(mode is 5 or 6 or 7 or 9)Reject<AuthenticationException>(apply);
            else if(mode==8||(!promote&&mode is 3 or 4))Reject<StaleAuthorizationRevisionException>(apply);
            else Reject<InvalidOperationException>(apply);
            Check(r.Trust==before&&r.Revision==revision&&r.F.Count("AuditEvents")==0&&r.F.Count("TrustedManagerCredentialHistory")==0&&r.Proof().Incarnation==proof.Incarnation);
            r.F.Execute("DROP TRIGGER ObservationFault;");r.F.Repository.ObserveActivePeerCredential(proof);Check(r.F.Count("AuditEvents")==1);
        }
        return Task.CompletedTask;
    }
    private sealed class CancelAtObservation(TimeProvider actual,CancellationTokenSource cancellation):TimeProvider
    {public override DateTimeOffset GetUtcNow(){cancellation.Cancel();return actual.GetUtcNow();}}
    public static Task CancellationConcurrencyAndMissingRevisionFailClosed()
    {
        foreach(var promote in new[]{false,true})
        {
            using var r=new Rig(expired:true);var before=r.Trust;var revision=r.Revision;using var ct=new CancellationTokenSource();
            var repo=new PeerTrustRepository(r.F.Database,r.F.HostId,new CancelAtObservation(r.F.Time,ct));
            Reject<OperationCanceledException>(()=>repo.ObserveActivePeerCredential(r.Proof(promote),ct.Token));
            Check(r.Trust==before&&r.Revision==revision&&r.F.Count("AuditEvents")==0&&r.F.Count("TrustedManagerCredentialHistory")==0);
            Reject<OperationCanceledException>(()=>r.F.Repository.ObserveActivePeerCredential(r.Proof(promote),ct.Token));
        }
        using(var r=new Rig())
        {
            var proof=r.Proof(true);var revision=r.Revision;int promoted=0;
            Parallel.For(0,8,_=>{if(r.F.Repository.ObserveActivePeerCredential(proof).Promoted)Interlocked.Increment(ref promoted);});
            Check(promoted==1&&r.Revision==revision+1&&r.F.Count("AuditEvents")==1&&r.F.Count("TrustedManagerCredentialHistory")==1&&r.Proof().Incarnation==proof.Incarnation);
        }
        using(var r=new Rig())
        {
            var proof=r.Proof(true);var before=r.Trust;r.F.Execute("DELETE FROM AuthorizationRevision;");
            Reject<InvalidDataException>(()=>r.F.Repository.ObserveActivePeerCredential(proof));Check(r.Trust==before&&r.F.Count("AuditEvents")==0&&r.F.Count("TrustedManagerCredentialHistory")==0);
        }
        return Task.CompletedTask;
    }
}
