using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Core.Security;
using ActorRef=PalworldServerManager.Core.Authorization.ActorRef;
using HostCapability=PalworldServerManager.Core.Authorization.HostCapability;

namespace PalworldServerManager.SelfTest;

internal static partial class LocalOwnerPairingRpcTests
{
    private static Task<LocalHandshakeReply> TrustHello(LocalSecurityRpcTests.Client c,bool prepare=true,bool recovery=true)
    {
        var hello=new Handshake{Protocol=new(){Major=1,Minor=0},Capabilities={FeatureCapability.LocalPrincipalSecurity,FeatureCapability.LocalPeerTrustManagement}};
        if(prepare)hello.Capabilities.Add(FeatureCapability.LocalPeerUnpairPreparation);
        if(recovery)hello.Capabilities.Add(FeatureCapability.LocalOwnerPeerRecovery);
        return c.Call<Handshake,LocalHandshakeReply>("Negotiate",hello);
    }
    private static async Task<LocalSecurityRpcTests.Client> TrustClient(Fixture f)
    {
        var c=f.Client();try{await TrustHello(c);await c.Authenticate(f.State.State.HostId,f.Principal,f.Key);return c;}catch{c.Dispose();throw;}
    }
    private static LocalPeerContactRequest Contact(Guid peer,Uri address)=>new(){PeerHostId=peer.ToString("D"),ReachableHost=address.Host,PeerPort=(uint)address.Port};
    private static Task<LocalPreparePeerUnpairReply> Prepare(LocalSecurityRpcTests.Client c,Guid peer,Uri address)
        =>c.Call<LocalPeerContactRequest,LocalPreparePeerUnpairReply>("PreparePeerUnpair",Contact(peer,address));
    private static Task<LocalCompletePeerRecoveryReply> Recover(LocalSecurityRpcTests.Client c,Guid peer,Uri address)
        =>c.Call<LocalPeerContactRequest,LocalCompletePeerRecoveryReply>("CompletePeerRecovery",Contact(peer,address));
    private static GrantPolicyRepository NetworkGrants(Fixture f)=>new(f.State.State.Database,f.State.State.HostId,f.State.State.Time);
    private static LocalPrincipalMutationActor NetworkOwner(Fixture f)=>new(f.State.State.HostId,f.State.State.OwnerId,
        f.Principal==f.State.State.OwnerId?f.Native:"native-owner",f.Principal==f.State.State.OwnerId?Convert.ToBase64String(f.Key.PublicKey):"fixture-public");
    private static void NetworkGrant(Fixture f,HostCapability capability,Guid? target=null)
    {
        var repo=NetworkGrants(f);repo.IssueHost(NetworkOwner(f),repo.Read().Revision,Guid.NewGuid(),ActorRef.LocalPrincipal(f.Principal),capability,target??f.State.State.HostId,new(false,false),null);
    }
    private static async Task NetworkPair(Fixture a,Fixture b)
    {
        a.State.Bind(b.State);b.State.Bind(a.State);await a.Generation.ActivateAsync(b.State.State.HostId,b.Generation.Endpoints!.Value.Peer);
    }
    private static void RecoveryPair(Fixture a,Fixture b)
    {
        a.State.Bind(b.State);b.State.Bind(a.State);
        foreach(var (local,remote,old) in new[]{(a,b,'E'),(b,a,'F')})
        {
            // Explicit historical verified-binding fixture, followed by canonical Owner approval.
            local.State.State.Execute($"UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{new string(old,64)}',PeerRecoveryRequired=1;");
            var id=local.State.State.Repository.RecordOwnerVerifiedBinding(NetworkOwner(local),remote.State.State.HostId,remote.State.Pin,local.State.Pin).ReplacementId!.Value;
            var repo=NetworkGrants(local);repo.ApprovePeerReplacement(NetworkOwner(local),repo.Read().Revision,id);
        }
    }
    private static async Task RevokeThroughRpc(Fixture local,Fixture peer,LocalSecurityRpcTests.Client c)
    {
        var page=await c.Call<LocalPeerTrustPageRequest,LocalPeerTrustPageReply>("ReadPeerTrust",new());
        var item=page.Peers.Single(p=>p.PeerHostId==peer.State.State.HostId.ToString("D"));
        var result=await c.Call<LocalRevokePeerRequest,LocalRevokePeerReply>("RevokePeerTrust",new(){PeerHostId=item.PeerHostId,ExpectedIncarnation=item.Incarnation,ExpectedAuthorizationRevision=page.AuthorizationRevision});
        Check(result.Changed&&local.State.State.Repository.Read(peer.State.State.HostId)!.State=="Revoked");
    }
    private static async Task WaitRevoked(Fixture receiver,Fixture sender)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(receiver.State.State.Repository.Read(sender.State.State.HostId)!.State!="Revoked")await Task.Delay(10,timeout.Token);
        Check(receiver.State.State.Count("PeerUnpairReceipts")==1);
    }
    public static async Task TrustNetworkFeaturesAndClosedProfiles()
    {
        await ProtocolTests.SchemaEvolution();Check((int)FeatureCapability.LocalPeerUnpairPreparation==19&&(int)FeatureCapability.LocalOwnerPeerRecovery==20);
        await using(var local=new LocalSecurityRpcTests.Fixture(true))
        {
            await local.Start();using var c=local.Connect();var hello=await TrustHello(c);
            Check(!hello.Handshake.Capabilities.Contains(FeatureCapability.LocalPeerUnpairPreparation)&&!hello.Handshake.Capabilities.Contains(FeatureCapability.LocalOwnerPeerRecovery));
            await Refused(Prepare(c,Guid.NewGuid(),new("https://127.0.0.1:1")),StatusCode.FailedPrecondition);
        }
        await using var f=new Fixture();await f.Start(new RefusingFactory());using var anonymous=f.Client();var offer=await TrustHello(anonymous);
        Check(offer.Handshake.Protocol.Minor==0&&offer.Handshake.Capabilities.Contains(FeatureCapability.LocalPeerUnpairPreparation)&&offer.Handshake.Capabilities.Contains(FeatureCapability.LocalOwnerPeerRecovery));
        await Refused(Prepare(anonymous,Guid.NewGuid(),new("https://127.0.0.1:1")),StatusCode.Unauthenticated);
        await Refused(Recover(anonymous,Guid.NewGuid(),new("https://127.0.0.1:1")),StatusCode.Unauthenticated);
        using var old=f.Client();await TrustHello(old,false,false);await old.Authenticate(f.State.State.HostId,f.Principal,f.Key);
        await Refused(Prepare(old,Guid.NewGuid(),new("https://127.0.0.1:1")),StatusCode.FailedPrecondition);
        await Refused(Recover(old,Guid.NewGuid(),new("https://127.0.0.1:1")),StatusCode.FailedPrecondition);
        var fields=LocalPeerContactRequest.Descriptor.Fields.InDeclarationOrder().Select(x=>x.Name);
        Check(fields.SequenceEqual(new[]{"peer_host_id","reachable_host","peer_port"}));
    }
    public static async Task TrustNetworkDeniesBeforeOutboundContact()
    {
        await using var f=new Fixture(false);await f.Start(new RefusingFactory());using var c=await TrustClient(f);
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        try
        {
            var address=new Uri($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
            await Refused(Prepare(c,Guid.NewGuid(),address).WaitAsync(TimeSpan.FromSeconds(5)),StatusCode.PermissionDenied);
            NetworkGrant(f,HostCapability.ManagePermissions);NetworkGrant(f,HostCapability.ManageTrustedManagers,Guid.NewGuid());
            await Refused(Prepare(c,Guid.NewGuid(),address).WaitAsync(TimeSpan.FromSeconds(5)),StatusCode.PermissionDenied);
            NetworkGrant(f,HostCapability.ManageTrustedManagers);
            await Refused(Recover(c,Guid.NewGuid(),address).WaitAsync(TimeSpan.FromSeconds(5)),StatusCode.Unauthenticated);
            Check(!listener.Pending()&&f.State.State.Count("PeerUnpairReceipts")==0);
        }
        finally{listener.Stop();}
    }
    public static async Task TrustNetworkOwnerPreparesActualReciprocalUnpair()
    {
        await using var a=new Fixture();await using var b=new Fixture();await a.Start(new RefusingFactory());await b.Start(new RefusingFactory());await NetworkPair(a,b);
        using var c=await TrustClient(a);var peer=b.State.State.HostId;var address=b.Generation.Endpoints!.Value.Peer;
        Check((await Prepare(c,peer,address)).Prepared);Check(!(await Prepare(c,peer,address)).Prepared);
        await RevokeThroughRpc(a,b,c);await WaitRevoked(b,a);
        Check(!(await Prepare(c,peer,address)).Prepared&&a.State.State.Count("HostCapabilityGrants")==0&&b.State.State.Count("HostCapabilityGrants")==0);
    }
    public static async Task TrustNetworkDelegatedPreparationAndOfflineRevocation()
    {
        await using var a=new Fixture(false);await using var b=new Fixture();await a.Start(new RefusingFactory());await b.Start(new RefusingFactory());await NetworkPair(a,b);
        NetworkGrant(a,HostCapability.ManageTrustedManagers);using var c=await TrustClient(a);
        Check((await Prepare(c,b.State.State.HostId,b.Generation.Endpoints!.Value.Peer)).Prepared);
        await RevokeThroughRpc(a,b,c);await WaitRevoked(b,a);
        await using var offline=new Fixture();await offline.Start(new RefusingFactory());
        var remote=Guid.NewGuid();offline.State.State.Execute($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{remote:D}','Active','{new string('B',64)}','fixture');");
        using var local=await TrustClient(offline);var page=await local.Call<LocalPeerTrustPageRequest,LocalPeerTrustPageReply>("ReadPeerTrust",new());var item=page.Peers.Single();
        var result=await local.Call<LocalRevokePeerRequest,LocalRevokePeerReply>("RevokePeerTrust",new(){PeerHostId=item.PeerHostId,ExpectedIncarnation=item.Incarnation,ExpectedAuthorizationRevision=page.AuthorizationRevision});
        Check(result.Changed&&offline.State.State.Repository.Read(remote)!.State=="Revoked");
    }
    public static async Task TrustNetworkInputAndCurrentIdentityRefusals()
    {
        await using var f=new Fixture();await f.Start(new RefusingFactory());using var c=await TrustClient(f);
        foreach(var request in new[]{new LocalPeerContactRequest(),new(){PeerHostId=Guid.NewGuid().ToString("D"),ReachableHost="https://bad/path",PeerPort=1},new(){PeerHostId=Guid.NewGuid().ToString("D"),ReachableHost="127.0.0.1",PeerPort=65536},Contact(f.State.State.HostId,new("https://127.0.0.1:1"))})
        {
            await Refused(c.Call<LocalPeerContactRequest,LocalPreparePeerUnpairReply>("PreparePeerUnpair",request),StatusCode.InvalidArgument);
            await Refused(c.Call<LocalPeerContactRequest,LocalCompletePeerRecoveryReply>("CompletePeerRecovery",request),StatusCode.InvalidArgument);
        }
        f.State.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");
        await Refused(Prepare(c,Guid.NewGuid(),new("https://127.0.0.1:1")),StatusCode.Unauthenticated);
        await Refused(Recover(c,Guid.NewGuid(),new("https://127.0.0.1:1")),StatusCode.Unauthenticated);
    }
    public static async Task TrustNetworkActualIndependentRecoveryAndNoPending()
    {
        await using var a=new Fixture();await using var b=new Fixture();await a.Start(new RefusingFactory());await b.Start(new RefusingFactory());RecoveryPair(a,b);
        using var c=await TrustClient(a);var result=await Recover(c,b.State.State.HostId,b.Generation.Endpoints!.Value.Peer);
        Check(result.Sent==LocalPeerRecoveryPushResult.Confirmed&&result.Received==LocalPeerRecoveryPullResult.Confirmed);
        foreach(var (local,remote) in new[]{(a,b),(b,a)})
        {
            Check(!local.State.State.Repository.Read(remote.State.State.HostId)!.RecoveryRequired&&local.State.State.Count("PeerRecoveryCompletionReceipts")==1);
            Check(local.State.State.Count("HostCapabilityGrants")==0&&local.State.State.Count("ServerCapabilityGrants")==0);
        }
        var repeat=await Recover(c,b.State.State.HostId,b.Generation.Endpoints.Value.Peer);
        Check(repeat.Sent==LocalPeerRecoveryPushResult.NoPending&&repeat.Received==LocalPeerRecoveryPullResult.NoPending);
    }
    public static async Task TrustNetworkLatePreparationAuthorityCancels()
    {
        await using var a=new Fixture();await using var b=new Fixture();await a.Start(new RefusingFactory());await b.Start(new RefusingFactory());await NetworkPair(a,b);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app=WindowsHostComposition.BuildPeerApplication(b.State.Runtime,b.State.Certificate.Value,new(IPAddress.Loopback,0),next=>async connection=>
        {entered.TrySetResult();await release.Task;await next(connection);});
        await app.StartAsync();using var c=await TrustClient(a);var task=Prepare(c,b.State.State.HostId,new(app.Urls.Single()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));a.State.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");
            release.TrySetResult();await Refused(task,StatusCode.Unauthenticated);
            Check(a.State.State.Repository.Read(b.State.State.HostId)!.State=="Active"&&b.State.State.Count("PeerUnpairReceipts")==0);
        }
        finally{release.TrySetResult();await app.StopAsync();}
    }
    public static async Task TrustNetworkLateOwnerStopsRecoveryFollowup()
    {
        await using var a=new Fixture();await using var b=new Fixture();await a.Start(new RefusingFactory());await b.Start(new RefusingFactory());RecoveryPair(a,b);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app=WindowsHostComposition.BuildPeerApplication(b.State.Runtime,b.State.Certificate.Value,new(IPAddress.Loopback,0));
        app.Use(async(context,next)=>
        {
            if(!context.Request.Path.Value!.EndsWith("/ReceiveRecoveryCompletion",StringComparison.Ordinal)){await next(context);return;}
            entered.TrySetResult();await release.Task;await next(context);
        });
        await app.StartAsync();using var c=await TrustClient(a);var task=Recover(c,b.State.State.HostId,new(app.Urls.Single()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Check(b.State.State.Repository.Read(a.State.State.HostId)!.RecoveryRequired&&!task.IsCompleted);
            a.State.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");release.TrySetResult();await Refused(task,StatusCode.Unauthenticated);
            Check(a.State.State.Repository.Read(b.State.State.HostId)!.RecoveryRequired&&a.State.State.Count("PeerRecoveryCompletionReceipts")==0);
            Check(!b.State.State.Repository.Read(a.State.State.HostId)!.RecoveryRequired&&b.State.State.Count("PeerRecoveryCompletionReceipts")==1);
        }
        finally{release.TrySetResult();await app.StopAsync();}
    }
    public static async Task TrustNetworkCancellationAndForeignGeneration()
    {
        await using var f=new Fixture();await f.Start(new RefusingFactory());using var auth=f.Authentication();var peer=Guid.NewGuid();
        var helper=new LocalPeerTrustNetworkActions(f.State.State.Database,f.State.State.HostId,f.State.State.Time);
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        try{await helper.Prepare(f.Generation,auth,peer,HostReachableAddress.Parse("127.0.0.1",1),canceled.Token);throw new Exception("Canceled preparation succeeded.");}catch(OperationCanceledException){}
        var foreign=new LocalPeerTrustNetworkActions(f.State.State.Database,Guid.NewGuid());
        try{await foreign.Prepare(f.Generation,auth,peer,HostReachableAddress.Parse("127.0.0.1",1),default);throw new Exception("Foreign generation admitted.");}catch(AuthenticationException){}
        await f.Generation.StopAsync();
        try{await helper.CompleteRecovery(f.Generation,auth,peer,HostReachableAddress.Parse("127.0.0.1",1),default);throw new Exception("Stopped generation admitted.");}catch(InvalidOperationException){}
    }
    public static async Task TrustNetworkFailedContactKeepsOriginalApproval()
    {
        await using var a=new Fixture();await using var b=new Fixture();await a.Start(new RefusingFactory());await b.Start(new RefusingFactory());RecoveryPair(a,b);
        using var c=await TrustClient(a);
        await Refused(Recover(c,Guid.NewGuid(),b.Generation.Endpoints!.Value.Peer),StatusCode.Unavailable);
        Check(a.State.State.Repository.Read(b.State.State.HostId)!.RecoveryRequired&&b.State.State.Repository.Read(a.State.State.HostId)!.RecoveryRequired);
        var result=await Recover(c,b.State.State.HostId,b.Generation.Endpoints.Value.Peer);
        Check(result.Sent==LocalPeerRecoveryPushResult.Confirmed&&result.Received==LocalPeerRecoveryPullResult.Confirmed);
    }
}
