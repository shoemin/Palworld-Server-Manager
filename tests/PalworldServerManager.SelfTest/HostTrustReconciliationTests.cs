using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.LocalEnrollmentTests;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static class HostTrustReconciliationTests
{
    private static readonly string A = new('A',64), B = new('B',64), C = new('C',64);
    private static void Reject<T>(Action action) where T:Exception
    { try { action(); throw new Exception("Expected "+typeof(T).Name); } catch(T) { } }
    private static async Task RejectAsync<T>(Func<Task> action) where T:Exception
    { try { await action(); throw new Exception("Expected "+typeof(T).Name); } catch(T) { } }
    private static HostCredentialStateRepository Setup(Fixture f)
    { var r=new HostCredentialStateRepository(f.Database,f.HostId); r.PlanCredential("a"); r.RecordCreated("a",A); r.InstallInitial("a"); return r; }
    private static void Owner(Fixture f)
    {
        var id=Guid.NewGuid(); using var proof=f.Proof(id,true); f.Repository.PrepareOfflineBootstrap(id,"owner",proof,f.Time.Now.AddMinutes(15));
        f.Owner=f.Repository.CompleteBootstrap(id,"owner",proof,Public(f.OwnerKey));
    }
    private static void Plan(HostCredentialStateRepository r,string reference,string fingerprint)
    { r.PlanCredential(reference); r.RecordCreated(reference,fingerprint); }
    private static Guid Rotation(Fixture f,string state,string current="a")
    {
        var id=Guid.NewGuid(); f.Sql($"""
            UPDATE HostIdentity SET CurrentCredentialRef='{current}';
            INSERT INTO HostCredentialRotations (RotationId,OldCredentialRef,NewCredentialRef,State,StartedUtc)
            VALUES ('{id:D}','a','b','{state}','test');
            """); return id;
    }

    public static Task MigrationAndProjection()
    {
        using var f=new Fixture(false);
        var prior=new HostDatabase(new HostDataRoot(Path.Combine(f.Root,"Prior")));
        using(var c=prior.OpenConnection())
        {
            new HostSchemaMigrationRunner(HostSchema.AllMigrations().Take(1)).Migrate(c);
            new HostIdentityRepository(prior).EnsureHostIdentity(c,hostIdFactory:()=>f.HostId.ToString("D"));
            HostDatabase.Execute(c,$"""
                INSERT INTO SecureCredentialReferences (CredentialRef,Purpose,CreatedUtc) VALUES ('prior','HostTlsV1','created-before-upgrade');
                UPDATE HostIdentity SET CurrentCredentialRef='prior';
                """);
            Check(HostSchemaMigrationRunner.Default().Migrate(c)==1,"Upgrade did not apply exactly the additive metadata migration.");
            Check(HostDatabase.QueryScalarText(c,"SELECT ActivatedUtc FROM SecureCredentialReferences;")=="created-before-upgrade" &&
                HostDatabase.QueryScalarText(c,"SELECT CurrentCredentialRef FROM HostIdentity;")=="prior","Upgrade lost current credential/activation history.");
        }
        Reject<InvalidDataException>(()=>HostTrustPlanning.Build(new HostCredentialStateRepository(prior,f.HostId).Read())); // no invented public metadata
        foreach(var state in Enum.GetValues<HostCredentialRotationState>())
        {
            using var sample=new Fixture(false); var r=Setup(sample); Plan(r,"b",B);
            var post=state is HostCredentialRotationState.CutOver or HostCredentialRotationState.Completed;
            var id=Rotation(sample,state.ToString(),post?"b":"a");
            var plan=HostTrustPlanning.Build(r.Read());
            Check(plan.Publication!.CurrentFingerprint==(post?B:A),"Projection used a credential other than authoritative Current.");
            var pending=state is HostCredentialRotationState.Staging or HostCredentialRotationState.ReadyForCutover;
            Check(plan.Publication.PendingFingerprint==(pending?B:null) && plan.Publication.PendingRotationId==(pending?id:null),"Rotation state produced stale/missing Pending trust.");
            if(state==HostCredentialRotationState.Aborted) Check(plan.Retire.SequenceEqual(new[]{"b"}),"Abort retained abandoned New.");
            else if(state==HostCredentialRotationState.Completed) Check(plan.Retire.SequenceEqual(new[]{"a"}),"Completion retained superseded Old.");
            else Check(plan.Retained.Count==2 && plan.Retire.Count==0,"Nonterminal rotation retired required material, including CutOver Old.");
        }
        var repository=Setup(f); Plan(repository,"b",B); Rotation(f,"Staging"); Rotation(f,"Prepared");
        Reject<InvalidDataException>(()=>HostTrustPlanning.Build(repository.Read()));
        f.Sql("DELETE FROM HostCredentialRotations;"); f.Sql("UPDATE HostIdentity SET HostBootstrapState='Initialized';");
        Reject<InvalidDataException>(()=>repository.Read());
        return Task.CompletedTask;
    }

    public static Task RecoveryMetadataAndRollback()
    {
        foreach(var state in new[]{"Prepared","Staging","ReadyForCutover","CutOver"})
        foreach(var reason in Enum.GetValues<MachineCredentialRecoveryReason>())
        {
            using var f=new Fixture(false); var r=Setup(f); Owner(f); Plan(r,"b",B); Plan(r,"c",C);
            Rotation(f,state,state=="CutOver"?"b":"a");
            f.Sql("""
                INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('active','Active','peer-key','test');
                INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('bound','PeerBound','peer-key','test');
                INSERT INTO TrustedManagers (PeerHostId,State,CreatedUtc) VALUES ('revoked','Revoked','test');
                """);
            var oldCurrent=r.Read().CurrentReference; var ownerKey=f.Text("SELECT PublicVerificationKey FROM LocalPrincipals WHERE IsOwner=1;");
            f.Sql("CREATE TRIGGER fail_audit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT,'test failure'); END;");
            Reject<SqliteException>(()=>r.ReplaceOffline("c",reason));
            Check(r.Read().CurrentReference==oldCurrent && f.Text("SELECT State FROM HostCredentialRotations;")==state &&
                f.Count("SELECT SUM(PeerRecoveryRequired) FROM TrustedManagers;")==0 &&
                f.Count("SELECT COUNT(*) FROM SecureCredentialReferences WHERE CredentialRef='c' AND ActivatedUtc IS NOT NULL;")==0,"Recovery audit failure left partial authority/rotation/peer metadata.");
            f.Sql("DROP TRIGGER fail_audit;"); r.ReplaceOffline("c",reason);
            var plan=HostTrustPlanning.Build(r.Read());
            Check(plan.Publication!.CurrentFingerprint==C && plan.Publication.PendingFingerprint is null && plan.Retire.Count==2,"Recovery retained stale current or pending trust.");
            Check(f.Text("SELECT State FROM HostCredentialRotations;")=="Aborted" && f.Count("SELECT SUM(PeerRecoveryRequired) FROM TrustedManagers;")==1 &&
                f.Count("SELECT PeerRecoveryRequired FROM TrustedManagers WHERE PeerHostId='active';")==1,"Recovery did not abort rotation or marked incorrect peers.");
            var kind=reason==MachineCredentialRecoveryReason.CredentialLoss?"HostCredentialRecoveredFromLoss":"HostCredentialRecoveredFromCompromise";
            Check(f.Count($"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='{kind}' AND IsOfflineRecovery=1;")==1,"Recovery reason not distinctly audited.");
            Check(f.Text("SELECT PublicVerificationKey FROM LocalPrincipals WHERE IsOwner=1;")==ownerKey && HostIdentityRepository.CountActiveOwners(f.Writer)==1,"Machine recovery altered Owner credential/authority.");
        }
        using var fresh=new Fixture(false); var repository=Setup(fresh); Plan(repository,"b",B);
        repository.ReplaceOffline("b",MachineCredentialRecoveryReason.CredentialLoss);
        // No retirement yet and no rotation history: an old activated credential must still be rejected.
        Reject<InvalidDataException>(()=>repository.ReplaceOffline("a",MachineCredentialRecoveryReason.SuspectedCompromise));
        Plan(repository,"old-key-new-name",A);
        Reject<InvalidOperationException>(()=>repository.ReplaceOffline("old-key-new-name",MachineCredentialRecoveryReason.CredentialLoss));
        Reject<ArgumentException>(()=>repository.ReplaceOffline("b",(MachineCredentialRecoveryReason)999));
        Reject<InvalidOperationException>(()=>repository.RecordRetired("b"));
        Reject<InvalidOperationException>(()=>repository.InstallInitial("a"));
        Reject<InvalidDataException>(()=>repository.RecordCreated("a",C));
        return Task.CompletedTask;
    }

    public static async Task ReconciliationFailureOrdering()
    {
        using var f=new Fixture(false); var r=Setup(f); Plan(r,"b",B); Rotation(f,"Aborted"); r.PlanCredential("abandoned-before-create");
        var events=new List<string>(); var failPublish=true; var failDelete=true;
        var reconciler=new HostTrustReconciler(r.Read,
            (p,ct)=>{events.Add("publish:"+p.CurrentFingerprint); if(failPublish) throw new IOException("test publication failure"); return Task.CompletedTask;},
            (retained,ct)=>{Check(retained.SequenceEqual(new[]{"a"}),"Native reconciliation retained wrong set.");events.Add("native");return Task.CompletedTask;},
            (reference,ct)=>{events.Add("delete:"+reference); if(failDelete) throw new IOException("test deletion failure"); return Task.CompletedTask;},r.RecordRetired);
        await RejectAsync<IOException>(()=>reconciler.ReconcileAsync());
        Check(events.Count==1 && f.Count("SELECT COUNT(*) FROM SecureCredentialReferences WHERE RetiredUtc IS NOT NULL;")==0,"Publication failure retired credentials prematurely.");
        failPublish=false; events.Clear(); await RejectAsync<IOException>(()=>reconciler.ReconcileAsync());
        Check(events[0].StartsWith("publish:") && events[1]=="native" && f.Count("SELECT COUNT(*) FROM SecureCredentialReferences WHERE RetiredUtc IS NOT NULL;")==0,
            "Failed deletion was recorded as complete or cleanup preceded publication.");
        failDelete=false; events.Clear(); var plan=await reconciler.ReconcileAsync();
        Check(plan.Retire.Count==2 && f.Count("SELECT COUNT(*) FROM SecureCredentialReferences WHERE RetiredUtc IS NOT NULL;")==2,"Restart did not retire abandoned planned material.");
        await reconciler.ReconcileAsync(); // missing material remains a successful idempotent delete
        using var cancel=new CancellationTokenSource(); cancel.Cancel(); events.Clear();
        await RejectAsync<OperationCanceledException>(()=>reconciler.ReconcileAsync(cancel.Token)); Check(events.Count==0,"Canceled reconciliation published or retired material.");
    }

    private sealed class MaterialStore:ISecureCredentialStore
    {
        internal readonly Dictionary<string,byte[]> Values=new(StringComparer.Ordinal); internal readonly List<string> Reads=[];
        internal string? ForbiddenRead; internal byte[]? LastRead;
        public Task<byte[]?> RetrieveAsync(string key,CancellationToken ct=default)
        { ct.ThrowIfCancellationRequested(); Reads.Add(key); if(key==ForbiddenRead) throw new Exception("Read forbidden old private credential.");
            LastRead=Values.TryGetValue(key,out var value)?value.ToArray():null; return Task.FromResult(LastRead); }
        public Task StoreAsync(string key,ReadOnlyMemory<byte> secret,CancellationToken ct=default)
        {ct.ThrowIfCancellationRequested();Values[key]=secret.ToArray();return Task.CompletedTask;}
        public Task DeleteAsync(string key,CancellationToken ct=default)
        {ct.ThrowIfCancellationRequested();if(Values.Remove(key,out var value)) CryptographicOperations.ZeroMemory(value);return Task.CompletedTask;}
    }
    public static async Task MaterialAndNoOldPrivateRead()
    {
        using var f=new Fixture(false); var store=new MaterialStore(); var material=new WindowsHostCredentialMaterial(store); var r=new HostCredentialStateRepository(f.Database,f.HostId);
        try
        {
            r.PlanCredential("old"); var old=await material.CreateAsync(f.HostId,"old"); r.RecordCreated("old",old); r.InstallInitial("old");
            await material.ValidateAsync("old",old); Check(store.LastRead!.All(b=>b==0),"Validation retained private material.");
            await RejectAsync<CryptographicException>(()=>material.ValidateAsync("old",A));
            await RejectAsync<InvalidOperationException>(()=>material.CreateAsync(f.HostId,"old"));
            await material.EnsureEnrollmentKeyAsync(f.HostId,false); var hmacName=LocalEnrollmentVerifier.KeyName(f.HostId); var hmac=store.Values[hmacName].ToArray();
            await material.EnsureEnrollmentKeyAsync(f.HostId,true); Check(hmac.SequenceEqual(store.Values[hmacName]),"Existing HMAC key was regenerated.");
            store.ForbiddenRead="old"; store.Reads.Clear(); r.PlanCredential("new"); var next=await material.CreateAsync(f.HostId,"new"); r.RecordCreated("new",next);
            r.ReplaceOffline("new",MachineCredentialRecoveryReason.SuspectedCompromise);
            var projected=false; var reconciler=new HostTrustReconciler(r.Read,(p,ct)=>{Check(p.CurrentFingerprint==next,"Wrong recovered pin.");projected=true;return Task.CompletedTask;},
                (_,_)=>Task.CompletedTask,store.DeleteAsync,r.RecordRetired);
            await reconciler.ReconcileAsync(); await material.ValidateAsync("new",next);
            Check(projected && !store.Reads.Contains("old") && !store.Values.ContainsKey("old") && hmac.SequenceEqual(store.Values[hmacName]),
                "Recovery read/retained old private material or retired the independent HMAC key.");
            await store.DeleteAsync(hmacName); await RejectAsync<CryptographicException>(()=>material.EnsureEnrollmentKeyAsync(f.HostId,true));
            store.Values[hmacName]=[1]; await RejectAsync<CryptographicException>(()=>material.EnsureEnrollmentKeyAsync(f.HostId,false));
            CryptographicOperations.ZeroMemory(hmac);
        }
        finally { foreach(var bytes in store.Values.Values) CryptographicOperations.ZeroMemory(bytes); }
    }
}
