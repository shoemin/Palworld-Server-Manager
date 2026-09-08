using System.Security.Authentication;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.PeerSecurityRpcTests;

namespace PalworldServerManager.SelfTest;

internal static class LiveUnpairConnectionTests
{
    private static void Check(bool value){if(!value)throw new Exception("Live unpair connection assertion failed.");}
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static PeerTrustRevocationResult Revoke(Fixture local,Fixture peer)
    {
        var repo=new GrantPolicyRepository(local.State.Database,local.State.HostId,local.State.Time);
        var incarnation=HostDatabase.QueryScalarLong(local.State.Writer,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{peer.State.HostId:D}';");
        return repo.RevokeLocalPeerTrust(new(local.State.HostId,local.State.OwnerId,"native-owner","fixture-public"),repo.Read().Revision,peer.State.HostId,incarnation);
    }
    private sealed class Pair:IAsyncDisposable
    {
        internal readonly Fixture A=new(),B=new();
        internal async Task Start(bool active=true)
        {
            A.Bind(B);B.Bind(A);await B.Start();
            if(active)await WindowsHostComposition.CreatePeerActivationClient(A.Runtime,A.Certificate.Value).FinalizeAsync(B.State.HostId,B.Address);
        }
        internal PeerUnpairConnectionFactory Client(IPeerHttpTransportFactory? transport=null)=>transport is null?
            WindowsHostComposition.CreatePeerUnpairConnectionFactory(A.Runtime,A.Certificate.Value):new(A.Runtime,transport);
        public async ValueTask DisposeAsync(){try{await B.DisposeAsync();}finally{await A.DisposeAsync();}}
    }
    // Real Windows TLS under a controlled Host-side response hold. Not a remote-server hang fixture.
    private sealed class TrackingFactory(Fixture client,bool hold=false):IPeerHttpTransportFactory
    {
        internal int Observed,Disposed;internal IPeerHttpTransport? Native;
        internal readonly TaskCompletionSource Entered=Signal();
        public IPeerHttpTransport Create(Func<string,bool> accepts,Action<PeerTlsConnectionIdentity>? observed=null)
        {
            Native=new WindowsPeerHttpTransportFactory(client.Certificate.Value).Create(accepts,proof=>{Interlocked.Increment(ref Observed);observed?.Invoke(proof);});
            return new Wrapped(this,Native,hold);
        }
        private sealed class Wrapped:IPeerHttpTransport
        {
            private readonly TrackingFactory owner;private readonly IPeerHttpTransport native;private int disposed;
            internal Wrapped(TrackingFactory owner,IPeerHttpTransport native,bool hold){this.owner=owner;this.native=native;Handler=new Hold(owner,hold){InnerHandler=native.Handler};}
            public HttpMessageHandler Handler{get;}
            public PeerTlsConnectionIdentity Identity=>native.Identity;
            public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0){Handler.Dispose();native.Dispose();Interlocked.Increment(ref owner.Disposed);}}
        }
        private sealed class Hold(TrackingFactory owner,bool hold):DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
            {
                var reply=await base.SendAsync(request,ct).ConfigureAwait(false);
                if(hold&&request.RequestUri!.AbsolutePath.EndsWith("/ReceiveUnpair",StringComparison.Ordinal))
                {
                    owner.Entered.TrySetResult();
                    try{await Task.Delay(Timeout.Infinite,ct).ConfigureAwait(false);}catch{reply.Dispose();throw;}
                }
                return reply;
            }
        }
    }
    public static async Task ActualScopedDeliveryAndSingleAttempt()
    {
        await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A);
        PeerUnpairConnection? captured=null;PeerTrustRevocationResult? revoked=null;
        var result=await f.Client(tracked).WithConnection(f.B.State.HostId,f.B.Address,async(held,ct)=>
        {
            captured=held;revoked=Revoke(f.A,f.B);
            Check(f.A.State.Repository.Read(f.B.State.HostId)!.State=="Revoked"&&f.B.State.Repository.Read(f.A.State.HostId)!.State=="Active");
            Check(await held.Send(revoked)==PeerUnpairDelivery.Confirmed);
            Check(await held.Send(revoked)==PeerUnpairDelivery.Unconfirmed);return true;
        });
        Check(result&&tracked.Observed==1&&tracked.Disposed==1&&f.B.State.Repository.Read(f.A.State.HostId)!.State=="Revoked");
        Check(await captured!.Send(revoked!)==PeerUnpairDelivery.Unconfirmed);
        Check(HostDatabase.QueryScalarLong(f.A.State.Writer,$"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked' AND ActorLocalPrincipalId='{f.A.State.OwnerId:D}';")==1);
        Check(HostDatabase.QueryScalarLong(f.B.State.Writer,$"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked' AND ActorPeerHostId='{f.A.State.HostId:D}';")==1);
    }
    public static async Task UncommittedOrLaterTransitionNeverSends()
    {
        await using var f=new Pair();await f.Start();
        await f.Client().WithConnection(f.B.State.HostId,f.B.Address,async(held,ct)=>
        {
            var original=HostDatabase.QueryScalarLong(f.A.State.Writer,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{f.B.State.HostId:D}';");
            var fake=new PeerTrustRevocationResult(f.B.State.HostId,0,original+1,true,0,0,original);
            Check(await held.Send(fake)==PeerUnpairDelivery.Unconfirmed);
            Check(f.A.State.Repository.Read(f.B.State.HostId)!.State=="Active"&&f.B.State.Repository.Read(f.A.State.HostId)!.State=="Active");return true;
        });
        await f.Client().WithConnection(f.B.State.HostId,f.B.Address,async(held,ct)=>
        {
            var revoked=Revoke(f.A,f.B);
            f.A.State.Execute($"UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{f.B.Pin}' WHERE PeerHostId='{f.B.State.HostId:D}';");
            var later=Revoke(f.A,f.B);Check(await held.Send(later)==PeerUnpairDelivery.Unconfirmed);
            Check(f.B.State.Repository.Read(f.A.State.HostId)!.State=="Active");return true;
        });
    }
    public static async Task LostActualTransportCannotAuthenticateAgain()
    {
        await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A);
        await f.Client(tracked).WithConnection(f.B.State.HostId,f.B.Address,async(held,ct)=>
        {
            tracked.Native!.Dispose();var revoked=Revoke(f.A,f.B);
            Check(await held.Send(revoked)==PeerUnpairDelivery.Unconfirmed);return true;
        });
        Check(tracked.Observed==1&&tracked.Disposed==1&&f.A.State.Repository.Read(f.B.State.HostId)!.State=="Revoked"&&f.B.State.Repository.Read(f.A.State.HostId)!.State=="Active");
    }
    public static async Task HeldResponseDisposalDrainsSend()
    {
        await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A,true);
        await f.Client(tracked).WithConnection(f.B.State.HostId,f.B.Address,async(held,ct)=>
        {
            var revoked=Revoke(f.A,f.B);var sending=held.Send(revoked);await tracked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!sending.IsCompleted&&f.A.State.Repository.Read(f.B.State.HostId)!.State=="Revoked"&&f.B.State.Repository.Read(f.A.State.HostId)!.State=="Revoked");
            var closing=Enumerable.Range(0,8).Select(_=>held.DisposeAsync().AsTask()).ToArray();await Task.WhenAll(closing).WaitAsync(TimeSpan.FromSeconds(5));
            Check(await sending==PeerUnpairDelivery.Unconfirmed);return true;
        });
        Check(tracked.Disposed==1&&tracked.Observed==1);
    }
    public static async Task PreparationRequiresActiveAndActualProof()
    {
        await using var f=new Pair();await f.Start(false);var entered=false;
        try{await f.Client().WithConnection(f.B.State.HostId,f.B.Address,(held,ct)=>{entered=true;return Task.FromResult(true);});throw new Exception("PeerBound preparation accepted.");}
        catch(Exception e)when(e is Grpc.Core.RpcException or AuthenticationException){}
        Check(!entered&&f.A.State.Repository.Read(f.B.State.HostId)!.State=="PeerBound"&&f.B.State.Count("PeerUnpairReceipts")==0);
        await WindowsHostComposition.CreatePeerActivationClient(f.A.Runtime,f.A.Certificate.Value).FinalizeAsync(f.B.State.HostId,f.B.Address);
        var unproven=new UnprovenTransport(f.B.Pin);
        try{await f.Client(unproven).WithConnection(f.B.State.HostId,f.B.Address,(held,ct)=>{entered=true;return Task.FromResult(true);});throw new Exception("Unproven connection accepted.");}
        catch(AuthenticationException){}
        Check(!entered&&unproven.Admitted);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        try{await f.Client().WithConnection(f.B.State.HostId,f.B.Address,(held,ct)=>{entered=true;return Task.FromResult(true);},cancel.Token);throw new Exception("Canceled preparation accepted.");}
        catch(OperationCanceledException){}Check(!entered);
    }
}
