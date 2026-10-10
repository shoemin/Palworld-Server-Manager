using System.Security.Authentication;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using ActorRef=PalworldServerManager.Core.Authorization.ActorRef;
using HostCapability=PalworldServerManager.Core.Authorization.HostCapability;
using DelegationRights=PalworldServerManager.Core.Authorization.DelegationRights;

namespace PalworldServerManager.SelfTest;

internal static class PeerTrustAdministrationRpcTests
{
    private static void Check(bool condition){if(!condition)throw new Exception("Peer administration assertion failed.");}
    private static async Task Refused(Task call,StatusCode expected)
    {try{await call;}catch(RpcException ex){if(ex.StatusCode!=expected)throw new Exception($"Expected {expected}, received {ex.StatusCode}.",ex);return;}throw new Exception("Peer administration unexpectedly succeeded.");}
    private sealed class Rig:IAsyncDisposable
    {
        internal readonly PeerSecurityRpcTests.Fixture A=new(),B=new();
        internal readonly Guid Target=Guid.NewGuid(),Other=Guid.NewGuid();
        internal GrantPolicyRepository Repo=>new(B.State.Database,B.State.HostId,B.State.Time);
        internal LocalPrincipalMutationActor Owner=>new(B.State.HostId,B.State.OwnerId,"native-owner","fixture-public");
        internal long Revision=>Repo.Read().Revision;
        internal long Count(string sql)=>HostDatabase.QueryScalarLong(B.State.Writer,sql);
        internal async Task Start()
        {
            A.Bind(B);B.Bind(A);
            // Explicit previously verified Active relationships; real TLS is used below.
            A.State.Execute("UPDATE TrustedManagers SET State='Active';");B.State.Execute("UPDATE TrustedManagers SET State='Active';");
            Add(Target);Add(Other);await B.Start();
        }
        internal void Add(Guid peer)=>B.State.Execute($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{peer:D}','Active','{new string('E',64)}','fixture');");
        internal Guid Grant(Guid? grantee=null,HostCapability capability=HostCapability.ManageTrustedManagers,Guid? target=null,DelegationRights? rights=null)
        {var id=Guid.NewGuid();Repo.IssueHost(Owner,Revision,id,ActorRef.RemoteManager(grantee??A.State.HostId),capability,target??B.State.HostId,rights??new(false,false),null);return id;}
        internal async Task<PeerSecurityRpcTests.RawClient> Client(bool feature=true,uint minor=0)
        {
            var c=new PeerSecurityRpcTests.RawClient(A,B);
            try{var hello=PeerSecurityRpcRuntime.Hello(A.State.HostId);hello.Handshake.Protocol.Minor=minor;if(!feature)hello.Handshake.Capabilities.Remove(FeatureCapability.PeerTrustAdministration);await c.Negotiate(hello);return c;}catch{c.Dispose();throw;}
        }
        internal Task<PeerTrustPageReply> Page(PeerSecurityRpcTests.RawClient c,string after="",uint limit=0)
            =>c.Rpc.ReadManagedPeerTrustAsync(new(){AuthoritativeHostId=B.State.HostId.ToString("D"),AfterPeerHostId=after,Limit=limit},deadline:DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
        internal Task<PeerRevokeTrustReply> Revoke(PeerSecurityRpcTests.RawClient c,Guid target,long? revision=null,long? incarnation=null)
            =>c.Rpc.RevokeManagedPeerTrustAsync(new(){AuthoritativeHostId=B.State.HostId.ToString("D"),PeerHostId=target.ToString("D"),ExpectedAuthorizationRevision=revision??Revision,
                ExpectedIncarnation=incarnation??Count($"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{target:D}';")},deadline:DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
        public async ValueTask DisposeAsync(){await B.DisposeAsync();await A.DisposeAsync();}
    }
    public static async Task FeaturesAndHistory()
    {
        await ProtocolTests.SchemaEvolution();Check((int)FeatureCapability.PeerTrustAdministration==21);
        Check(!PeerSecurityRpcRuntime.RecoveryHello(Guid.NewGuid()).Handshake.Capabilities.Contains(FeatureCapability.PeerTrustAdministration));
        await using var r=new Rig();await r.Start();r.Grant();
        using(var raw=new PeerSecurityRpcTests.RawClient(r.A,r.B))await Refused(r.Page(raw),StatusCode.FailedPrecondition);
        using(var old=await r.Client(false)){await Refused(r.Page(old),StatusCode.FailedPrecondition);await Refused(r.Revoke(old,r.Target),StatusCode.FailedPrecondition);}
        using(var recovery=new PeerSecurityRpcTests.RawClient(r.A,r.B))
        {
            var request=PeerSecurityRpcRuntime.RecoveryHello(r.A.State.HostId);request.Handshake.Capabilities.Add(FeatureCapability.PeerTrustAdministration);
            var reply=await recovery.Rpc.NegotiateRecoveryAsync(request).ResponseAsync;
            Check(!reply.Handshake.Capabilities.Contains(FeatureCapability.PeerTrustAdministration));
            await Refused(r.Page(recovery),StatusCode.FailedPrecondition);
        }
        using var c=new PeerSecurityRpcTests.RawClient(r.A,r.B);var hello=PeerSecurityRpcRuntime.Hello(r.A.State.HostId);hello.Handshake.Protocol.Minor=0;
        var offered=await c.Negotiate(hello);Check(offered.Handshake.Protocol.Minor==0&&offered.Handshake.Capabilities.Contains(FeatureCapability.PeerTrustAdministration));
        Check((await r.Page(c)).Peers.Count==3);
        Check(PeerRevokeTrustRequest.Descriptor.Fields.InDeclarationOrder().Select(f=>f.Name).SequenceEqual(new[]{"authoritative_host_id","peer_host_id","expected_authorization_revision","expected_incarnation"}));
    }
    public static async Task IndependentMachineCeiling()
    {
        await using var r=new Rig();await r.Start();using var c=await r.Client();
        await Refused(r.Page(c),StatusCode.PermissionDenied);await Refused(r.Revoke(c,r.Target),StatusCode.PermissionDenied);
        r.Grant(capability:HostCapability.ManagePermissions);r.Grant(target:r.Other);
        await Refused(r.Page(c),StatusCode.PermissionDenied);await Refused(r.Revoke(c,r.Target),StatusCode.PermissionDenied);
        var root=r.Grant();Check((await r.Page(c)).Peers.Count==3);
        r.Repo.InvalidateHostSubtree(r.Owner,r.Revision,root);
        await Refused(r.Page(c),StatusCode.PermissionDenied);Check(r.B.State.Repository.Read(r.Target)!.State=="Active");
        Check(r.Count("SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked';")==0);
    }
    public static async Task ExactForestAndIdempotence()
    {
        await using var r=new Rig();await r.Start();r.Grant();var root=r.Grant(r.Target,HostCapability.CreateServer,rights:new(true,true));
        var proof=new PeerGrantMutationActor(r.B.State.HostId,r.Target,new string('E',64),r.B.Pin,r.Count($"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.Target:D}';"));
        var child=Guid.NewGuid();r.Repo.IssueRemoteHost(proof,r.Revision,child,ActorRef.RemoteManager(r.Other),HostCapability.CreateServer,r.B.State.HostId,new(false,false),root);
        var unrelated=r.Grant(r.Other,HostCapability.CreateServer);using var c=await r.Client();var before=await r.Page(c);
        var result=await r.Revoke(c,r.Target,before.AuthorizationRevision,before.Peers.Single(p=>p.PeerHostId==r.Target.ToString("D")).Incarnation);
        Check(result.Changed&&result.AuthoritativeHostId==r.B.State.HostId.ToString("D")&&result.PeerHostId==r.Target.ToString("D")&&result.AuthorizationRevision>before.AuthorizationRevision);
        var snapshot=r.Repo.Read();Check(snapshot.HostGrants.Single(g=>g.GrantId==root).InvalidatedUtc is not null&&snapshot.HostGrants.Single(g=>g.GrantId==child).InvalidatedUtc is not null&&snapshot.HostGrants.Single(g=>g.GrantId==unrelated).InvalidatedUtc is null);
        Check(r.B.State.Repository.Read(r.Other)!.State=="Active"&&snapshot.Policy.IsOwner(ActorRef.LocalPrincipal(r.B.State.OwnerId)));
        var audits=r.B.State.Count("AuditEvents");Check(!(await r.Revoke(c,r.Target)).Changed&&r.B.State.Count("AuditEvents")==audits);
        Check(r.Count($"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked' AND ActorKind='RemoteManager' AND ActorPeerHostId='{r.A.State.HostId:D}' AND ActorLocalPrincipalId IS NULL;")==1);
    }
    public static async Task SelfRevocationAndOldSession()
    {
        await using var r=new Rig();await r.Start();r.Grant();using var c=await r.Client();
        Check((await r.Revoke(c,r.A.State.HostId)).Changed);await Refused(r.Page(c),StatusCode.Unauthenticated);
        await Refused(r.Revoke(c,r.A.State.HostId),StatusCode.Unauthenticated);
        // Artificial same-key later relationship: the old actual TLS session must stay invalid.
        r.B.State.Execute($"UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{r.A.Pin}' WHERE PeerHostId='{r.A.State.HostId:D}';");r.Grant();
        await Refused(r.Page(c),StatusCode.Unauthenticated);using var fresh=await r.Client();Check((await r.Page(fresh)).Peers.Count==3);
        Check(r.Repo.Read().Policy.IsOwner(ActorRef.LocalPrincipal(r.B.State.OwnerId)));
    }
    public static async Task StaleMalformedAndOwnerRefusals()
    {
        await using var r=new Rig();await r.Start();r.Grant();using var c=await r.Client();var revision=r.Revision;r.Grant(r.Other,HostCapability.CreateServer);
        await Refused(r.Revoke(c,r.Target,revision),StatusCode.Aborted);
        await Refused(r.Revoke(c,r.Target,incarnation:long.MaxValue),StatusCode.FailedPrecondition);
        await Refused(r.Revoke(c,r.B.State.HostId,incarnation:1),StatusCode.InvalidArgument);
        await Refused(r.Revoke(c,r.B.State.OwnerId,incarnation:1),StatusCode.Internal);
        foreach(var req in new[]{new PeerTrustPageRequest(),new(){AuthoritativeHostId=Guid.NewGuid().ToString("D")},new(){AuthoritativeHostId=r.B.State.HostId.ToString("D"),Limit=17},new(){AuthoritativeHostId=r.B.State.HostId.ToString("D"),AfterPeerHostId="bad"}})
            await Refused(c.Rpc.ReadManagedPeerTrustAsync(req).ResponseAsync,StatusCode.InvalidArgument);
        var bad=new PeerRevokeTrustRequest{AuthoritativeHostId=r.B.State.HostId.ToString("D"),PeerHostId=r.Target.ToString("D"),ExpectedAuthorizationRevision=-1,ExpectedIncarnation=1};
        await Refused(c.Rpc.RevokeManagedPeerTrustAsync(bad).ResponseAsync,StatusCode.InvalidArgument);
        Check(r.B.State.Repository.Read(r.Target)!.State=="Active"&&r.Count("SELECT COUNT(*) FROM LocalPrincipals WHERE IsOwner=1 AND RevokedUtc IS NULL;")==1);
    }
    public static async Task BoundedReadAndCorruptProjection()
    {
        await using var r=new Rig();await r.Start();r.Grant();for(var i=0;i<31;i++)r.Add(Guid.NewGuid());using var c=await r.Client();
        var audits=r.B.State.Count("AuditEvents");var all=new HashSet<string>();var after="";
        do{var page=await r.Page(c,after);Check(page.Peers.Count is >0 and <=16&&page.CalculateSize()<=PeerSecurityRpcService.MaximumMessageBytes);foreach(var peer in page.Peers)Check(all.Add(peer.PeerHostId));after=page.NextAfterPeerHostId;}while(after!="");
        Check(all.Count==34&&r.B.State.Count("AuditEvents")==audits);
        var corrupt=(await r.Page(c)).Peers.First(p=>p.PeerHostId!=r.A.State.HostId.ToString("D")).PeerHostId;
        r.B.State.Execute($"UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='invalid' WHERE PeerHostId='{corrupt}';");
        await Refused(r.Page(c),StatusCode.Internal);
    }
    public static async Task OriginalProofAndNonActiveStates()
    {
        foreach(var change in new[]{"PeerRecoveryRequired=1","State='PeerBound'","State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL","CurrentTrustedPublicKeyFingerprint='"+new string('F',64)+"'"})
        {
            await using var r=new Rig();await r.Start();r.Grant();using var c=await r.Client();r.B.State.Execute($"UPDATE TrustedManagers SET {change} WHERE PeerHostId='{r.A.State.HostId:D}';");
            await Refused(r.Page(c),StatusCode.Unauthenticated);await Refused(r.Revoke(c,r.Target),StatusCode.Unauthenticated);
            Check(r.B.State.Repository.Read(r.Target)!.State=="Active");
        }
        await using var changed=new Rig();await changed.Start();changed.Grant();using var client=await changed.Client();changed.B.State.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{new string('F',64)}' WHERE CredentialRef='current';");
        await Refused(changed.Page(client),StatusCode.Unauthenticated);
    }
    public static async Task AuditRollbackAndOwnedDispatch()
    {
        await using var r=new Rig();await r.Start();r.Grant();using var c=await r.Client();
        r.B.State.Execute("CREATE TRIGGER Fault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerTrustRevoked' BEGIN DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId; END;");
        await Refused(r.Revoke(c,r.Target),StatusCode.FailedPrecondition);Check(r.B.State.Repository.Read(r.Target)!.State=="Active");
        var context=new DefaultHttpContext();context.Request.Scheme="https";context.Request.Protocol="HTTP/2";
        await using var connection=new PeerSecurityRpcConnection(r.A.Runtime,r.A.Pin,r.B.Pin);context.Features.Set(connection);
        try{await r.B.Runtime.Permissions.Invoke(context,_=>true,default,FeatureCapability.PeerTrustAdministration);throw new Exception("Foreign runtime accepted.");}catch(AuthenticationException){}
        context.Features.Set<PeerSecurityRpcConnection>(null);using var canceled=new CancellationTokenSource();canceled.Cancel();
        await using var owned=new PeerSecurityRpcConnection(r.B.Runtime,r.B.Pin,r.A.Pin);context.Features.Set(owned);
        try{await r.B.Runtime.Permissions.Invoke(context,_=>true,canceled.Token,FeatureCapability.PeerTrustAdministration);throw new Exception("Canceled dispatch accepted.");}catch(OperationCanceledException){}
    }
    public static async Task FeatureBeforeRotationObservation()
    {
        await using var r=new Rig();await r.Start();r.Grant();var old=new string('F',64);
        // Previously accepted staged peer key. Actual TLS presents its pending key.
        r.B.State.Execute($"UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{old}',PendingTrustedPublicKeyFingerprint='{r.A.Pin}',PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{r.B.State.Time.Now.AddMinutes(10):O}' WHERE PeerHostId='{r.A.State.HostId:D}';");
        using(var missing=await r.Client(false))
        {
            // Normal native negotiation independently observes a valid pending key.
            Check(r.B.State.Repository.Read(r.A.State.HostId)!.CurrentFingerprint==r.A.Pin);
            var audits=r.B.State.Count("AuditEvents");
            await Refused(r.Page(missing),StatusCode.FailedPrecondition);
            Check(r.B.State.Count("AuditEvents")==audits);
        }
        // Controlled original-session fixture isolates dispatch ordering from negotiation.
        r.B.State.Execute($"UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{old}',PendingTrustedPublicKeyFingerprint='{r.A.Pin}',PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{r.B.State.Time.Now.AddMinutes(10):O}' WHERE PeerHostId='{r.A.State.HostId:D}';");
        var context=new DefaultHttpContext();context.Request.Scheme="https";context.Request.Protocol="HTTP/2";
        var hello=PeerSecurityRpcRuntime.Hello(r.A.State.HostId);hello.Handshake.Capabilities.Remove(FeatureCapability.PeerTrustAdministration);
        await using var session=new PeerSecurityRpcConnection(r.B.Runtime,r.B.Pin,r.A.Pin){PeerId=r.A.State.HostId,
            PeerIncarnation=r.Count($"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.A.State.HostId:D}';"),
            Protocol=NegotiatedProtocol.Negotiate(PeerSecurityRpcRuntime.Hello(r.B.State.HostId).Handshake,hello.Handshake)};
        context.Features.Set(session);
        try{await r.B.Runtime.Permissions.Invoke(context,call=>call.ReadPeerTrust(null,16),default,FeatureCapability.PeerTrustAdministration);throw new Exception("Missing feature accepted.");}catch(InvalidOperationException){}
        Check(r.B.State.Repository.Read(r.A.State.HostId)!.CurrentFingerprint==old);
        session.Protocol=NegotiatedProtocol.Negotiate(PeerSecurityRpcRuntime.Hello(r.B.State.HostId).Handshake,PeerSecurityRpcRuntime.Hello(r.A.State.HostId).Handshake);
        var before=r.Revision;var incarnation=r.Count($"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.Target:D}';");
        try{await r.B.Runtime.Permissions.Invoke(context,call=>call.RevokePeer(before,r.Target,incarnation),default,FeatureCapability.PeerTrustAdministration);throw new Exception("Stale revision accepted.");}catch(StaleAuthorizationRevisionException){}
        Check(r.B.State.Repository.Read(r.A.State.HostId)!.CurrentFingerprint==r.A.Pin&&r.B.State.Repository.Read(r.Target)!.State=="Active");
        var page=await r.B.Runtime.Permissions.Invoke(context,call=>call.ReadPeerTrust(null,16),default,FeatureCapability.PeerTrustAdministration);
        Check(page.Revision>before);
    }
}
