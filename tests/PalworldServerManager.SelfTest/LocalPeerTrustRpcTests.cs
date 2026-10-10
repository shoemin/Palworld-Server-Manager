using Google.Protobuf;
using Grpc.Core;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using ActorRef=PalworldServerManager.Core.Authorization.ActorRef;
using HostCapability=PalworldServerManager.Core.Authorization.HostCapability;
using ServerCapability=PalworldServerManager.Core.Authorization.ServerCapability;
using DelegationRights=PalworldServerManager.Core.Authorization.DelegationRights;
using ServerRef=PalworldServerManager.Core.Authorization.ServerRef;
using LocalPeerTrustItem=PalworldServerManager.Contracts.Wire.LocalPeerTrustItem;

namespace PalworldServerManager.SelfTest;

internal static partial class LocalPeerTrustRpcTests
{
    private static void Check(bool value){if(!value)throw new Exception("Local peer trust RPC assertion failed.");}
    private static async Task Refused(Task call,StatusCode code)
    {try{await call;}catch(RpcException ex){Check(ex.StatusCode==code);return;}throw new Exception("Trust RPC unexpectedly succeeded.");}
    private static Task<LocalPeerTrustPageReply> Page(LocalSecurityRpcTests.Client c,string after="",uint limit=0)
        =>c.Call<LocalPeerTrustPageRequest,LocalPeerTrustPageReply>("ReadPeerTrust",new(){AfterPeerHostId=after,Limit=limit});
    private static Task<LocalRevokePeerReply> Revoke(LocalSecurityRpcTests.Client c,LocalPeerTrustPageReply page,LocalPeerTrustItem peer)
        =>c.Call<LocalRevokePeerRequest,LocalRevokePeerReply>("RevokePeerTrust",new(){PeerHostId=peer.PeerHostId,ExpectedAuthorizationRevision=page.AuthorizationRevision,ExpectedIncarnation=peer.Incarnation});
    private static Task<LocalHandshakeReply> Hello(LocalSecurityRpcTests.Client c,bool feature=true,uint minor=0)
    {
        var h=new Handshake{Protocol=new(){Major=1,Minor=minor},ProductVersion="not-authority"};h.Capabilities.Add(FeatureCapability.LocalPrincipalSecurity);
        if(feature)h.Capabilities.Add(FeatureCapability.LocalPeerTrustManagement);
        return c.Call<Handshake,LocalHandshakeReply>("Negotiate",h);
    }
    private sealed class Rig(bool user=false):IAsyncDisposable
    {
        internal readonly LocalSecurityRpcTests.Fixture F=new(true);
        internal Guid Principal,Peer=Guid.NewGuid(),Other=Guid.NewGuid();
        internal GrantPolicyRepository Repo=>new(F.State.Database,F.State.HostId,F.State.Time);
        internal LocalPrincipalMutationActor Owner=>F.State.Actor with{OsPrincipalRef=user?"owner":F.Native};
        internal long Revision=>Repo.Read().Revision;
        internal async Task Start()
        {
            Principal=user?F.State.Enroll(F.Native):F.State.Owner;
            if(!user)F.State.Sql($"UPDATE LocalPrincipals SET OsPrincipalRef='{F.Native}' WHERE IsOwner=1;");
            var pin=(await F.Trust().ReadAsync()).CurrentFingerprint;
            F.State.Sql($"INSERT INTO SecureCredentialReferences (CredentialRef,Purpose,CreatedUtc,PublicKeyFingerprint,ActivatedUtc) VALUES ('current','HostTlsV1','{F.State.Time.Now:O}','{pin}','{F.State.Time.Now:O}'); UPDATE HostIdentity SET CurrentCredentialRef='current' WHERE Id=1;");
            Add(Peer);Add(Other);await F.Start();
        }
        internal void Add(Guid id)=>F.State.Sql($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{id:D}','Active','{new string('B',64)}','{F.State.Time.Now:O}');");
        internal async Task<LocalSecurityRpcTests.Client> Connect(bool feature=true)
        {
            var c=F.Connect();try{await Hello(c,feature);await c.Authenticate(F.State.HostId,Principal,user?F.State.UserKey:F.State.OwnerKey);return c;}
            catch{c.Dispose();throw;}
        }
        internal Guid Grant(Guid principal,HostCapability capability=HostCapability.ManageTrustedManagers)
        {var id=Guid.NewGuid();Repo.IssueHost(Owner,Revision,id,ActorRef.LocalPrincipal(principal),capability,F.State.HostId,new(false,false),null);return id;}
        internal long Count(string sql)=>F.State.Count(sql);
        internal long Audits=>Count("SELECT COUNT(*) FROM AuditEvents;");
        public ValueTask DisposeAsync()=>F.DisposeAsync();
    }
    public static async Task FeatureAndWireHistory()
    {
        await ProtocolTests.SchemaEvolution();await using var r=new Rig();await r.Start();
        using(var old=await r.Connect(false))await Refused(Page(old),StatusCode.FailedPrecondition);
        using(var fresh=r.F.Connect())
        {
            await Refused(Page(fresh),StatusCode.FailedPrecondition);var hello=await Hello(fresh);
            Check(hello.Handshake.Protocol.Minor==0&&hello.Handshake.Capabilities.Contains(FeatureCapability.LocalPeerTrustManagement));
            await Refused(Page(fresh),StatusCode.Unauthenticated);
        }
        using var c=await r.Connect();Check((await Page(c)).Peers.Count==2);
        var methods=LocalSecurityReflection.Descriptor.Services.Single().Methods;
        Check(methods.Single(m=>m.Name=="ReadPeerTrust").InputType.Fields.InDeclarationOrder().Select(f=>f.Name).SequenceEqual(new[]{"after_peer_host_id","limit"}));
        Check((int)FeatureCapability.LocalPeerTrustManagement==17);
        await using var uninitialized=new LocalSecurityRpcTests.Fixture();await uninitialized.Start();using var u=uninitialized.Connect();await Hello(u);await Refused(Page(u),StatusCode.Unauthenticated);
    }
    public static async Task OwnerRevokesExactPeerAndPreservesOthers()
    {
        await using var r=new Rig();await r.Start();var target=new ServerRef(r.F.State.HostId,Guid.NewGuid());
        var revokedRoot=Guid.NewGuid();var retainedRoot=Guid.NewGuid();
        r.Repo.IssueHost(r.Owner,r.Revision,revokedRoot,ActorRef.RemoteManager(r.Peer),HostCapability.ManageHostSettings,r.F.State.HostId,new(false,false),null);
        r.Repo.IssueServer(r.Owner,r.Revision,retainedRoot,ActorRef.RemoteManager(r.Other),ServerCapability.ViewServer,target,new(false,false),null);
        using var c=await r.Connect();var before=await Page(c);var peer=before.Peers.Single(p=>p.PeerHostId==r.Peer.ToString("D"));var audit=r.Audits;
        Check(before.AuthoritativeHostId==r.F.State.HostId.ToString("D")&&before.AuthorizationRevision==r.Revision&&peer.State==LocalPeerTrustState.Active&&peer.CurrentFingerprint.Length==64);
        var result=await Revoke(c,before,peer);Check(result.Changed&&result.Incarnation>peer.Incarnation&&result.AuthorizationRevision==r.Revision&&r.Audits==audit+1);
        var after=await Page(c);var revoked=after.Peers.Single(p=>p.PeerHostId==peer.PeerHostId);
        Check(revoked.State==LocalPeerTrustState.Revoked&&revoked.CurrentFingerprint==""&&revoked.PendingFingerprint==""&&!revoked.RecoveryRequired);
        Check(after.Peers.Single(p=>p.PeerHostId==r.Other.ToString("D")).State==LocalPeerTrustState.Active);
        Check(r.Repo.Read().HostGrants.Single(g=>g.GrantId==revokedRoot).InvalidatedUtc is not null&&r.Repo.Read().ServerGrants.Single(g=>g.GrantId==retainedRoot).InvalidatedUtc is null);
        Check(!(await Revoke(c,after,revoked)).Changed&&r.Audits==audit+1&&r.Count("SELECT COUNT(*) FROM LocalPrincipals WHERE IsOwner=1 AND State='Active';")==1);
        await Refused(Revoke(c,before,peer),StatusCode.Aborted);
    }
    public static async Task NonOwnerNeedsExactCurrentCapability()
    {
        await using var r=new Rig(true);await r.Start();using var c=await r.Connect();var audit=r.Audits;
        await Refused(Page(c),StatusCode.PermissionDenied);Check(r.Audits==audit+1);
        var row=r.Repo.ReadLocalPeerTrust(r.Owner,null,16).Peers.Single(p=>p.PeerHostId==r.Peer);
        var page=new LocalPeerTrustPageReply{AuthorizationRevision=r.Revision};var peer=new LocalPeerTrustItem{PeerHostId=r.Peer.ToString("D"),Incarnation=row.Incarnation};
        await Refused(Revoke(c,page,peer),StatusCode.PermissionDenied);Check(r.Audits==audit+2);
        r.Grant(r.Principal,HostCapability.ManagePermissions);await Refused(Page(c),StatusCode.PermissionDenied);
        var grant=r.Grant(r.Principal);var permitted=await Page(c);Check((await Revoke(c,permitted,permitted.Peers.Single(p=>p.PeerHostId==peer.PeerHostId))).Changed);
        r.Repo.InvalidateHostSubtree(r.Owner,r.Revision,grant);await Refused(Page(c),StatusCode.PermissionDenied);
    }
    public static async Task CurrentIdentityIsRechecked()
    {
        foreach(var mutation in new[]{"key","native","state"})
        {
            await using var r=new Rig(true);await r.Start();r.Grant(r.Principal);using var c=await r.Connect();await Page(c);
            if(mutation=="state")r.F.State.Repository.RevokePrincipal(r.Owner,r.Principal);
            else r.F.State.Sql(mutation switch{
                "key"=>$"UPDATE LocalPrincipals SET PublicVerificationKey='{Convert.ToBase64String(r.F.State.OwnerKey.PublicKey)}' WHERE LocalPrincipalId='{r.Principal:D}';",
                _=>$"UPDATE LocalPrincipals SET OsPrincipalRef='different-native-user' WHERE LocalPrincipalId='{r.Principal:D}';"});
            await Refused(Page(c),StatusCode.Unauthenticated);
        }
    }
    public static async Task PagesAreBoundedCurrentObservations()
    {
        await using var r=new Rig();await r.Start();for(var i=0;i<33;i++)r.Add(Guid.NewGuid());using var c=await r.Connect();
        var audit=r.Audits;var seen=new HashSet<string>();var cursor="";
        do{var page=await Page(c,cursor);Check(page.Peers.Count<=16&&page.CalculateSize()<=16*1024&&page.AuthorizationRevision==r.Revision);foreach(var p in page.Peers)Check(seen.Add(p.PeerHostId));cursor=page.NextAfterPeerHostId;}while(cursor!="");
        Check(seen.Count==35&&r.Audits==audit);
        await Refused(Page(c,limit:17),StatusCode.InvalidArgument);await Refused(Page(c,"not-an-id"),StatusCode.InvalidArgument);await Refused(Page(c,Guid.Empty.ToString("D")),StatusCode.InvalidArgument);
        var old=await Page(c,limit:1);r.Add(Guid.NewGuid());var next=await Page(c,old.NextAfterPeerHostId,1);Check(next.AuthorizationRevision>old.AuthorizationRevision);
        await Refused(Revoke(c,old,old.Peers[0]),StatusCode.Aborted);
    }
    public static async Task StaleIncarnationAndMalformedRequestsRefuse()
    {
        await using var r=new Rig();await r.Start();using var c=await r.Connect();var before=await Page(c);var peer=before.Peers.Single(p=>p.PeerHostId==r.Peer.ToString("D"));
        r.F.State.Sql($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{r.Peer:D}';");
        before.AuthorizationRevision=r.Revision;await Refused(Revoke(c,before,peer),StatusCode.FailedPrecondition);
        foreach(var request in new[]{new LocalRevokePeerRequest(),new(){PeerHostId=r.Peer.ToString("D"),ExpectedAuthorizationRevision=-1,ExpectedIncarnation=peer.Incarnation},new(){PeerHostId=r.F.State.HostId.ToString("D"),ExpectedAuthorizationRevision=r.Revision,ExpectedIncarnation=peer.Incarnation}})
            await Refused(c.Call<LocalRevokePeerRequest,LocalRevokePeerReply>("RevokePeerTrust",request),StatusCode.InvalidArgument);
        Check((await Page(c)).Peers.Single(p=>p.PeerHostId==peer.PeerHostId).RecoveryRequired);
    }
    public static async Task AuditFailureRollsBackAndErrorsStayBounded()
    {
        await using var r=new Rig();await r.Start();using var c=await r.Connect();var page=await Page(c);var peer=page.Peers[0];var audit=r.Audits;
        r.F.State.Sql("CREATE TRIGGER trust_rpc_failure BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT,'secret-canary-not-for-rpc'); END;");
        try{await Revoke(c,page,peer);throw new Exception("Audit failure ignored.");}
        catch(RpcException ex){Check(ex.StatusCode==StatusCode.Internal&&ex.Status.Detail=="Local trust request failed.");}
        Check((await Page(c)).Equals(page)&&r.Audits==audit);r.F.State.Sql("DROP TRIGGER trust_rpc_failure;");
        Check((await Revoke(c,page,peer)).Changed);
    }
    public static async Task ConcurrentRevocationHasOneWinner()
    {
        await using var r=new Rig();await r.Start();using var a=await r.Connect();using var b=await r.Connect();var page=await Page(a);var audit=r.Audits;
        async Task<bool> Attempt(LocalSecurityRpcTests.Client c){try{return (await Revoke(c,page,page.Peers[0])).Changed;}catch(RpcException ex){Check(ex.StatusCode==StatusCode.Aborted);return false;}}
        var result=await Task.WhenAll(Attempt(a),Attempt(b));Check(result.Count(v=>v)==1&&r.Audits==audit+1);
    }
}
