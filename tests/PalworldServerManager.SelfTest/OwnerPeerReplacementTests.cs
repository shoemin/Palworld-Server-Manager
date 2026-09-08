using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.SelfTest;

internal static partial class PeerTrustRevocationTests
{
    private static readonly string ReplacementPin=new('D',64);
    private static Guid ReplacementRequest(Rig r)=>r.F.Repository.RecordOwnerVerifiedBinding(r.Owner,r.F.PeerId,ReplacementPin,new('A',64)).ReplacementId!.Value;
    private static void ReplacementDefaults(Rig r)=>r.Repo.ConfigureDefaults(r.Owner,r.Revision,
        new([new(HostCapability.CreateServer,Use)],[new(ServerCapability.EditSettings,r.Target,Use)]));
    public static Task OwnerReplacementAppliesCurrentDefaultsAndExactForest()
    {
        using var r=new Rig();var request=ReplacementRequest(r);ReplacementDefaults(r);var before=r.Repo.Read();var source=r.Incarnation;
        var applied=r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,request);var after=r.Repo.Read();var peer=ActorRef.RemoteManager(r.F.PeerId);
        Check(applied.Changed&&applied.Incarnation>source&&applied.InvalidatedGrants==5&&applied.CreatedDefaultGrants==2);
        Check(after.Policy.CanUseHost(peer,HostCapability.CreateServer,r.F.HostId)&&after.Policy.CanUseServer(peer,ServerCapability.EditSettings,r.Target));
        Check(!after.Policy.CanUseHost(peer,HostCapability.ManageHostSettings,r.Other)&&after.Policy.IsOwner(ActorRef.LocalPrincipal(r.F.OwnerId)));
        var old=before.HostGrants.Cast<CapabilityGrant>().Concat(before.ServerGrants).ToDictionary(g=>g.GrantId);
        var fresh=after.HostGrants.Cast<CapabilityGrant>().Concat(after.ServerGrants).Where(g=>!old.ContainsKey(g.GrantId)).ToArray();
        Check(fresh.Length==2&&fresh.All(g=>g.GrantedByActor==ActorRef.LocalPrincipal(r.F.OwnerId)&&g.GranteeActor==peer&&g.DerivedFromGrantId is null&&g.Rights==Use));
        Check(after.HostGrants.Where(g=>old.ContainsKey(g.GrantId)).Concat<CapabilityGrant>(after.ServerGrants.Where(g=>old.ContainsKey(g.GrantId))).Count(g=>g.InvalidatedUtc is not null)==5);
        Check(after.Policy.CanUseHost(ActorRef.RemoteManager(r.Other),HostCapability.ManageHostSettings,r.F.HostId));
        Check(r.F.Repository.Read(r.F.PeerId)!.CurrentFingerprint==ReplacementPin&&r.F.Count("PeerReplacementCompletions")==1);
        Check(HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerCredentialReplacementApproved' AND ActorKind='LocalPrincipal' AND ActorLocalPrincipalId='{r.F.OwnerId:D}' AND ActorPeerHostId IS NULL;")==1);
        return Task.CompletedTask;
    }
    public static Task OwnerReplacementHandlesRevokedPeerBoundAndBenignRotation()
    {
        foreach(var kind in Enumerable.Range(0,3))
        {
            using var r=new Rig(false);
            if(kind==0)r.Revoke();
            if(kind==1)r.F.Execute($"UPDATE TrustedManagers SET State='PeerBound' WHERE PeerHostId='{r.F.PeerId:D}';");
            if(kind==2)r.F.Execute($"UPDATE TrustedManagers SET PendingRotationId='{Guid.NewGuid():D}' WHERE PeerHostId='{r.F.PeerId:D}';");
            var request=ReplacementRequest(r);var source=r.Incarnation;var result=r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,request);
            var trust=r.F.Repository.Read(r.F.PeerId)!;
            Check(result.Changed&&result.Incarnation>source&&result.CreatedDefaultGrants==0&&trust.State=="Active"&&trust.CurrentFingerprint==ReplacementPin&&trust.PendingRotationId is null);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM PeerLocalBindingEvidence WHERE PeerHostId='{r.F.PeerId:D}' AND Incarnation={result.Incarnation} AND LocalFingerprint='{new string('A',64)}';")==1);
            Check(HostDatabase.QueryScalarLong(r.F.Writer,$"SELECT COUNT(*) FROM TrustedManagerPairings WHERE PeerHostId='{r.F.PeerId:D}';")==0);
        }
        return Task.CompletedTask;
    }
    public static Task OwnerReplacementRetainsRecoveryAndDeniesOrdinaryAuthority()
    {
        using var r=new Rig(false);ReplacementDefaults(r);
        r.F.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';");
        var result=r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,ReplacementRequest(r));var peer=ActorRef.RemoteManager(r.F.PeerId);var snapshot=r.Snapshot();
        Check(result.CreatedDefaultGrants==2&&r.F.Repository.Read(r.F.PeerId)!.RecoveryRequired&&!r.Repo.Read().Policy.IsActive(peer));
        Check(!r.Repo.Read().Policy.CanUseHost(peer,HostCapability.CreateServer,r.F.HostId)&&!r.Repo.Read().Policy.CanUseServer(peer,ServerCapability.EditSettings,r.Target));
        var audits=r.F.Count("AuditEvents");
        Reject<UnauthorizedAccessException>(()=>r.Repo.IssueHost(r.Owner,r.Revision,Guid.NewGuid(),peer,HostCapability.CreateServer,r.F.HostId,Use,null));
        var denied=r.Snapshot();
        Check(r.F.Count("AuditEvents")==audits+1&&snapshot.Split('\n').Where(s=>!s.StartsWith("AuditEvents")).SequenceEqual(denied.Split('\n').Where(s=>!s.StartsWith("AuditEvents"))));
        Reject<System.Security.Authentication.AuthenticationException>(()=>r.Repo.RequireRemoteHostCapability(new(r.F.HostId,r.F.PeerId,ReplacementPin,new('A',64),result.Incarnation),HostCapability.CreateServer,r.F.HostId));
        using(var tx=r.F.Writer.BeginTransaction())
        {Reject<UnauthorizedAccessException>(()=>r.Repo.CreateDefaultActivationHook().Apply(r.F.Writer,tx,new(r.F.HostId,r.F.PeerId,r.F.Time.Now,ActorRef.LocalPrincipal(r.F.OwnerId))));}
        Check(r.Snapshot()==denied);
        using var changed=new Rig(false);ReplacementDefaults(changed);
        changed.F.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{changed.F.PeerId:D}';");
        var request=ReplacementRequest(changed);
        changed.F.Execute($"CREATE TRIGGER ClearRecoveryDuringApproval AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerCredentialReplacementApproved' BEGIN UPDATE TrustedManagers SET PeerRecoveryRequired=0 WHERE PeerHostId='{changed.F.PeerId:D}'; END;");
        var before=changed.Snapshot();
        Reject<StaleAuthorizationRevisionException>(()=>changed.Repo.ApprovePeerReplacement(changed.Owner,changed.Revision,request));
        Check(before==changed.Snapshot()&&changed.F.Repository.Read(changed.F.PeerId)!.RecoveryRequired);
        return Task.CompletedTask;
    }
    public static Task OwnerReplacementDeniesStaleAndUnprovenRequests()
    {
        foreach(var kind in Enumerable.Range(0,9))
        {
            using var r=new Rig(false);var request=ReplacementRequest(r);var revision=r.Revision;var actor=r.Owner;
            if(kind==0)actor=r.Local;
            if(kind==1)actor=actor with{PublicVerificationKey="wrong"};
            if(kind==2)revision--;
            if(kind==3)r.F.Time.Now+=TimeSpan.FromMinutes(31);
            if(kind==4)r.F.Execute("DELETE FROM PeerReplacementBindingEvidence;");
            if(kind==5)r.F.Execute($"UPDATE PendingCredentialReplacements SET InvalidatedUtc='fixture' WHERE ReplacementId='{request:D}';");
            if(kind==6)r.F.Execute($"UPDATE PeerReplacementBindingEvidence SET LocalFingerprint='{new string('F',64)}';");
            if(kind==7)request=Guid.NewGuid();
            if(kind==8)r.F.Execute("UPDATE PeerReplacementBindingEvidence SET SourceIncarnation=SourceIncarnation+1;");
            var before=r.Snapshot();var refused=false;
            try{r.Repo.ApprovePeerReplacement(actor,revision,request);}
            catch(Exception ex)when(ex is UnauthorizedAccessException or InvalidOperationException or System.Security.Authentication.AuthenticationException or StaleAuthorizationRevisionException){refused=true;}
            Check(refused&&r.Snapshot()==before);
        }
        return Task.CompletedTask;
    }
    public static Task OwnerReplacementRejectsFreshStagedCandidate()
    {
        using var r=new Rig(false);
        r.F.Execute($"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{new string('F',64)}',PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{r.F.Time.Now.AddMinutes(5):O}' WHERE PeerHostId='{r.F.PeerId:D}';");
        var request=ReplacementRequest(r);var before=r.Snapshot();
        Reject<InvalidOperationException>(()=>r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,request));Check(before==r.Snapshot());return Task.CompletedTask;
    }
    public static Task OwnerReplacementRetriesAndSupersedesWithoutRevival()
    {
        using var r=new Rig(false);ReplacementDefaults(r);var request=ReplacementRequest(r);
        var first=r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,request);var ids=r.Repo.Read().HostGrants.Cast<CapabilityGrant>().Concat(r.Repo.Read().ServerGrants).Select(g=>g.GrantId).ToHashSet();
        var snapshot=r.Snapshot();var duplicate=r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,request);
        Check(!duplicate.Changed&&duplicate.Incarnation==first.Incarnation&&duplicate.CreatedDefaultGrants==0&&r.Snapshot()==snapshot);
        r.Revoke();Check(HostDatabase.QueryScalarLong(r.F.Writer,"SELECT COUNT(*) FROM PeerReplacementCompletions WHERE InvalidatedUtc IS NOT NULL;")==1);
        Reject<InvalidOperationException>(()=>r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,request));
        var fresh=ReplacementRequest(r);var second=r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,fresh);
        Check(fresh!=request&&second.Incarnation>first.Incarnation&&second.CreatedDefaultGrants==2&&r.F.Count("PeerReplacementCompletions")==1);
        var grants=r.Repo.Read().HostGrants.Cast<CapabilityGrant>().Concat(r.Repo.Read().ServerGrants).ToArray();
        Check(grants.Where(g=>ids.Contains(g.GrantId)).All(g=>g.InvalidatedUtc is not null)&&grants.Count(g=>!ids.Contains(g.GrantId)&&g.InvalidatedUtc is null)==2);
        return Task.CompletedTask;
    }
    public static Task OwnerReplacementLateFaultsRollback()
    {
        foreach(var fault in new[]{
            "DELETE FROM PendingCredentialReplacements WHERE ReplacementId IN (SELECT ReplacementId FROM PeerReplacementCompletions);",
            "DELETE FROM PeerReplacementCompletions;","UPDATE PeerReplacementCompletions SET CurrentIncarnation=CurrentIncarnation+1;",
            $"UPDATE PeerReplacementCompletions SET ApprovedPeerFingerprint='{new string('F',64)}';",
            "UPDATE PeerReplacementBindingEvidence SET SourceIncarnation=SourceIncarnation+1;","DELETE FROM PeerLocalBindingEvidence;",
            "UPDATE LocalPrincipals SET PublicVerificationKey='wrong' WHERE IsOwner=1;",
            "UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId IN (SELECT PeerHostId FROM PeerReplacementCompletions);",
            $"UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{new string('F',64)}' WHERE PeerHostId IN (SELECT PeerHostId FROM PeerReplacementCompletions);",
            "UPDATE HostDefaultGrants SET CanDelegate=1;","DELETE FROM AuditEvents WHERE EventKind='DefaultGrantApplied';",
            "DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId;","UPDATE AuditEvents SET Summary='wrong' WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE AuditEvents SET IsOfflineRecovery=1 WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE HostCapabilityGrants SET CanDelegate=1;","UPDATE PendingCredentialReplacements SET InvalidatedUtc='wrong' WHERE ApprovedUtc IS NOT NULL;"
        })
        {
            using var r=new Rig(false);ReplacementDefaults(r);var request=ReplacementRequest(r);
            r.F.Execute($"CREATE TRIGGER ReplacementFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerCredentialReplacementApproved' BEGIN {fault} END;");
            var before=r.Snapshot();var refused=false;
            try{r.Repo.ApprovePeerReplacement(r.Owner,r.Revision,request);}
            catch(Exception ex)when(ex is InvalidOperationException or UnauthorizedAccessException or System.Security.Authentication.AuthenticationException or StaleAuthorizationRevisionException or InvalidDataException){refused=true;}
            if(!refused||before!=r.Snapshot())throw new Exception("Owner replacement fault did not roll back: "+fault);
        }
        return Task.CompletedTask;
    }
    public static Task OwnerReplacementConcurrencyAndCancellation()
    {
        using var r=new Rig(false);ReplacementDefaults(r);var request=ReplacementRequest(r);var before=r.Snapshot();
        using(var ct=new CancellationTokenSource())
        {
            var repo=new GrantPolicyRepository(r.F.Database,r.F.HostId,new CancelClock(ct,r.F.Time.Now));
            Reject<OperationCanceledException>(()=>repo.ApprovePeerReplacement(r.Owner,r.Revision,request,ct.Token));Check(before==r.Snapshot());
        }
        var revision=r.Revision;var wins=0;var stale=0;
        Parallel.For(0,8,_=>{try{if(r.Repo.ApprovePeerReplacement(r.Owner,revision,request).Changed)Interlocked.Increment(ref wins);}catch(StaleAuthorizationRevisionException){Interlocked.Increment(ref stale);}});
        Check(wins==1&&stale==7&&r.F.Count("PeerReplacementCompletions")==1&&r.Repo.Read().HostGrants.Count==1&&r.Repo.Read().ServerGrants.Count==1);
        return Task.CompletedTask;
    }
    public static Task ReplacementCompletionSchemaAddsNoAuthority()
    {
        using var f=new PeerTrustTests.Fixture(schemaVersion:14);f.Repository.RecordVerifiedBinding(f.PeerId,new('B',64),new('A',64));
        f.Execute("UPDATE TrustedManagers SET State='Active';");var request=f.Repository.RecordVerifiedBinding(f.PeerId,ReplacementPin,new('A',64));
        var before=f.Repository.Read(f.PeerId);var revision=new GrantPolicyRepository(f.Database,f.HostId,f.Time).Read().Revision;
        var runner=new HostSchemaMigrationRunner(HostSchema.AllMigrations().Where(m=>m.Version<=15));
        Check(runner.Migrate(f.Writer)==1&&runner.Migrate(f.Writer)==0&&f.Count("PeerReplacementCompletions")==0);
        Check(f.Repository.Read(f.PeerId)==before&&new GrantPolicyRepository(f.Database,f.HostId,f.Time).Read().Revision==revision&&f.Count("HostCapabilityGrants")==0);
        Check(HostDatabase.QueryScalarLong(f.Writer,$"SELECT COUNT(*) FROM PendingCredentialReplacements WHERE ReplacementId='{request.ReplacementId:D}' AND InvalidatedUtc IS NULL AND ApprovedUtc IS NULL;")==1);
        return Task.CompletedTask;
    }
}
