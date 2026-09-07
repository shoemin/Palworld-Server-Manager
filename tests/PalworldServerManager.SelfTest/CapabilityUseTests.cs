using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class CapabilityUseTests
{
    private static readonly DelegationRights Use=new(false,false),Onward=new(true,true);
    private static void Check(bool value){if(!value)throw new Exception("Capability use assertion failed.");}
    private static void Reject<T>(Action action)where T:Exception
    {try{action();}catch(T){return;}throw new Exception("Expected capability use refusal: "+typeof(T).Name);}
    private static bool Allowed(Func<long> action){try{action();return true;}catch(UnauthorizedAccessException){return false;}}
    private sealed class Rig:IDisposable
    {
        internal readonly PeerTrustTests.Fixture F=new();
        internal readonly Guid User=Guid.NewGuid();
        internal readonly ServerRef Target;
        internal GrantPolicyRepository Repo=>new(F.Database,F.HostId,F.Time);
        internal LocalPrincipalMutationActor Owner=>new(F.HostId,F.OwnerId,"native-owner","fixture-public");
        internal LocalPrincipalMutationActor Local=>new(F.HostId,User,"private-native","private-key");
        internal ActorRef LocalActor=>ActorRef.LocalPrincipal(User);
        internal ActorRef PeerActor=>ActorRef.RemoteManager(F.PeerId);
        internal long Revision=>Repo.Read().Revision;
        internal long Denials=>HostDatabase.QueryScalarLong(F.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PermissionPolicyDenied';");
        internal PeerGrantMutationActor Proof(Guid peer)=>new(F.HostId,peer,new('B',64),new('A',64),
            HostDatabase.QueryScalarLong(F.Writer,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{peer:D}';"));
        internal PeerGrantMutationActor Peer=>Proof(F.PeerId);
        internal Rig()
        {
            Target=new(F.HostId,Guid.NewGuid());
            F.Execute($"INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc) VALUES ('{User:D}','private-native','private-key',0,'Active','{F.Time.Now:O}');");
            Active(F.PeerId);
        }
        internal void Active(Guid peer)=>F.Execute($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{peer:D}','Active','{new string('B',64)}','{F.Time.Now:O}');");
        internal Guid Host(ActorRef actor,HostCapability cap,Guid? target=null,DelegationRights? rights=null)
        {var id=Guid.NewGuid();Repo.IssueHost(Owner,Revision,id,actor,cap,target??F.HostId,rights??Use,null);return id;}
        internal Guid Server(ActorRef actor,ServerCapability cap,ServerRef? target=null,DelegationRights? rights=null)
        {var id=Guid.NewGuid();Repo.IssueServer(Owner,Revision,id,actor,cap,target??Target,rights??Use,null);return id;}
        internal long LocalHost()=>Repo.RequireLocalHostCapability(Local,HostCapability.CreateServer,F.HostId);
        internal long LocalServer()=>Repo.RequireLocalServerCapability(Local,ServerCapability.ViewServer,Target);
        internal long RemoteHost()=>Repo.RequireRemoteHostCapability(Peer,HostCapability.CreateServer,F.HostId);
        internal long RemoteServer()=>Repo.RequireRemoteServerCapability(Peer,ServerCapability.ViewServer,Target);
        public void Dispose()=>F.Dispose();
    }
    public static Task EveryTypedCapabilityUsesOnlyItsExactGrant()
    {
        using var r=new Rig();long expectedDenials=0;
        foreach(var remote in new[]{false,true})
        {
            var actor=remote?r.PeerActor:r.LocalActor;
            foreach(var granted in Enum.GetValues<HostCapability>())
            {
                var root=r.Host(actor,granted);var before=r.Revision;
                foreach(var requested in Enum.GetValues<HostCapability>())
                {
                    var allowed=Allowed(()=>remote?r.Repo.RequireRemoteHostCapability(r.Peer,requested,r.F.HostId):r.Repo.RequireLocalHostCapability(r.Local,requested,r.F.HostId));
                    Check(allowed==(granted==requested));if(!allowed)expectedDenials++;
                }
                Check(r.Revision==before);r.Repo.InvalidateHostSubtree(r.Owner,before,root);
            }
            foreach(var granted in Enum.GetValues<ServerCapability>())
            {
                var root=r.Server(actor,granted);var before=r.Revision;
                foreach(var requested in Enum.GetValues<ServerCapability>())
                {
                    var allowed=Allowed(()=>remote?r.Repo.RequireRemoteServerCapability(r.Peer,requested,r.Target):r.Repo.RequireLocalServerCapability(r.Local,requested,r.Target));
                    Check(allowed==(granted==requested));if(!allowed)expectedDenials++;
                }
                Check(r.Revision==before);r.Repo.InvalidateServerSubtree(r.Owner,before,root);
            }
        }
        Check(r.Denials==expectedDenials&&expectedDenials==124&&r.F.Count("ServerInventory")==0);
        var revision=r.Revision;var audits=r.F.Count("AuditEvents");
        foreach(var cap in Enum.GetValues<HostCapability>())Check(r.Repo.RequireLocalHostCapability(r.Owner,cap,r.F.HostId)==revision);
        foreach(var cap in Enum.GetValues<ServerCapability>())Check(r.Repo.RequireLocalServerCapability(r.Owner,cap,r.Target)==revision);
        Check(r.Revision==revision&&r.F.Count("AuditEvents")==audits);return Task.CompletedTask;
    }
    public static Task QualifiedTargetsAndRevocationInvalidateEarlierObservations()
    {
        using var r=new Rig();var hostRoot=r.Host(r.LocalActor,HostCapability.CreateServer,rights:Onward);
        var child=Guid.NewGuid();r.Repo.IssueHost(r.Local,r.Revision,child,r.PeerActor,HostCapability.CreateServer,r.F.HostId,Use,hostRoot);
        var serverRoot=r.Server(r.PeerActor,ServerCapability.ViewServer);var before=r.Revision;
        Check(r.LocalHost()==before&&r.RemoteHost()==before&&r.RemoteServer()==before);
        // Same profile UUID on another Host and a different profile on this Host both refuse.
        Reject<UnauthorizedAccessException>(()=>r.Repo.RequireRemoteServerCapability(r.Peer,ServerCapability.ViewServer,new(r.F.PeerId,r.Target.ServerProfileId)));
        Reject<UnauthorizedAccessException>(()=>r.Repo.RequireRemoteServerCapability(r.Peer,ServerCapability.ViewServer,new(r.F.HostId,Guid.NewGuid())));
        Reject<UnauthorizedAccessException>(()=>r.Repo.RequireRemoteHostCapability(r.Peer,HostCapability.CreateServer,r.F.PeerId));
        // Even a matching stored grant cannot turn incoming use into third-Host forwarding.
        r.Host(r.PeerActor,HostCapability.CreateServer,r.F.PeerId);
        r.Server(r.PeerActor,ServerCapability.ViewServer,new(r.F.PeerId,r.Target.ServerProfileId));
        Reject<UnauthorizedAccessException>(()=>r.Repo.RequireRemoteHostCapability(r.Peer,HostCapability.CreateServer,r.F.PeerId));
        Reject<UnauthorizedAccessException>(()=>r.Repo.RequireRemoteServerCapability(r.Peer,ServerCapability.ViewServer,new(r.F.PeerId,r.Target.ServerProfileId)));
        r.Repo.InvalidateHostSubtree(r.Owner,r.Revision,hostRoot);
        Reject<UnauthorizedAccessException>(()=>r.RemoteHost());Reject<UnauthorizedAccessException>(()=>r.LocalHost());
        Check(r.RemoteServer()==r.Revision&&r.Revision>before);
        r.Repo.InvalidateServerSubtree(r.Owner,r.Revision,serverRoot);Reject<UnauthorizedAccessException>(()=>r.RemoteServer());
        Check(r.Repo.Read().HostGrants.Single(g=>g.GrantId==child).InvalidatedUtc is not null);return Task.CompletedTask;
    }
    public static Task TwoHostCeilingsAreIndependentForHostAndServer()
    {
        foreach(var localGrant in new[]{false,true})foreach(var remoteGrant in new[]{false,true})
        {
            using var local=new Rig();using var remote=new Rig();local.Active(remote.F.HostId);remote.Active(local.F.HostId);
            var target=new ServerRef(remote.F.HostId,local.Target.ServerProfileId);
            if(localGrant){local.Host(local.LocalActor,HostCapability.CreateServer,remote.F.HostId);local.Server(local.LocalActor,ServerCapability.ViewServer,target);}
            if(remoteGrant){remote.Host(ActorRef.RemoteManager(local.F.HostId),HostCapability.CreateServer);remote.Server(ActorRef.RemoteManager(local.F.HostId),ServerCapability.ViewServer,target);}
            var proof=remote.Proof(local.F.HostId);var a=local.Revision;var b=remote.Revision;
            var lh=Allowed(()=>local.Repo.RequireLocalHostCapability(local.Local,HostCapability.CreateServer,remote.F.HostId));
            var rh=Allowed(()=>remote.Repo.RequireRemoteHostCapability(proof,HostCapability.CreateServer,remote.F.HostId));
            var ls=Allowed(()=>local.Repo.RequireLocalServerCapability(local.Local,ServerCapability.ViewServer,target));
            var rs=Allowed(()=>remote.Repo.RequireRemoteServerCapability(proof,ServerCapability.ViewServer,target));
            Check(lh==localGrant&&ls==localGrant&&rh==remoteGrant&&rs==remoteGrant&&(lh&&rh)==(localGrant&&remoteGrant));
            Check(local.Revision==a&&remote.Revision==b);
            Check(local.Denials==(localGrant?0:2)&&remote.Denials==(remoteGrant?0:2));
            if(!localGrant)Check(HostDatabase.QueryScalarLong(local.F.Writer,$"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PermissionPolicyDenied' AND AffectedHostId='{remote.F.HostId:D}';")==2);
            Reject<UnauthorizedAccessException>(()=>local.Repo.RequireLocalServerCapability(local.Local,ServerCapability.ViewServer,local.Target));
            Check(local.Repo.RequireLocalHostCapability(local.Owner,HostCapability.CreateServer,remote.F.HostId)==a);
            Check(Allowed(()=>remote.Repo.RequireRemoteHostCapability(proof,HostCapability.CreateServer,remote.F.HostId))==remoteGrant);
            // The local Owner also cannot route through inactive/recovery-required trust.
            local.F.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{remote.F.HostId:D}';");
            Reject<UnauthorizedAccessException>(()=>local.Repo.RequireLocalHostCapability(local.Owner,HostCapability.CreateServer,remote.F.HostId));
            Reject<UnauthorizedAccessException>(()=>local.Repo.RequireLocalServerCapability(local.Owner,ServerCapability.ViewServer,target));
            Check(local.Repo.RequireLocalServerCapability(local.Owner,ServerCapability.ViewServer,local.Target)==local.Revision);
        }
        return Task.CompletedTask;
    }
    public static Task EveryEntryRequiresCurrentProofAndClosedInputs()
    {
        using var r=new Rig();var before=r.Revision;
        Reject<ArgumentException>(()=>r.Repo.RequireLocalHostCapability(r.Local,(HostCapability)999,r.F.HostId));
        Reject<ArgumentException>(()=>r.Repo.RequireRemoteHostCapability(r.Peer,HostCapability.CreateServer,Guid.Empty));
        Reject<ArgumentException>(()=>r.Repo.RequireLocalServerCapability(r.Local,(ServerCapability)999,r.Target));
        Reject<ArgumentNullException>(()=>r.Repo.RequireRemoteServerCapability(r.Peer,ServerCapability.ViewServer,null!));
        var bad=r.Local with{PublicVerificationKey="forged"};
        Reject<AuthenticationException>(()=>r.Repo.RequireLocalHostCapability(bad,HostCapability.CreateServer,r.F.HostId));
        Reject<AuthenticationException>(()=>r.Repo.RequireLocalServerCapability(bad,ServerCapability.ViewServer,r.Target));
        foreach(var badPeer in new[]{r.Peer with{PeerFingerprint=new('C',64)},r.Peer with{LocalFingerprint=new('C',64)},r.Peer with{Incarnation=r.Peer.Incarnation+1}})
        {
            Reject<AuthenticationException>(()=>r.Repo.RequireRemoteHostCapability(badPeer,HostCapability.CreateServer,r.F.HostId));
            Reject<AuthenticationException>(()=>r.Repo.RequireRemoteServerCapability(badPeer,ServerCapability.ViewServer,r.Target));
        }
        using var ct=new CancellationTokenSource();ct.Cancel();
        Reject<OperationCanceledException>(()=>r.Repo.RequireLocalHostCapability(r.Local,HostCapability.CreateServer,r.F.HostId,ct.Token));
        Check(r.Denials==0&&r.Revision==before);
        foreach(var change in new[]{"State='PeerBound'","State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL","PeerRecoveryRequired=1"})
        {
            using var inactive=new Rig();var proof=inactive.Peer;
            inactive.F.Execute($"UPDATE TrustedManagers SET {change} WHERE PeerHostId='{inactive.F.PeerId:D}';");
            Reject<AuthenticationException>(()=>inactive.Repo.RequireRemoteHostCapability(proof,HostCapability.CreateServer,inactive.F.HostId));
            Reject<AuthenticationException>(()=>inactive.Repo.RequireRemoteServerCapability(proof,ServerCapability.ViewServer,inactive.Target));
            Check(inactive.Denials==0);
        }
        var oldLocal=r.Local;var oldPeer=r.Peer;
        r.F.Execute($"UPDATE LocalPrincipals SET State='Revoked',PublicVerificationKey=NULL WHERE LocalPrincipalId='{r.User:D}';");
        r.F.Execute($"UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{new string('C',64)}' WHERE PeerHostId='{r.F.PeerId:D}';");
        Reject<AuthenticationException>(()=>r.Repo.RequireLocalHostCapability(oldLocal,HostCapability.CreateServer,r.F.HostId));
        Reject<AuthenticationException>(()=>r.Repo.RequireLocalServerCapability(oldLocal,ServerCapability.ViewServer,r.Target));
        Reject<AuthenticationException>(()=>r.Repo.RequireRemoteHostCapability(oldPeer,HostCapability.CreateServer,r.F.HostId));
        Reject<AuthenticationException>(()=>r.Repo.RequireRemoteServerCapability(oldPeer,ServerCapability.ViewServer,r.Target));
        Check(r.Denials==0);
        return Task.CompletedTask;
    }
    public static Task UseDenialsRecordRealActorTargetAndSurviveContention()
    {
        using var r=new Rig();var before=r.Revision;var local=r.Local;var peer=r.Peer;
        Parallel.For(0,8,i=>Reject<UnauthorizedAccessException>(()=>
        {switch(i%4){case 0:r.Repo.RequireLocalHostCapability(local,HostCapability.CreateServer,r.F.HostId);break;case 1:r.Repo.RequireLocalServerCapability(local,ServerCapability.ViewServer,r.Target);break;case 2:r.Repo.RequireRemoteHostCapability(peer,HostCapability.CreateServer,r.F.HostId);break;default:r.Repo.RequireRemoteServerCapability(peer,ServerCapability.ViewServer,r.Target);break;}}));
        Check(r.Denials==8&&r.Revision==before&&r.F.Count("HostCapabilityGrants")==0&&r.F.Count("ServerCapabilityGrants")==0);
        using var cmd=r.F.Writer.CreateCommand();cmd.CommandText="SELECT ActorKind,ActorLocalPrincipalId,ActorPeerHostId,AffectedHostId,AffectedServerProfileId,IsOfflineRecovery,Summary FROM AuditEvents WHERE EventKind='PermissionPolicyDenied';";
        using var reader=cmd.ExecuteReader();int locals=0,peers=0,hosts=0,servers=0;
        while(reader.Read())
        {
            if(reader.GetString(0)=="LocalPrincipal"){Check(reader.GetString(1)==r.User.ToString("D")&&reader.IsDBNull(2));locals++;}
            else{Check(reader.GetString(0)=="RemoteManager"&&reader.IsDBNull(1)&&reader.GetString(2)==r.F.PeerId.ToString("D"));peers++;}
            Check(reader.GetString(3)==r.F.HostId.ToString("D")&&reader.GetInt32(5)==0);
            if(reader.IsDBNull(4)){Check(reader.GetString(6)=="Action=UseHostCapability; Capability=CreateServer; Outcome=PolicyDenied.");hosts++;}
            else{Check(reader.GetString(4)==r.Target.ServerProfileId.ToString("D")&&reader.GetString(6)=="Action=UseServerCapability; Capability=ViewServer; Outcome=PolicyDenied.");servers++;}
        }
        Check(locals==4&&peers==4&&hosts==4&&servers==4);return Task.CompletedTask;
    }
    private sealed class CancelOnAudit(TimeProvider actual,CancellationTokenSource cancellation):TimeProvider
    {public override DateTimeOffset GetUtcNow(){cancellation.Cancel();return actual.GetUtcNow();}}
    public static Task UseAuditFaultsAndLateProofChangesRollBack()
    {
        foreach(var remote in new[]{false,true})foreach(var mode in Enumerable.Range(0,6))
        {
            using var r=new Rig();var before=r.Revision;var local=r.Local;var peer=r.Peer;
            var sql=mode switch
            {
                0=>"SELECT RAISE(ABORT,'use audit unavailable');",
                1=>"DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId;",
                2=>"UPDATE AuditEvents SET AffectedHostId='changed' WHERE AuditEventId=NEW.AuditEventId;",
                3=>remote?$"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.F.PeerId:D}';":$"UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE LocalPrincipalId='{r.User:D}';",
                4=>$"UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE LocalPrincipalId='{r.F.OwnerId:D}';",
                _=>remote?$"DELETE FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.F.PeerId:D}'; INSERT INTO PeerRelationshipIncarnations (PeerHostId) VALUES ('{r.F.PeerId:D}');":$"UPDATE LocalPrincipals SET OsPrincipalRef='changed' WHERE LocalPrincipalId='{r.User:D}';"
            };
            r.F.Execute($"CREATE TRIGGER UseFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PermissionPolicyDenied' BEGIN {sql} END;");
            Action apply=()=>{if(remote)r.Repo.RequireRemoteServerCapability(peer,ServerCapability.ViewServer,r.Target);else r.Repo.RequireLocalHostCapability(local,HostCapability.CreateServer,r.F.HostId);};
            if(mode==0)Reject<SqliteException>(apply);else if(mode is 1 or 2)Reject<InvalidOperationException>(apply);else if(mode==4)Reject<StaleAuthorizationRevisionException>(apply);else Reject<AuthenticationException>(apply);
            Check(r.Denials==0&&r.Revision==before);r.F.Execute("DROP TRIGGER UseFault;");Reject<UnauthorizedAccessException>(apply);Check(r.Denials==1);
        }
        using var cancel=new Rig();using var ct=new CancellationTokenSource();var revision=cancel.Revision;
        var repo=new GrantPolicyRepository(cancel.F.Database,cancel.F.HostId,new CancelOnAudit(cancel.F.Time,ct));
        Reject<OperationCanceledException>(()=>repo.RequireLocalServerCapability(cancel.Local,ServerCapability.ViewServer,cancel.Target,ct.Token));
        Check(cancel.Denials==0&&cancel.Revision==revision);return Task.CompletedTask;
    }
}
