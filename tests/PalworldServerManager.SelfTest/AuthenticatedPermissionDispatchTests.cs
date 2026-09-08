using System.Security.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using ActorRef = PalworldServerManager.Core.Authorization.ActorRef;
using HostCapability = PalworldServerManager.Core.Authorization.HostCapability;
using ServerCapability = PalworldServerManager.Core.Authorization.ServerCapability;
using ServerRef = PalworldServerManager.Core.Authorization.ServerRef;

namespace PalworldServerManager.SelfTest;

internal static class AuthenticatedPermissionDispatchTests
{
    private static readonly DelegationRights Use=new(false,false),Onward=new(true,true);
    private static readonly string LocalPin=new('A',64),PeerPin=new('B',64),NewPin=new('C',64);
    private static void Check(bool value){if(!value)throw new Exception("Authenticated permission dispatch assertion failed.");}
    private static void Reject<T>(Action action)where T:Exception
    {try{action();}catch(T){return;}throw new Exception("Expected permission dispatch refusal: "+typeof(T).Name);}
    private static async Task RejectAsync<T>(Func<Task> action)where T:Exception
    {try{await action();}catch(T){return;}throw new Exception("Expected permission dispatch refusal: "+typeof(T).Name);}
    private static NegotiatedProtocol Protocol(FeatureCapability feature,bool offered=true)
    {
        var hello=new Handshake{Protocol=new(){Major=1,Minor=10}};
        if(offered)hello.Capabilities.Add(feature);return NegotiatedProtocol.Negotiate(hello,hello);
    }
    // Real client P256 signatures/production verifier, but explicit simulated HTTP/TLS/native
    // connection facts. This is adapter evidence, not a new permission RPC transport claim.
    private sealed class Rig:IAsyncDisposable
    {
        internal readonly LocalEnrollmentTests.Fixture F;
        internal readonly Guid User,PeerId=Guid.NewGuid();
        internal readonly ServerRef Target;
        internal readonly LocalSecurityRpcRuntime Local;
        internal readonly PeerSecurityRpcRuntime Peer;
        private readonly List<IAsyncDisposable> connections=[];
        internal GrantPolicyRepository Repo=>new(F.Database,F.HostId,F.Time);
        internal long Revision=>Repo.Read().Revision;
        internal long Count(string table)=>F.Count("SELECT COUNT(*) FROM "+table+";");
        internal long Denials=>F.Count("SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PermissionPolicyDenied';");
        internal Rig(bool initialized=true)
        {
            F=new(initialized);User=initialized?F.Enroll():Guid.Empty;Target=new(F.HostId,Guid.NewGuid());
            F.Sql($"INSERT INTO SecureCredentialReferences (CredentialRef,Purpose,CreatedUtc,PublicKeyFingerprint,ActivatedUtc) VALUES ('current','HostTlsV1','{F.Time.Now:O}','{LocalPin}','{F.Time.Now:O}'); UPDATE HostIdentity SET CurrentCredentialRef='current' WHERE Id=1;");
            F.Sql($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{PeerId:D}','Active','{PeerPin}','{F.Time.Now:O}');");
            Local=NewLocal();Peer=NewPeer();
        }
        internal LocalSecurityRpcRuntime NewLocal()=>new(F.Database,F.HostId,F.Secrets,c=>(string)c.Items["fixture-native"]!,_=>{},F.Time);
        internal PeerSecurityRpcRuntime NewPeer()=>new(F.Database,F.HostId,Repo.CreateDefaultActivationHook(),F.Time);
        private static DefaultHttpContext Context()
        {var c=new DefaultHttpContext();c.Request.Scheme="https";c.Request.Protocol="HTTP/2";return c;}
        internal async Task<DefaultHttpContext> LocalContext(bool owner=false,bool negotiate=true,bool authenticate=true,bool feature=true)
        {
            var c=Context();var native=owner?"owner":"user";c.Items["fixture-native"]=native;
            var connection=new LocalSecurityRpcConnection(Local);connections.Add(connection);c.Features.Set(connection);
            await connection.Invoke(native,session=>
            {
                if(negotiate)session.Protocol=Protocol(FeatureCapability.LocalPrincipalSecurity,feature);
                if(authenticate)
                {
                    var id=owner?F.Owner:User;var key=owner?F.OwnerKey:F.UserKey;
                    var payload=session.Authentication.IssueChallenge(id);
                    session.Authentication.Authenticate(new WindowsLocalPrincipalCryptography().Sign(new(id,key),F.HostId,payload));
                }
                return Task.FromResult(true);
            },CancellationToken.None);return c;
        }
        internal DefaultHttpContext PeerContext(bool negotiate=true,bool feature=true,string? remote=null,string? local=null,long? incarnation=null)
        {
            var c=Context();var connection=new PeerSecurityRpcConnection(Peer,local??LocalPin,remote??PeerPin)
            {
                PeerId=PeerId,PeerIncarnation=incarnation??F.Count($"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{PeerId:D}';"),
                Protocol=negotiate?Protocol(FeatureCapability.PeerTrustActivation,feature):null
            };
            connections.Add(connection);c.Features.Set(connection);return c;
        }
        internal Guid HostRoot(ActorRef grantee,DelegationRights? rights=null)
        {var id=Guid.NewGuid();Repo.IssueHost(F.Actor,Revision,id,grantee,HostCapability.CreateServer,F.HostId,rights??Use,null);return id;}
        internal Guid ServerRoot(ActorRef grantee,DelegationRights? rights=null)
        {var id=Guid.NewGuid();Repo.IssueServer(F.Actor,Revision,id,grantee,ServerCapability.ViewServer,Target,rights??Use,null);return id;}
        internal void Register(SqliteConnection c,SqliteTransaction tx,ServerRef target)
        {using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=$"INSERT INTO ServerInventory (ServerProfileId,AuthoritativeHostId,DisplayName,InstallPath,CreatedUtc) VALUES ('{target.ServerProfileId:D}','{F.HostId:D}','fixture','fixture','{F.Time.Now:O}');";cmd.ExecuteNonQuery();}
        public async ValueTask DisposeAsync(){foreach(var connection in connections)await connection.DisposeAsync();F.Dispose();}
    }
    public static async Task LocalSignaturesFeedCanonicalOwnerAndDelegationActions()
    {
        await using var r=new Rig();var owner=await r.LocalContext(true);var user=await r.LocalContext();var me=ActorRef.LocalPrincipal(r.User);var peer=ActorRef.RemoteManager(r.PeerId);
        Check(await r.Local.Permissions.Invoke(owner,c=>c.RequireHost(HostCapability.CreateServer,r.F.HostId),default)==r.Revision);
        Check(await r.Local.Permissions.Invoke(owner,c=>c.RequireServer(ServerCapability.ViewServer,r.Target),default)==r.Revision);
        var host=Guid.NewGuid();var server=Guid.NewGuid();
        await r.Local.Permissions.Invoke(owner,c=>c.IssueHost(r.Revision,new(host,me,HostCapability.CreateServer,r.F.HostId,Onward)),default);
        await r.Local.Permissions.Invoke(owner,c=>c.IssueServer(r.Revision,new(server,me,ServerCapability.ViewServer,r.Target,Onward)),default);
        Check(await r.Local.Permissions.Invoke(user,c=>c.RequireHost(HostCapability.CreateServer,r.F.HostId),default)==r.Revision);
        var h=await r.Local.Permissions.Invoke(user,c=>c.IssueHost(r.Revision,new(Guid.NewGuid(),peer,HostCapability.CreateServer,r.F.HostId,Use,host)),default);
        var s=await r.Local.Permissions.Invoke(user,c=>c.IssueServer(r.Revision,new(Guid.NewGuid(),peer,ServerCapability.ViewServer,r.Target,Use,server)),default);
        Check(r.Repo.Read().HostGrants.Single(g=>g.GrantId==h.GrantId).GrantedByActor==me&&r.Repo.Read().ServerGrants.Single(g=>g.GrantId==s.GrantId).GrantedByActor==me);
        var preset=new RolePreset("private-recipe",[new(Guid.NewGuid(),peer,HostCapability.CreateServer,r.F.HostId,Use,host)],[new(Guid.NewGuid(),peer,ServerCapability.ViewServer,r.Target,Use,server)]);
        var batch=await r.Local.Permissions.Invoke(user,c=>c.ApplyPreset(r.Revision,preset),default);Check(batch.HostGrantIds.Count==1&&batch.ServerGrantIds.Count==1);
        await RejectAsync<UnauthorizedAccessException>(()=>r.Local.Permissions.Invoke(user,c=>c.ConfigureDefaults(r.Revision,DefaultGrantTemplate.Factory),default));
        await r.Local.Permissions.Invoke(owner,c=>c.ConfigureDefaults(r.Revision,DefaultGrantTemplate.Factory),default);
        await r.Local.Permissions.Invoke(owner,c=>c.InvalidateHost(r.Revision,host),default);
        await r.Local.Permissions.Invoke(owner,c=>c.InvalidateServer(r.Revision,server),default);
        await RejectAsync<UnauthorizedAccessException>(()=>r.Local.Permissions.Invoke(user,c=>c.RequireServer(ServerCapability.ViewServer,r.Target),default));
        var historical=r.HostRoot(peer);await r.Local.Permissions.Invoke(owner,c=>c.InvalidateHost(r.Revision,historical),default);
        var reissued=await r.Local.Permissions.Invoke(owner,c=>c.ReissueHistorical(r.Revision,r.PeerId,[historical],[]),default);
        Check(reissued.Hosts.Single().HistoricalGrantId==historical&&r.Repo.Read().HostGrants.Single(g=>g.GrantId==reissued.Hosts[0].NewGrantId).GrantedByActor==ActorRef.LocalPrincipal(r.F.Owner));
        Check(r.F.Count($"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PermissionPolicyDenied' AND ActorLocalPrincipalId='{r.User:D}' AND ActorPeerHostId IS NULL;")==2);
    }
    public static async Task PeerActionsUseRealPeerAndCreatorKeepsIndependentUserCeiling()
    {
        await using var r=new Rig();var peer=ActorRef.RemoteManager(r.PeerId);var me=ActorRef.LocalPrincipal(r.User);
        var host=r.HostRoot(peer,Onward);var server=r.ServerRoot(peer,Onward);var context=r.PeerContext();
        Check(await r.Peer.Permissions.Invoke(context,c=>c.RequireHost(HostCapability.CreateServer,r.F.HostId),default)==r.Revision);
        Check(await r.Peer.Permissions.Invoke(context,c=>c.RequireServer(ServerCapability.ViewServer,r.Target),default)==r.Revision);
        var h=await r.Peer.Permissions.Invoke(context,c=>c.IssueHost(r.Revision,new(Guid.NewGuid(),me,HostCapability.CreateServer,r.F.HostId,Use,host)),default);
        var s=await r.Peer.Permissions.Invoke(context,c=>c.IssueServer(r.Revision,new(Guid.NewGuid(),me,ServerCapability.ViewServer,r.Target,Use,server)),default);
        Check(r.Repo.Read().HostGrants.Single(g=>g.GrantId==h.GrantId).GrantedByActor==peer&&r.Repo.Read().ServerGrants.Single(g=>g.GrantId==s.GrantId).GrantedByActor==peer);
        var preset=new RolePreset("private-recipe",[new(Guid.NewGuid(),me,HostCapability.CreateServer,r.F.HostId,Use,host)],[new(Guid.NewGuid(),me,ServerCapability.ViewServer,r.Target,Use,server)]);
        Check((await r.Peer.Permissions.Invoke(context,c=>c.ApplyPreset(r.Revision,preset),default)).ServerGrantIds.Count==1);
        await RejectAsync<UnauthorizedAccessException>(()=>r.Peer.Permissions.Invoke(context,c=>c.IssueHost(r.Revision,new(Guid.NewGuid(),me,HostCapability.CreateServer,r.F.HostId,Use)),default));
        var created=new ServerRef(r.F.HostId,Guid.NewGuid());var result=await r.Peer.Permissions.Invoke(context,c=>c.CommitConfirmedCreation(r.Revision,created,(db,tx)=>r.Register(db,tx,created)),default);
        var grants=r.Repo.Read().ServerGrants.Where(g=>result.GrantIds.Contains(g.GrantId)).ToArray();
        Check(grants.Length==6&&grants.All(g=>g.GranteeActor==peer&&g.GrantedByActor==ActorRef.LocalPrincipal(r.F.Owner)&&g.Target==created&&g.DerivedFromGrantId is null&&!g.Rights.CanDelegate&&!g.Rights.CanDelegateOnwardDelegation)&&grants.All(g=>g.Capability!=ServerCapability.ManageServerSharing));
        var user=await r.LocalContext();await RejectAsync<UnauthorizedAccessException>(()=>r.Local.Permissions.Invoke(user,c=>c.RequireServer(ServerCapability.ViewServer,created),default));
        Check(await r.Peer.Permissions.Invoke(context,c=>c.RequireServer(ServerCapability.ViewServer,created),default)==r.Revision);
        Check(r.F.Count($"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PermissionPolicyDenied' AND ActorPeerHostId='{r.PeerId:D}' AND ActorLocalPrincipalId IS NULL;")==1);
    }
    public static async Task LocalChannelNegotiationAndCurrentIdentityGateCallbacks()
    {
        await using var r=new Rig();bool called=false;long Run(LocalPermissionCall c){called=true;return c.RequireHost(HostCapability.CreateServer,r.F.HostId);}
        var missing=new DefaultHttpContext();missing.Request.Scheme="https";missing.Request.Protocol="HTTP/2";
        await RejectAsync<AuthenticationException>(()=>r.Local.Permissions.Invoke(missing,Run,default));
        var absent=await r.LocalContext(negotiate:false);await RejectAsync<InvalidOperationException>(()=>r.Local.Permissions.Invoke(absent,Run,default));
        var featureless=await r.LocalContext(feature:false);await RejectAsync<InvalidOperationException>(()=>r.Local.Permissions.Invoke(featureless,Run,default));
        var unauthenticated=await r.LocalContext(authenticate:false);await RejectAsync<AuthenticationException>(()=>r.Local.Permissions.Invoke(unauthenticated,Run,default));
        var valid=await r.LocalContext();valid.Request.Scheme="http";await RejectAsync<AuthenticationException>(()=>r.Local.Permissions.Invoke(valid,Run,default));
        valid.Request.Scheme="https";valid.Request.Protocol="HTTP/1.1";await RejectAsync<AuthenticationException>(()=>r.Local.Permissions.Invoke(valid,Run,default));valid.Request.Protocol="HTTP/2";
        valid.Items["fixture-native"]="other";await RejectAsync<AuthenticationException>(()=>r.Local.Permissions.Invoke(valid,Run,default));valid.Items["fixture-native"]="user";
        r.F.Sql($"UPDATE LocalPrincipals SET PublicVerificationKey='{LocalEnrollmentTests.Public(r.F.OwnerKey)}' WHERE LocalPrincipalId='{r.User:D}';");
        await RejectAsync<AuthenticationException>(()=>r.Local.Permissions.Invoke(valid,Run,default));
        r.F.Sql($"UPDATE LocalPrincipals SET State='Revoked',PublicVerificationKey=NULL WHERE LocalPrincipalId='{r.User:D}';");
        await RejectAsync<AuthenticationException>(()=>r.Local.Permissions.Invoke(valid,Run,default));Check(!called&&r.Denials==0);
        await using var empty=new Rig(false);var uninitialized=await empty.LocalContext(authenticate:false);
        await RejectAsync<AuthenticationException>(()=>empty.Local.Permissions.Invoke(uninitialized,c=>{called=true;return 1;},default));Check(!called);
    }
    public static async Task PeerChannelNegotiationAndOriginalProofGateCallbacks()
    {
        await using var r=new Rig();bool called=false;long Run(PeerPermissionCall c){called=true;return c.RequireHost(HostCapability.CreateServer,r.F.HostId);}
        var absent=r.PeerContext(negotiate:false);await RejectAsync<InvalidOperationException>(()=>r.Peer.Permissions.Invoke(absent,Run,default));
        var featureless=r.PeerContext(feature:false);await RejectAsync<InvalidOperationException>(()=>r.Peer.Permissions.Invoke(featureless,Run,default));
        foreach(var bad in new[]{r.PeerContext(remote:NewPin),r.PeerContext(local:NewPin),r.PeerContext(incarnation:1_000_000)})
            await RejectAsync<AuthenticationException>(()=>r.Peer.Permissions.Invoke(bad,Run,default));
        var original=r.PeerContext();original.Request.Scheme="http";await RejectAsync<AuthenticationException>(()=>r.Peer.Permissions.Invoke(original,Run,default));original.Request.Scheme="https";
        r.F.Sql($"DELETE FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.PeerId:D}'; INSERT INTO PeerRelationshipIncarnations (PeerHostId) VALUES ('{r.PeerId:D}');");
        await RejectAsync<AuthenticationException>(()=>r.Peer.Permissions.Invoke(original,Run,default));
        r.F.Sql($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.PeerId:D}';");
        await RejectAsync<AuthenticationException>(()=>r.Peer.Permissions.Invoke(r.PeerContext(),Run,default));
        Check(!called&&r.Denials==0&&r.Count("TrustedManagerCredentialHistory")==0);
        foreach(var change in new[]{"State='PeerBound'","State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL"})
        {
            await using var inactive=new Rig();inactive.F.Sql($"UPDATE TrustedManagers SET {change} WHERE PeerHostId='{inactive.PeerId:D}';");
            await RejectAsync<AuthenticationException>(()=>inactive.Peer.Permissions.Invoke(inactive.PeerContext(),c=>{called=true;return 1;},default));Check(!called);
        }
    }
    public static async Task CallLifetimeCancellationAndRuntimeAssociationAreBound()
    {
        await using var r=new Rig();var local=await r.LocalContext(true);var peer=r.PeerContext();var revision=r.Revision;bool called=false;
        await RejectAsync<AuthenticationException>(()=>r.NewLocal().Permissions.Invoke(local,c=>{called=true;return 1;},default));
        await RejectAsync<AuthenticationException>(()=>r.NewPeer().Permissions.Invoke(peer,c=>{called=true;return 1;},default));Check(!called);
        var escaped=await r.Local.Permissions.Invoke(local,c=>c,default);Reject<ObjectDisposedException>(()=>escaped.RequireHost(HostCapability.CreateServer,r.F.HostId));
        var remote=await r.Peer.Permissions.Invoke(peer,c=>c,default);Reject<ObjectDisposedException>(()=>remote.RequireHost(HostCapability.CreateServer,r.F.HostId));
        await r.Local.Permissions.Invoke(local,c=>
        {
            Reject<InvalidOperationException>(()=>Task.Factory.StartNew(()=>c.RequireHost(HostCapability.CreateServer,r.F.HostId),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default).GetAwaiter().GetResult());
            return c.RequireHost(HostCapability.CreateServer,r.F.HostId);
        },default);
        using(var ct=new CancellationTokenSource())
        {
            await RejectAsync<OperationCanceledException>(()=>r.Local.Permissions.Invoke(local,c=>{ct.Cancel();return c.RequireHost(HostCapability.CreateServer,r.F.HostId);},ct.Token));
            await RejectAsync<OperationCanceledException>(()=>r.Peer.Permissions.Invoke(peer,c=>{called=true;return 1;},ct.Token));Check(!called);
        }
        using(var abort=new CancellationTokenSource())
        {
            abort.Cancel();local.RequestAborted=abort.Token;peer.RequestAborted=abort.Token;
            await RejectAsync<OperationCanceledException>(()=>r.Local.Permissions.Invoke(local,c=>{called=true;return 1;},default));
            await RejectAsync<OperationCanceledException>(()=>r.Peer.Permissions.Invoke(peer,c=>{called=true;return 1;},default));Check(!called);
            local.RequestAborted=default;peer.RequestAborted=default;
        }
        Check(await r.Local.Permissions.Invoke(local,c=>c.RequireHost(HostCapability.CreateServer,r.F.HostId),default)==revision);
        await local.Features.Get<LocalSecurityRpcConnection>()!.DisposeAsync();await peer.Features.Get<PeerSecurityRpcConnection>()!.DisposeAsync();
        await RejectAsync<ObjectDisposedException>(()=>r.Local.Permissions.Invoke(local,c=>{called=true;return 1;},default));
        await RejectAsync<ObjectDisposedException>(()=>r.Peer.Permissions.Invoke(peer,c=>{called=true;return 1;},default));Check(!called&&r.Revision==revision&&r.Denials==0);
    }
    public static async Task LocalRevocationDispatchBindsActorAndLifetime()
    {
        await using var r=new Rig();var owner=await r.LocalContext(true);var user=await r.LocalContext();
        var incarnation=r.F.Count($"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.PeerId:D}';");
        await RejectAsync<UnauthorizedAccessException>(()=>r.Local.Permissions.Invoke(user,c=>c.RevokePeer(r.Revision,r.PeerId,incarnation),default));
        var revision=r.Revision;var escaped=await r.Local.Permissions.Invoke(owner,c=>c,default);
        Reject<ObjectDisposedException>(()=>escaped.RevokePeer(revision,r.PeerId,incarnation));
        await r.Local.Permissions.Invoke(owner,c=>
        {
            Reject<InvalidOperationException>(()=>Task.Factory.StartNew(()=>c.RevokePeer(revision,r.PeerId,incarnation),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default).GetAwaiter().GetResult());
            return true;
        },default);
        using(var cancellation=new CancellationTokenSource())
            await RejectAsync<OperationCanceledException>(()=>r.Local.Permissions.Invoke(owner,c=>{cancellation.Cancel();return c.RevokePeer(revision,r.PeerId,incarnation);},cancellation.Token));
        Check(r.Revision==revision&&r.F.Text($"SELECT State FROM TrustedManagers WHERE PeerHostId='{r.PeerId:D}';")=="Active");
        var result=await r.Local.Permissions.Invoke(owner,c=>c.RevokePeer(revision,r.PeerId,incarnation),default);
        Check(result.Changed&&r.F.Count($"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked' AND ActorLocalPrincipalId='{r.F.Owner:D}' AND ActorPeerHostId IS NULL;")==1);
    }
    public static async Task RemoteRevocationDispatchBindsOriginalProof()
    {
        await using var r=new Rig();var peer=ActorRef.RemoteManager(r.PeerId);
        r.Repo.IssueHost(r.F.Actor,r.Revision,Guid.NewGuid(),peer,HostCapability.ManageTrustedManagers,r.F.HostId,Use,null);
        var stale=r.PeerContext();var revision=r.Revision;
        r.F.Sql($"DELETE FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.PeerId:D}'; INSERT INTO PeerRelationshipIncarnations (PeerHostId) VALUES ('{r.PeerId:D}');");
        var incarnation=r.F.Count($"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.PeerId:D}';");
        await RejectAsync<AuthenticationException>(()=>r.Peer.Permissions.Invoke(stale,c=>c.RevokePeer(revision,r.PeerId,incarnation),default));
        var context=r.PeerContext();var escaped=await r.Peer.Permissions.Invoke(context,c=>c,default);
        Reject<ObjectDisposedException>(()=>escaped.RevokePeer(revision,r.PeerId,incarnation));
        using(var cancellation=new CancellationTokenSource())
            await RejectAsync<OperationCanceledException>(()=>r.Peer.Permissions.Invoke(context,c=>{cancellation.Cancel();return c.RevokePeer(revision,r.PeerId,incarnation);},cancellation.Token));
        Check(r.Revision==revision&&r.Count("AuditEvents")>0);
        var result=await r.Peer.Permissions.Invoke(context,c=>c.RevokePeer(revision,r.PeerId,incarnation),default);
        Check(result.Changed&&result.Incarnation>incarnation&&r.F.Count($"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked' AND ActorPeerHostId='{r.PeerId:D}' AND ActorLocalPrincipalId IS NULL;")==1);
        await RejectAsync<AuthenticationException>(()=>r.Peer.Permissions.Invoke(context,c=>c.RevokePeer(r.Revision,r.PeerId,result.Incarnation),default));
    }
    public static async Task RevocationAfterStagedPromotionNeedsFreshRevision()
    {
        await using var r=new Rig();
        r.Repo.IssueHost(r.F.Actor,r.Revision,Guid.NewGuid(),ActorRef.RemoteManager(r.PeerId),HostCapability.ManageTrustedManagers,r.F.HostId,Use,null);
        r.F.Sql($"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{NewPin}',PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{r.F.Time.Now.AddMinutes(-1):O}' WHERE PeerHostId='{r.PeerId:D}';");
        var context=r.PeerContext(remote:NewPin);var revision=r.Revision;
        var incarnation=r.F.Count($"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.PeerId:D}';");
        await RejectAsync<StaleAuthorizationRevisionException>(()=>r.Peer.Permissions.Invoke(context,c=>c.RevokePeer(revision,r.PeerId,incarnation),default));
        Check(r.Revision==revision+1&&r.Count("TrustedManagerCredentialHistory")==1&&r.F.Text($"SELECT State FROM TrustedManagers WHERE PeerHostId='{r.PeerId:D}';")=="Active");
        Check(r.F.Count("SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked';")==0);
        var result=await r.Peer.Permissions.Invoke(context,c=>c.RevokePeer(r.Revision,r.PeerId,incarnation),default);
        Check(result.Changed&&result.InvalidatedGrants==1&&r.F.Text($"SELECT State FROM TrustedManagers WHERE PeerHostId='{r.PeerId:D}';")=="Revoked");
    }
    public static async Task GuardedPromotionDoesNotBecomePermissionOrLocalUserAuthority()
    {
        await using var r=new Rig();var root=r.HostRoot(ActorRef.RemoteManager(r.PeerId));var rotation=Guid.NewGuid();
        r.F.Sql($"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{NewPin}',PendingRotationId='{rotation:D}',PendingRotationExpiresUtc='{r.F.Time.Now.AddMinutes(-1):O}' WHERE PeerHostId='{r.PeerId:D}';");
        var peer=r.PeerContext(remote:NewPin);var revision=r.Revision;
        var request=new HostGrantRequest(Guid.NewGuid(),ActorRef.LocalPrincipal(r.User),HostCapability.CreateServer,r.F.HostId,Use,root);
        await RejectAsync<StaleAuthorizationRevisionException>(()=>r.Peer.Permissions.Invoke(peer,c=>c.IssueHost(revision,request),default));
        Check(r.Revision==revision+1&&r.Count("HostCapabilityGrants")==1&&r.Count("TrustedManagerCredentialHistory")==1&&r.Denials==0);
        Check(r.F.Text($"SELECT CurrentTrustedPublicKeyFingerprint FROM TrustedManagers WHERE PeerHostId='{r.PeerId:D}';")==NewPin);
        Check(await r.Peer.Permissions.Invoke(peer,c=>c.RequireHost(HostCapability.CreateServer,r.F.HostId),default)==r.Revision);
        await RejectAsync<UnauthorizedAccessException>(()=>r.Peer.Permissions.Invoke(peer,c=>c.IssueHost(r.Revision,request),default));
        var user=await r.LocalContext();await RejectAsync<UnauthorizedAccessException>(()=>r.Local.Permissions.Invoke(user,c=>c.RequireHost(HostCapability.CreateServer,r.F.HostId),default));
        Check(r.Denials==2&&r.Count("HostCapabilityGrants")==1&&r.Count("TrustedManagerCredentialHistory")==1&&r.Revision==revision+1);
    }
}
