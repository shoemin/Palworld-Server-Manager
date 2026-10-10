using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using ActorRef=PalworldServerManager.Core.Authorization.ActorRef;
using HostCapability=PalworldServerManager.Core.Authorization.HostCapability;

namespace PalworldServerManager.SelfTest;

internal static partial class LocalOwnerPairingRpcTests
{
    private static Task<LocalHandshakeReply> RemoteHello(LocalSecurityRpcTests.Client c,bool feature=true)
    {
        var h=new Handshake{Protocol=new(){Major=1,Minor=0},Capabilities={FeatureCapability.LocalPrincipalSecurity}};
        if(feature)h.Capabilities.Add(FeatureCapability.LocalRemoteTrustAdministration);
        return c.Call<Handshake,LocalHandshakeReply>("Negotiate",h);
    }
    private static async Task<LocalSecurityRpcTests.Client> RemoteClient(Fixture f,bool feature=true)
    {var c=f.Client();try{await RemoteHello(c,feature);await c.Authenticate(f.State.State.HostId,f.Principal,f.Key);return c;}catch{c.Dispose();throw;}}
    private static Task<PeerTrustPageReply> RemotePage(LocalSecurityRpcTests.Client c,Fixture receiver,Uri? address=null)
        =>c.Call<LocalRemotePeerTrustPageRequest,PeerTrustPageReply>("ReadRemotePeerTrust",new(){Destination=Contact(receiver.State.State.HostId,address??receiver.Generation.Endpoints!.Value.Peer)});
    private static Task<PeerRevokeTrustReply> RemoteRevoke(LocalSecurityRpcTests.Client c,Fixture receiver,Guid target,long revision,long incarnation,Uri? address=null)
        =>c.Call<LocalRemoteRevokePeerRequest,PeerRevokeTrustReply>("RevokeRemotePeerTrust",new(){Destination=Contact(receiver.State.State.HostId,address??receiver.Generation.Endpoints!.Value.Peer),PeerHostId=target.ToString("D"),ExpectedAuthorizationRevision=revision,ExpectedIncarnation=incarnation});
    private static Guid MachinePermission(Fixture receiver,Fixture source)
    {var repo=NetworkGrants(receiver);var id=Guid.NewGuid();repo.IssueHost(NetworkOwner(receiver),repo.Read().Revision,id,ActorRef.RemoteManager(source.State.State.HostId),HostCapability.ManageTrustedManagers,receiver.State.State.HostId,new(false,false),null);return id;}
    private static Guid OutgoingPermission(Fixture source,Fixture receiver)
    {var repo=NetworkGrants(source);var id=Guid.NewGuid();repo.IssueHost(NetworkOwner(source),repo.Read().Revision,id,ActorRef.LocalPrincipal(source.Principal),HostCapability.ManageTrustedManagers,receiver.State.State.HostId,new(false,false),null);return id;}
    private static Guid AddManagedPeer(Fixture receiver)
    {var id=Guid.NewGuid();receiver.State.State.Execute($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{id:D}','Active','{new string('E',64)}','fixture');");return id;}
    private static long PeerIncarnation(Fixture receiver,Guid target)=>HostDatabase.QueryScalarLong(receiver.State.State.Writer,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{target:D}';");
    public static async Task RemoteTrustFeatureAndIdentity()
    {
        await ProtocolTests.SchemaEvolution();Check((int)FeatureCapability.LocalRemoteTrustAdministration==22);
        await using(var local=new LocalSecurityRpcTests.Fixture(true))
        {await local.Start();using var c=local.Connect();var h=await RemoteHello(c);Check(!h.Handshake.Capabilities.Contains(FeatureCapability.LocalRemoteTrustAdministration));}
        await using var a=new Fixture();await using var b=new Fixture();await a.Start(new RefusingFactory());await b.Start(new RefusingFactory());await NetworkPair(a,b);MachinePermission(b,a);
        using(var anonymous=a.Client())
        {var h=await RemoteHello(anonymous);Check(h.Handshake.Protocol.Minor==0&&h.Handshake.Capabilities.Contains(FeatureCapability.LocalRemoteTrustAdministration));await Refused(RemotePage(anonymous,b),StatusCode.Unauthenticated);}
        using(var old=await RemoteClient(a,false))await Refused(RemotePage(old,b),StatusCode.FailedPrecondition);
        using var c2=await RemoteClient(a);Check((await RemotePage(c2,b)).AuthoritativeHostId==b.State.State.HostId.ToString("D"));
    }
    public static async Task RemoteTrustBothCeilingsAndNoDeniedContact()
    {
        await using var a=new Fixture(false);await using var b=new Fixture();await a.Start(new RefusingFactory());await b.Start(new RefusingFactory());await NetworkPair(a,b);var machine=MachinePermission(b,a);
        using var c=await RemoteClient(a);var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        try
        {
            var address=new Uri($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
            await Refused(RemotePage(c,b,address).WaitAsync(TimeSpan.FromSeconds(5)),StatusCode.PermissionDenied);
            NetworkGrant(a,HostCapability.ManageTrustedManagers);NetworkGrant(a,HostCapability.ManagePermissions,b.State.State.HostId);
            await Refused(RemotePage(c,b,address).WaitAsync(TimeSpan.FromSeconds(5)),StatusCode.PermissionDenied);Check(!listener.Pending());
        }
        finally{listener.Stop();}
        OutgoingPermission(a,b);var remote=NetworkGrants(b);remote.InvalidateHostSubtree(NetworkOwner(b),remote.Read().Revision,machine);
        await Refused(RemotePage(c,b),StatusCode.PermissionDenied);MachinePermission(b,a);Check((await RemotePage(c,b)).Peers.Count==1);
    }
    public static async Task RemoteTrustOwnerCannotBypassMachineAndExactRevoke()
    {
        await using var a=new Fixture();await using var b=new Fixture();await a.Start(new RefusingFactory());await b.Start(new RefusingFactory());await NetworkPair(a,b);var target=AddManagedPeer(b);var other=AddManagedPeer(b);
        using var c=await RemoteClient(a);await Refused(RemotePage(c,b),StatusCode.PermissionDenied);MachinePermission(b,a);
        var page=await RemotePage(c,b);var row=page.Peers.Single(p=>p.PeerHostId==target.ToString("D"));
        var result=await RemoteRevoke(c,b,target,page.AuthorizationRevision,row.Incarnation);
        Check(result.Changed&&result.PeerHostId==row.PeerHostId&&result.Incarnation>row.Incarnation&&b.State.State.Repository.Read(target)!.State=="Revoked"&&b.State.State.Repository.Read(other)!.State=="Active");
        var retry=await RemoteRevoke(c,b,target,result.AuthorizationRevision,result.Incarnation);Check(!retry.Changed);
        Check(NetworkGrants(b).Read().Policy.IsOwner(ActorRef.LocalPrincipal(b.State.State.OwnerId))&&a.State.State.Repository.Read(b.State.State.HostId)!.State=="Active");
    }
    public static async Task RemoteTrustLatePermissionPreventsCommand()
    {
        await using var a=new Fixture(false);await using var b=new Fixture();await a.Start(new RefusingFactory());await b.Start(new RefusingFactory());await NetworkPair(a,b);MachinePermission(b,a);var grant=OutgoingPermission(a,b);
        var target=AddManagedPeer(b);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var commands=0;
        await using var app=WindowsHostComposition.BuildPeerApplication(b.State.Runtime,b.State.Certificate.Value,new(IPAddress.Loopback,0));
        app.Use(async(context,next)=>{if(context.Request.Path.Value!.EndsWith("/Negotiate",StringComparison.Ordinal)){entered.TrySetResult();await release.Task;}if(context.Request.Path.Value!.EndsWith("/RevokeManagedPeerTrust",StringComparison.Ordinal))Interlocked.Increment(ref commands);await next(context);});
        await app.StartAsync();using var c=await RemoteClient(a);var task=RemoteRevoke(c,b,target,NetworkGrants(b).Read().Revision,PeerIncarnation(b,target),new(app.Urls.Single()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var local=NetworkGrants(a);local.InvalidateHostSubtree(NetworkOwner(a),local.Read().Revision,grant);release.TrySetResult();
            await Refused(task,StatusCode.PermissionDenied);Check(commands==0&&b.State.State.Repository.Read(target)!.State=="Active");
        }
        finally{release.TrySetResult();await app.StopAsync();}
    }
    public static async Task RemoteTrustIndependentCommitAfterLocalChange()
    {
        await using var a=new Fixture();await using var b=new Fixture();await a.Start(new RefusingFactory());await b.Start(new RefusingFactory());await NetworkPair(a,b);MachinePermission(b,a);var target=AddManagedPeer(b);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app=WindowsHostComposition.BuildPeerApplication(b.State.Runtime,b.State.Certificate.Value,new(IPAddress.Loopback,0));
        app.Use(async(context,next)=>{if(context.Request.Path.Value!.EndsWith("/RevokeManagedPeerTrust",StringComparison.Ordinal)){entered.TrySetResult();await release.Task;}await next(context);});
        await app.StartAsync();using var c=await RemoteClient(a);var task=RemoteRevoke(c,b,target,NetworkGrants(b).Read().Revision,PeerIncarnation(b,target),new(app.Urls.Single()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));a.State.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");release.TrySetResult();
            await Refused(task,StatusCode.Unauthenticated);Check(b.State.State.Repository.Read(target)!.State=="Revoked");
            Check(HostDatabase.QueryScalarLong(b.State.State.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked';")==1);
        }
        finally{release.TrySetResult();await app.StopAsync();}
    }
    public static async Task RemoteTrustSelfRevokeAndStaleRequests()
    {
        await using var a=new Fixture();await using var b=new Fixture();await a.Start(new RefusingFactory());await b.Start(new RefusingFactory());await NetworkPair(a,b);MachinePermission(b,a);using var c=await RemoteClient(a);
        var page=await RemotePage(c,b);var peer=page.Peers.Single();
        await Refused(RemoteRevoke(c,b,a.State.State.HostId,page.AuthorizationRevision-1,peer.Incarnation),StatusCode.Aborted);
        await Refused(RemoteRevoke(c,b,a.State.State.HostId,page.AuthorizationRevision,peer.Incarnation+1),StatusCode.FailedPrecondition);
        Check((await RemoteRevoke(c,b,a.State.State.HostId,page.AuthorizationRevision,peer.Incarnation)).Changed);
        await Refused(RemotePage(c,b),StatusCode.Unavailable);Check(b.State.State.Repository.Read(a.State.State.HostId)!.State=="Revoked");
    }
    public static Task RemoteTrustReplyValidation()
    {
        void Reject(Action call){try{call();}catch(AuthenticationException){return;}throw new Exception("Malformed reply accepted.");}
        var host=Guid.NewGuid().ToString("D");var peer=Guid.NewGuid().ToString("D");var request=new PeerTrustPageRequest{AuthoritativeHostId=host,Limit=1};
        var page=new PeerTrustPageReply{AuthoritativeHostId=host,AuthorizationRevision=1,Peers={new PeerManagedTrustItem{PeerHostId=peer,State=PeerManagedTrustState.Active,Incarnation=1,CurrentFingerprint=new string('A',64)}},NextAfterPeerHostId=peer};
        PeerTrustAdministrationRpcClient.ValidatePage(page,request);
        foreach(var mutate in new Action<PeerTrustPageReply>[] {p=>p.AuthoritativeHostId=peer,p=>p.AuthorizationRevision=-1,p=>p.Peers[0].State=(PeerManagedTrustState)999,p=>p.Peers[0].CurrentFingerprint="secret",p=>p.Peers[0].Incarnation=0,p=>p.Peers.Add(p.Peers[0].Clone()),p=>p.NextAfterPeerHostId=host,p=>p.Peers[0].PeerHostId=host,p=>p.Peers[0].PeerHostId="malformed",p=>p.Peers[0].PeerHostId=Guid.Empty.ToString("D")})
        {var bad=page.Clone();mutate(bad);Reject(()=>PeerTrustAdministrationRpcClient.ValidatePage(bad,request));}
        var revoke=new PeerRevokeTrustRequest{AuthoritativeHostId=host,PeerHostId=peer,ExpectedAuthorizationRevision=2,ExpectedIncarnation=3};
        var result=new PeerRevokeTrustReply{AuthoritativeHostId=host,PeerHostId=peer,AuthorizationRevision=2,Incarnation=3,Changed=true};
        PeerTrustAdministrationRpcClient.ValidateRevocation(result,revoke); // marker-only change may retain both observations
        foreach(var mutate in new Action<PeerRevokeTrustReply>[] {p=>p.AuthoritativeHostId=peer,p=>p.PeerHostId=host,p=>p.AuthorizationRevision=1,p=>p.Incarnation=2,p=>{p.Changed=false;p.Incarnation=4;}})
        {var bad=result.Clone();mutate(bad);Reject(()=>PeerTrustAdministrationRpcClient.ValidateRevocation(bad,revoke));}
        return Task.CompletedTask;
    }
    public static async Task RemoteTrustBoundsAndCancellation()
    {
        await using var a=new Fixture();await a.Start(new RefusingFactory());using var c=await RemoteClient(a);
        foreach(var request in new[]{new LocalRemotePeerTrustPageRequest(),new(){Destination=Contact(Guid.NewGuid(),new("https://127.0.0.1:1")),Limit=17},new(){Destination=Contact(a.State.State.HostId,new("https://127.0.0.1:1"))}})
            await Refused(c.Call<LocalRemotePeerTrustPageRequest,PeerTrustPageReply>("ReadRemotePeerTrust",request),StatusCode.InvalidArgument);
        using var auth=a.Authentication();var helper=new LocalPeerTrustNetworkActions(a.State.State.Database,a.State.State.HostId,a.State.State.Time);using var cancel=new CancellationTokenSource();cancel.Cancel();
        try{await helper.ReadRemote(a.Generation,auth,Guid.NewGuid(),HostReachableAddress.Parse("127.0.0.1",1),null,16,cancel.Token);throw new Exception("Canceled route accepted.");}catch(OperationCanceledException){}
        await a.Generation.StopAsync();
        try{await helper.ReadRemote(a.Generation,auth,Guid.NewGuid(),HostReachableAddress.Parse("127.0.0.1",1),null,16,default);throw new Exception("Stopped route accepted.");}catch(InvalidOperationException){}
    }
    public static async Task RemoteTrustGenerationStopDrainsActualRequest()
    {
        await using var a=new Fixture();await using var b=new Fixture();await a.Start(new RefusingFactory());await b.Start(new RefusingFactory());await NetworkPair(a,b);MachinePermission(b,a);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app=WindowsHostComposition.BuildPeerApplication(b.State.Runtime,b.State.Certificate.Value,new(IPAddress.Loopback,0));
        app.Use(async(context,next)=>{if(context.Request.Path.Value!.EndsWith("/Negotiate",StringComparison.Ordinal)){entered.TrySetResult();await release.Task;}await next(context);});
        await app.StartAsync();using var auth=a.Authentication();var helper=new LocalPeerTrustNetworkActions(a.State.State.Database,a.State.State.HostId,a.State.State.Time);var address=new Uri(app.Urls.Single());
        var task=helper.ReadRemote(a.Generation,auth,b.State.State.HostId,HostReachableAddress.Parse(address.Host,address.Port),null,16,default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Check(!task.IsCompleted);await a.Generation.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(task.IsCompleted&&!task.IsCompletedSuccessfully);
            try{await task;throw new Exception("Stopped request succeeded.");}catch(OperationCanceledException){}catch(RpcException ex)when(ex.StatusCode is StatusCode.Cancelled or StatusCode.Unavailable){}
            Check(b.State.State.Repository.Read(a.State.State.HostId)!.State=="Active"&&b.State.State.Count("PeerUnpairReceipts")==0);
        }
        finally{release.TrySetResult();await app.StopAsync();}
    }
}
