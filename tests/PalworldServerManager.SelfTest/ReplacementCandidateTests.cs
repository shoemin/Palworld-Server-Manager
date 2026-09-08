using System.Text.Json;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.SelfTest;

internal static class ReplacementCandidateTests
{
    private static readonly string Local=new('A',64),Peer=new('B',64),Candidate=new('C',64),Other=new('D',64);
    private static void Check(bool value){if(!value)throw new Exception("Replacement provenance assertion failed.");}
    private static PeerTrustTests.Fixture Active(int? version=null)
    {
        var f=new PeerTrustTests.Fixture(schemaVersion:version);f.Repository.RecordVerifiedBinding(f.PeerId,Peer,Local);
        // Explicit Active fixture; full Owner-approved repair is not implemented by this test.
        f.Execute($"UPDATE TrustedManagers SET State='Active' WHERE PeerHostId='{f.PeerId:D}';");return f;
    }
    private static PeerBindingResult Request(PeerTrustTests.Fixture f,string? key=null)
        =>f.Repository.RecordOwnerVerifiedBinding(new(f.HostId,f.OwnerId,"native-owner","fixture-public"),f.PeerId,key??Candidate,Local);
    private static long Count(PeerTrustTests.Fixture f,string sql)=>HostDatabase.QueryScalarLong(f.Writer,sql);
    private static bool Invalid(PeerTrustTests.Fixture f,Guid? id)
        =>Count(f,$"SELECT COUNT(*) FROM PendingCredentialReplacements WHERE ReplacementId='{id:D}' AND InvalidatedUtc IS NOT NULL;")==1;
    private static string Snapshot(PeerTrustTests.Fixture f)
    {
        var all=new List<object>();
        foreach(var table in new[]{"HostIdentity","LocalPrincipals","TrustedManagers","TrustedManagerPairings","PeerRelationshipIncarnations",
            "SecureCredentialReferences","PendingCredentialReplacements","PeerReplacementBindingEvidence","HostCapabilityGrants","ServerCapabilityGrants","AuditEvents","AuthorizationRevision","sqlite_sequence"})
        {
            using var command=f.Writer.CreateCommand();command.CommandText="SELECT * FROM "+table+" ORDER BY rowid;";
            using var reader=command.ExecuteReader();var rows=new List<object[]>();
            while(reader.Read()){var values=new object[reader.FieldCount];reader.GetValues(values);rows.Add(values);}
            all.Add(new{table,rows});
        }
        return JsonSerializer.Serialize(all);
    }
    public static Task FreshEvidenceIsDurableAndIdempotent()
    {
        using var f=Active();var result=Request(f);var id=result.ReplacementId;
        Check(result.Disposition==PeerBindingDisposition.ReplacementRequired&&id is not null);
        Check(Count(f,$"""
            SELECT COUNT(*) FROM PeerReplacementBindingEvidence e JOIN PendingCredentialReplacements r ON r.ReplacementId=e.ReplacementId
            JOIN PeerRelationshipIncarnations i ON i.PeerHostId=r.PeerHostId
            WHERE r.ReplacementId='{id:D}' AND e.SourceIncarnation=i.Incarnation AND e.LocalFingerprint='{Local}'
                AND e.VerifiedUtc=r.VerifiedUtc AND r.ApprovedUtc IS NULL AND r.InvalidatedUtc IS NULL;
            """)==1);
        var before=Snapshot(f);f.Time.Now+=TimeSpan.FromMinutes(1);
        var results=new PeerBindingResult[8];Parallel.For(0,8,i=>results[i]=Request(f));
        Check(results.All(v=>v==result)&&Snapshot(f)==before&&f.Count("PeerReplacementBindingEvidence")==1);
        Check(f.Count("HostCapabilityGrants")==0&&f.Count("ServerCapabilityGrants")==0&&f.Repository.Read(f.PeerId)!.CurrentFingerprint==Peer);
        return Task.CompletedTask;
    }
    public static Task TrustAndStagedRotationAbaPermanentlyInvalidate()
    {
        foreach(var kind in Enumerable.Range(0,6))
        {
            using var f=Active();var old=Request(f);var peer=$"WHERE PeerHostId='{f.PeerId:D}'";
            var change=kind switch
            {
                0=>$"UPDATE TrustedManagers SET State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL,RevokedUtc='fixture' {peer}; UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{Peer}',RevokedUtc=NULL {peer};",
                1=>$"UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{Other}' {peer}; UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{Peer}' {peer};",
                2=>$"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{Other}',PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{f.Time.Now.AddMinutes(5):O}' {peer}; UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint=NULL,PendingRotationId=NULL,PendingRotationExpiresUtc=NULL {peer};",
                3=>$"UPDATE TrustedManagers SET PeerRecoveryRequired=1 {peer}; UPDATE TrustedManagers SET PeerRecoveryRequired=0 {peer};",
                4=>$"UPDATE TrustedManagers SET PairedUtc='fixture' {peer}; UPDATE TrustedManagers SET PairedUtc=NULL {peer};",
                _=>$"UPDATE TrustedManagerPairings SET BoundUtc=BoundUtc {peer};"
            };
            f.Execute(change);Check(Invalid(f,old.ReplacementId));var invalid=Snapshot(f);
            var fresh=Request(f);Check(fresh.ReplacementId!=old.ReplacementId&&Invalid(f,old.ReplacementId));
            Check(f.Count("HostCapabilityGrants")==0&&f.Count("PeerReplacementBindingEvidence")==2&&Snapshot(f)!=invalid);
        }
        return Task.CompletedTask;
    }
    public static Task LocalCredentialAbaPermanentlyInvalidates()
    {
        foreach(var kind in Enumerable.Range(0,4))
        {
            using var f=Active();var old=Request(f);
            var change=kind switch
            {
                0=>$"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{Other}' WHERE CredentialRef='current'; UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{Local}' WHERE CredentialRef='current';",
                1=>"UPDATE HostIdentity SET CurrentCredentialRef='other' WHERE Id=1; UPDATE HostIdentity SET CurrentCredentialRef='current' WHERE Id=1;",
                2=>"UPDATE SecureCredentialReferences SET RetiredUtc='fixture' WHERE CredentialRef='current'; UPDATE SecureCredentialReferences SET RetiredUtc=NULL WHERE CredentialRef='current';",
                _=>$"DELETE FROM SecureCredentialReferences WHERE CredentialRef='current'; INSERT INTO SecureCredentialReferences (CredentialRef,Purpose,CreatedUtc,PublicKeyFingerprint,ActivatedUtc) VALUES ('current','HostTlsV1','{f.Time.Now:O}','{Local}','{f.Time.Now:O}');"
            };
            f.Execute(change);Check(Invalid(f,old.ReplacementId)&&Request(f).ReplacementId!=old.ReplacementId);
        }
        return Task.CompletedTask;
    }
    public static Task MissingOrMismatchedProofCannotBeReused()
    {
        foreach(var mutation in new[]{"DELETE FROM PeerReplacementBindingEvidence;","UPDATE PeerReplacementBindingEvidence SET SourceIncarnation=SourceIncarnation+1;",
            $"UPDATE PeerReplacementBindingEvidence SET LocalFingerprint='{Other}';","UPDATE PeerReplacementBindingEvidence SET VerifiedUtc='different';"})
        {
            using var f=Active();var old=Request(f);f.Execute(mutation);
            var fresh=Request(f);Check(fresh.ReplacementId!=old.ReplacementId&&fresh.Disposition==PeerBindingDisposition.ReplacementRequired);
            Check(f.Count("HostCapabilityGrants")==0&&f.Repository.Read(f.PeerId)!.CurrentFingerprint==Peer);
        }
        return Task.CompletedTask;
    }
    public static Task CanonicalRevocationPreservesItsAtomicTimestamp()
    {
        using var f=Active();var request=Request(f);var grants=new GrantPolicyRepository(f.Database,f.HostId,f.Time);
        var inc=Count(f,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{f.PeerId:D}';");
        var result=grants.RevokeLocalPeerTrust(new(f.HostId,f.OwnerId,"native-owner","fixture-public"),grants.Read().Revision,f.PeerId,inc);
        Check(result.Changed&&result.InvalidatedReplacements==1&&result.Incarnation>inc&&Invalid(f,request.ReplacementId));
        Check(HostDatabase.QueryScalarText(f.Writer,$"SELECT InvalidatedUtc FROM PendingCredentialReplacements WHERE ReplacementId='{request.ReplacementId:D}';")==f.Time.Now.ToString("O"));
        Check(f.Repository.Read(f.PeerId)!.State=="Revoked"&&f.Count("PeerReplacementBindingEvidence")==1);
        return Task.CompletedTask;
    }
    public static Task LegacyRequestsNeverGainFreshEvidence()
    {
        using var f=Active(13);var old=Guid.NewGuid();var approved=Guid.NewGuid();
        foreach(var id in new[]{old,approved})f.Execute($"""
            INSERT INTO PendingCredentialReplacements (ReplacementId,PeerHostId,ProposedKeyFingerprint,VerifiedUtc,ExpiresUtc,ExpectedTrustState,ExpectedCurrentTrustedPublicKeyFingerprint,CreatedUtc)
            VALUES ('{id:D}','{f.PeerId:D}','{Candidate}','{f.Time.Now:O}','{f.Time.Now.AddMinutes(30):O}','Active','{Peer}','{f.Time.Now:O}');
            """);
        f.Execute($"UPDATE PendingCredentialReplacements SET ApprovedUtc='{f.Time.Now:O}',ApprovedByOwnerLocalPrincipalId='{f.OwnerId:D}' WHERE ReplacementId='{approved:D}';");
        HostSchemaMigrationRunner.Default().Migrate(f.Writer);
        Check(HostSchemaMigrationRunner.ReadSchemaVersion(f.Writer)==14&&Invalid(f,old)&&!Invalid(f,approved)&&f.Count("PeerReplacementBindingEvidence")==0);
        var snapshot=Snapshot(f);HostSchemaMigrationRunner.Default().Migrate(f.Writer);Check(Snapshot(f)==snapshot);
        Check(Request(f).ReplacementId!=old&&f.Count("PeerReplacementBindingEvidence")==1&&f.Count("HostCapabilityGrants")==0);
        return Task.CompletedTask;
    }
    public static Task UnrelatedAndApprovedHistoryRemainIntact()
    {
        using var f=Active();var approved=Request(f);var pending=Request(f,Other);
        f.Execute($"UPDATE PendingCredentialReplacements SET ApprovedUtc='{f.Time.Now:O}',ApprovedByOwnerLocalPrincipalId='{f.OwnerId:D}' WHERE ReplacementId='{approved.ReplacementId:D}';");
        var otherPeer=Guid.NewGuid();f.Repository.RecordVerifiedBinding(otherPeer,Peer,Local);
        var unrelated=f.Repository.RecordVerifiedBinding(otherPeer,Candidate,Local);
        f.Execute($"UPDATE TrustedManagers SET PendingRotationId='{Guid.NewGuid():D}' WHERE PeerHostId='{f.PeerId:D}';");
        Check(!Invalid(f,approved.ReplacementId)&&Invalid(f,pending.ReplacementId)&&!Invalid(f,unrelated.ReplacementId));
        Check(Count(f,"SELECT COUNT(*) FROM LocalPrincipals WHERE State='Active' AND IsOwner=1;")==1&&f.Count("HostCapabilityGrants")==0);
        return Task.CompletedTask;
    }
    public static Task LateCandidateAuditAndAuthorityFaultsRollback()
    {
        var faults=new[]{
            "DELETE FROM PendingCredentialReplacements;","DELETE FROM PeerReplacementBindingEvidence;",
            $"UPDATE PendingCredentialReplacements SET ProposedKeyFingerprint='{Other}';",
            "UPDATE PendingCredentialReplacements SET ExpiresUtc='changed';","UPDATE PendingCredentialReplacements SET InvalidatedUtc='changed';",
            "UPDATE PeerReplacementBindingEvidence SET SourceIncarnation=SourceIncarnation+1;",
            $"UPDATE PeerReplacementBindingEvidence SET LocalFingerprint='{Other}';","UPDATE PeerReplacementBindingEvidence SET VerifiedUtc='changed';",
            "DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId;","UPDATE AuditEvents SET Summary='changed' WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE AuditEvents SET ActorKind=NULL WHERE AuditEventId=NEW.AuditEventId;","UPDATE AuditEvents SET IsOfflineRecovery=1 WHERE AuditEventId=NEW.AuditEventId;",
            $"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{Other}';",
            $"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{Other}' WHERE CredentialRef='current';",
            "UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;",
            "INSERT INTO HostCapabilityGrants (GrantId,TargetHostId,Capability,GranteeActorKind,GranteePeerHostId,GrantedByActorKind,GrantedByLocalPrincipalId,CanDelegate,CanDelegateOnwardDelegation,CreatedUtc) SELECT 'injected',h.HostId,'ViewHost','RemoteManager',t.PeerHostId,'LocalPrincipal',p.LocalPrincipalId,0,0,'fixture' FROM HostIdentity h,TrustedManagers t,LocalPrincipals p WHERE p.IsOwner=1;"
        };
        foreach(var fault in faults)
        {
            using var f=Active();f.Execute($"CREATE TRIGGER CandidateAuditFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerCredentialReplacementPending' BEGIN {fault} END;");
            var before=Snapshot(f);var rejected=false;
            try{Request(f);}catch(InvalidOperationException){rejected=true;}catch(System.Security.Authentication.AuthenticationException){rejected=true;}
            if(!rejected||before!=Snapshot(f))throw new Exception("Candidate fault did not roll back: "+fault);
        }
        return Task.CompletedTask;
    }
}
