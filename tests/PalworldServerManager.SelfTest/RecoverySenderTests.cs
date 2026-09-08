using System.Buffers.Binary;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.PeerSecurityRpcTests;
using HostCapability=PalworldServerManager.Core.Authorization.HostCapability;
using ActorRef=PalworldServerManager.Core.Authorization.ActorRef;
using WireResult=PalworldServerManager.Contracts.Wire.PeerRecoveryCompletionResult;

namespace PalworldServerManager.SelfTest;

internal static class RecoverySenderTests
{
    private static void Check(bool value){if(!value)throw new Exception("Recovery sender assertion failed.");}
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<Exception> Failed(Task task)
    {
        try{await task;}
        catch(Exception e)when(e is RpcException or AuthenticationException or OperationCanceledException or ArgumentException or InvalidOperationException or PalworldServerManager.Contracts.ProtocolCompatibilityException){return e;}
        throw new Exception("Expected recovery sender refusal.");
    }
    private static GrantPolicyRepository Grants(Fixture f)=>new(f.State.Database,f.State.HostId,f.State.Time);
    private static LocalPrincipalMutationActor Owner(Fixture f)=>new(f.State.HostId,f.State.OwnerId,"native-owner","fixture-public");
    private static long Incarnation(Fixture local,Fixture peer)=>HostDatabase.QueryScalarLong(local.State.Writer,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{peer.State.HostId:D}';");
    private static long CountEvent(Fixture f,string name)=>HostDatabase.QueryScalarLong(f.State.Writer,$"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='{name}';");
    private static bool Confirmed(Fixture f)=>HostDatabase.QueryScalarLong(f.State.Writer,"SELECT COUNT(*) FROM PeerReplacementCompletions WHERE ConfirmedUtc IS NOT NULL;")==1;
    private static Guid Approve(Fixture local,Fixture peer,string? peerPin=null)
    {
        var proposal=local.State.Repository.RecordOwnerVerifiedBinding(Owner(local),peer.State.HostId,peerPin??peer.Pin,local.Pin).ReplacementId!.Value;
        var repo=Grants(local);repo.ApprovePeerReplacement(Owner(local),repo.Read().Revision,proposal);return proposal;
    }
    private static void Recover(Fixture f,string reference,string pin)
    {
        f.State.Execute($"INSERT INTO SecureCredentialReferences (CredentialRef,Purpose,CreatedUtc,PublicKeyFingerprint) VALUES ('{reference}','HostTlsV1','{f.State.Time.Now:O}','{pin}');");
        new HostCredentialStateRepository(f.State.Database,f.State.HostId).ReplaceOffline(reference,MachineCredentialRecoveryReason.CredentialLoss);
    }
    private sealed class Pair:IAsyncDisposable
    {
        internal readonly Fixture A=new(),B=new();
        internal readonly Guid AApproval,BApproval;
        internal Pair(bool approvals=true)
        {
            A.Bind(B);B.Bind(A);
            // Explicit prior independently verified binding/historical key fixture; local
            // Owner approval uses the canonical writer. Full PAKE ceremony is separate.
            foreach(var (local,peer,old) in new[]{(A,B,'E'),(B,A,'F')})
            {
                if(approvals)
                {
                    local.State.Execute($"UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{new string(old,64)}',PeerRecoveryRequired=1 WHERE PeerHostId='{peer.State.HostId:D}';");
                    var repo=Grants(local);repo.ConfigureDefaults(Owner(local),repo.Read().Revision,new([new(HostCapability.CreateServer,new(false,false))],[]));
                }
                else local.State.Execute("UPDATE TrustedManagers SET State='Active';");
            }
            if(approvals){AApproval=Approve(A,B);BApproval=Approve(B,A);}
        }
        internal async Task Start(){await A.Start();await B.Start();}
        internal PeerRecoveryCompletionRpcClient Sender(Fixture local,IPeerHttpTransportFactory? transport=null)=>transport is null?
            WindowsHostComposition.CreatePeerRecoveryCompletionClient(local.Runtime,local.Certificate.Value):new(local.Runtime,transport);
        public async ValueTask DisposeAsync(){try{await B.DisposeAsync();}finally{await A.DisposeAsync();}}
    }
    // Real native TLS underneath controlled Host-side observation/response faults.
    // These are not physical remote-host hang or network-MITM qualification.
    private sealed class TrackingFactory(X509Certificate2 certificate):IPeerHttpTransportFactory
    {
        internal int Observed,Disposed,ReceiptAttempts;
        internal Action<PeerTlsConnectionIdentity>? Observe;
        internal Func<HttpRequestMessage,HttpResponseMessage,CancellationToken,Task<HttpResponseMessage>>? After;
        public IPeerHttpTransport Create(Func<string,bool> accepts,Action<PeerTlsConnectionIdentity>? observed=null)
        {
            var native=new WindowsPeerHttpTransportFactory(certificate).Create(accepts,actual=>
            {Interlocked.Increment(ref Observed);Observe?.Invoke(actual);observed?.Invoke(actual);});
            return new Wrapped(this,native);
        }
        private sealed class Wrapped:IPeerHttpTransport
        {
            private readonly TrackingFactory owner;private readonly IPeerHttpTransport native;private int disposed;
            internal Wrapped(TrackingFactory owner,IPeerHttpTransport native){this.owner=owner;this.native=native;Handler=new Watch(owner){InnerHandler=native.Handler};}
            public HttpMessageHandler Handler{get;}
            public PeerTlsConnectionIdentity Identity=>native.Identity;
            public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0){try{Handler.Dispose();}finally{native.Dispose();Interlocked.Increment(ref owner.Disposed);}}}
        }
        private sealed class Watch(TrackingFactory owner):DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
            {
                if(IsReceipt(request))Interlocked.Increment(ref owner.ReceiptAttempts);
                var response=await base.SendAsync(request,ct).ConfigureAwait(false);
                try{return owner.After is {} after?await after(request,response,ct).ConfigureAwait(false):response;}
                catch{response.Dispose();throw;}
            }
        }
    }
    private static bool IsReceipt(HttpRequestMessage request)=>request.RequestUri!.AbsolutePath.EndsWith("/ReceiveRecoveryCompletion",StringComparison.Ordinal);
    private static async Task<T> Message<T>(HttpResponseMessage response,MessageParser<T> parser,CancellationToken ct)where T:IMessage<T>
    {
        var bytes=await response.Content.ReadAsByteArrayAsync(ct);Check(bytes.Length>=5&&bytes[0]==0&&BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(1,4))==bytes.Length-5);
        return parser.ParseFrom(bytes.AsSpan(5).ToArray());
    }
    private static HttpResponseMessage Rewrite(HttpResponseMessage response,IMessage message)
    {
        var bytes=message.ToByteArray();var frame=new byte[bytes.Length+5];BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1,4),bytes.Length);bytes.CopyTo(frame,5);
        response.Content.Dispose();response.Content=new ByteArrayContent(frame);response.Content.Headers.ContentType=new("application/grpc");return response;
    }
    public static async Task ActualMutualCompletionAndFreshAuthority()
    {
        await using var f=new Pair();await f.Start();var a=Grants(f.A);var b=Grants(f.B);
        Check(!a.Read().Policy.CanUseHost(ActorRef.RemoteManager(f.B.State.HostId),HostCapability.CreateServer,f.A.State.HostId));
        var tracked=new TrackingFactory(f.A.Certificate.Value);
        Check(await f.Sender(f.A,tracked).ConfirmAsync(f.B.State.HostId,f.B.Address)==PeerRecoveryCompletionExchange.Confirmed);
        Check(tracked.Observed==1&&tracked.Disposed==1&&tracked.ReceiptAttempts==1&&Confirmed(f.A)&&!Confirmed(f.B));
        Check(f.A.State.Repository.Read(f.B.State.HostId)!.RecoveryRequired&&!f.B.State.Repository.Read(f.A.State.HostId)!.RecoveryRequired);
        Check(await f.Sender(f.B).ConfirmAsync(f.A.State.HostId,f.A.Address)==PeerRecoveryCompletionExchange.Confirmed);
        Check(Confirmed(f.B)&&!f.A.State.Repository.Read(f.B.State.HostId)!.RecoveryRequired);
        Check(a.Read().Policy.CanUseHost(ActorRef.RemoteManager(f.B.State.HostId),HostCapability.CreateServer,f.A.State.HostId)&&b.Read().Policy.CanUseHost(ActorRef.RemoteManager(f.A.State.HostId),HostCapability.CreateServer,f.B.State.HostId));
        foreach(var local in new[]{f.A,f.B})Check(local.State.Count("HostCapabilityGrants")==1&&CountEvent(local,"PeerRecoveryCompletionReceived")==1&&CountEvent(local,"PeerRecoveryCompletionConfirmed")==1);
        Check(await f.Sender(f.A).ConfirmAsync(f.B.State.HostId,f.B.Address)==PeerRecoveryCompletionExchange.NoPending&&CountEvent(f.A,"PeerRecoveryCompletionConfirmed")==1);
    }
    public static async Task ActualOwnRecoveryUsesCurrentKeyAndHistoricalApproval()
    {
        await using var f=new Pair();await f.Start();using var next=new PeerTlsTests.Certificate();var pin=WindowsPeerTls.PublicFingerprint(next.Value);
        Recover(f.A,"sender-own-recovery",pin);await f.A.Stop();await f.A.Start(next.Value);Approve(f.B,f.A,pin);
        var sender=new PeerRecoveryCompletionRpcClient(f.A.Runtime,new WindowsPeerHttpTransportFactory(next.Value));
        Check(await sender.ConfirmAsync(f.B.State.HostId,f.B.Address)==PeerRecoveryCompletionExchange.Confirmed);
        Check(HostDatabase.QueryScalarLong(f.A.State.Writer,$"SELECT COUNT(*) FROM PeerReplacementCompletions WHERE ReplacementId='{f.AApproval:D}' AND LocalFingerprintAtApproval='{f.A.Pin}' AND ConfirmedUtc IS NOT NULL;")==1);
        Check(await f.Sender(f.B).ConfirmAsync(f.A.State.HostId,f.A.Address)==PeerRecoveryCompletionExchange.Confirmed);
        Check(!f.A.State.Repository.Read(f.B.State.HostId)!.RecoveryRequired&&!f.B.State.Repository.Read(f.A.State.HostId)!.RecoveryRequired);
        Check(f.B.State.Count("HostCapabilityGrants")==2&&Grants(f.B).Read().HostGrants.Count(g=>g.InvalidatedUtc is not null)==1);
    }
    public static async Task UnapprovedNewOwnKeyAndOldLocalKeyRefuse()
    {
        await using var f=new Pair();await f.Start();using var next=new PeerTlsTests.Certificate();Recover(f.A,"sender-unapproved",WindowsPeerTls.PublicFingerprint(next.Value));
        var failure=await Failed(new PeerRecoveryCompletionRpcClient(f.A.Runtime,new WindowsPeerHttpTransportFactory(next.Value)).ConfirmAsync(f.B.State.HostId,f.B.Address));
        var actualTls=false;for(Exception? cause=failure;cause is not null;cause=cause.InnerException)if(cause is AuthenticationException or IOException)actualTls=true;
        Check(actualTls);
        await Failed(f.Sender(f.A).ConfirmAsync(f.B.State.HostId,f.B.Address));
        Check(!Confirmed(f.A)&&f.B.State.Repository.Read(f.A.State.HostId)!.RecoveryRequired&&CountEvent(f.B,"PeerRecoveryCompletionReceived")==0);
    }
    public static async Task LostReplyRetriesExactDurableApproval()
    {
        await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);var entered=Signal();using var cancel=new CancellationTokenSource();
        tracked.After=async(request,response,ct)=>{if(IsReceipt(request)){entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);}return response;};
        var call=f.Sender(f.A,tracked).ConfirmAsync(f.B.State.HostId,f.B.Address,cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();await Failed(call);
        Check(!Confirmed(f.A)&&tracked.Disposed==1&&CountEvent(f.B,"PeerRecoveryCompletionReceived")==1);
        Check(await f.Sender(f.A).ConfirmAsync(f.B.State.HostId,f.B.Address)==PeerRecoveryCompletionExchange.Confirmed);
        Check(Confirmed(f.A)&&CountEvent(f.B,"PeerRecoveryCompletionReceived")==1&&CountEvent(f.A,"PeerRecoveryCompletionConfirmed")==1);
    }
    public static async Task ActualSimultaneousCompletionRequiresFreshIncarnation()
    {
        await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);var entered=Signal();var release=Signal();
        tracked.After=async(request,response,ct)=>{if(IsReceipt(request)){entered.TrySetResult();await release.Task.WaitAsync(ct);}return response;};
        var prior=Incarnation(f.A,f.B);var call=f.Sender(f.A,tracked).ConfirmAsync(f.B.State.HostId,f.B.Address);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(await f.Sender(f.B).ConfirmAsync(f.A.State.HostId,f.A.Address)==PeerRecoveryCompletionExchange.Confirmed);
            Check(Incarnation(f.A,f.B)>prior&&!Confirmed(f.A));release.TrySetResult();
            Check(await Failed(call) is AuthenticationException);
        }
        finally{release.TrySetResult();try{await call;}catch{}}
        Check(CountEvent(f.A,"PeerRecoveryCompletionConfirmed")==0&&tracked.Disposed==1);
        Check(await f.Sender(f.A).ConfirmAsync(f.B.State.HostId,f.B.Address)==PeerRecoveryCompletionExchange.Confirmed);
        foreach(var local in new[]{f.A,f.B})Check(Confirmed(local)&&CountEvent(local,"PeerRecoveryCompletionReceived")==1&&CountEvent(local,"PeerRecoveryCompletionConfirmed")==1);
    }
    public static async Task ChangedLocalAuthorityNeverConfirms()
    {
        foreach(var afterReceipt in new[]{false,true})
        {
            await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);var changed=false;
            tracked.After=(request,response,ct)=>
            {
                if(!changed&&IsReceipt(request)==afterReceipt){changed=true;LiveUnpairConnectionTests.Revoke(f.A,f.B);}return Task.FromResult(response);
            };
            Check(await Failed(f.Sender(f.A,tracked).ConfirmAsync(f.B.State.HostId,f.B.Address)) is AuthenticationException);
            Check(changed&&!Confirmed(f.A)&&f.A.State.Repository.Read(f.B.State.HostId)!.State=="Revoked"&&tracked.Disposed==1);
            Check(tracked.ReceiptAttempts==(afterReceipt?1:0)&&CountEvent(f.A,"PeerRecoveryCompletionConfirmed")==0);
        }
        await using var own=new Pair();await own.Start();using var next=new PeerTlsTests.Certificate();var watch=new TrackingFactory(own.A.Certificate.Value);
        watch.After=(request,response,ct)=>{if(IsReceipt(request))Recover(own.A,"sender-late-key",WindowsPeerTls.PublicFingerprint(next.Value));return Task.FromResult(response);};
        Check(await Failed(own.Sender(own.A,watch).ConfirmAsync(own.B.State.HostId,own.B.Address)) is AuthenticationException);
        Check(!Confirmed(own.A)&&watch.Disposed==1&&own.A.State.Repository.Read(own.B.State.HostId)!.RecoveryRequired);
    }
    public static async Task MalformedAndMismatchRepliesNeverConfirm()
    {
        await using var f=new Pair();await f.Start();
        for(var mode=0;mode<7;mode++)
        {
            var tracked=new TrackingFactory(f.A.Certificate.Value);var selected=mode;
            tracked.After=async(request,response,ct)=>
            {
                if(!IsReceipt(request))return response;
                var reply=await Message(response,PeerRecoveryCompletionReply.Parser,ct);
                if(selected==0)reply.ReceivingHostId=f.A.State.HostId.ToString("D");if(selected==1)reply.ApprovingHostId=f.B.State.HostId.ToString("D");
                if(selected==2)reply.ApprovalId=Guid.NewGuid().ToString("D");if(selected==3)reply.AcknowledgedFingerprint=new string('F',64);
                if(selected==4)reply.Result=(WireResult)999;if(selected==5)reply.Result=WireResult.Unspecified;if(selected==6)reply.Result=WireResult.KeyMismatch;
                return Rewrite(response,reply);
            };
            var call=f.Sender(f.A,tracked).ConfirmAsync(f.B.State.HostId,f.B.Address);
            if(mode==6)Check(await call==PeerRecoveryCompletionExchange.KeyMismatch);else Check(await Failed(call) is ArgumentException);
            Check(!Confirmed(f.A)&&tracked.Disposed==1&&CountEvent(f.A,"PeerRecoveryCompletionConfirmed")==0);
        }
        Check(await f.Sender(f.A).ConfirmAsync(f.B.State.HostId,f.B.Address)==PeerRecoveryCompletionExchange.Confirmed&&CountEvent(f.B,"PeerRecoveryCompletionReceived")==1);
    }
    public static async Task NegotiationAndNoPendingAreBounded()
    {
        await using var f=new Pair();await f.Start();
        for(var mode=0;mode<5;mode++)
        {
            var tracked=new TrackingFactory(f.A.Certificate.Value);var selected=mode;
            tracked.After=async(request,response,ct)=>
            {
                var reply=await Message(response,PeerHello.Parser,ct);
                if(selected==0)reply.Host.HostId=f.A.State.HostId.ToString("D");if(selected==1)reply.Handshake.Capabilities.Clear();
                if(selected==2)reply.Handshake.Protocol.Major=2;if(selected==3)reply.Handshake.ProductVersion=new string('X',257);
                if(selected==4)for(var i=0;i<65;i++)reply.Handshake.Capabilities.Add((FeatureCapability)999);
                return Rewrite(response,reply);
            };
            await Failed(f.Sender(f.A,tracked).ConfirmAsync(f.B.State.HostId,f.B.Address));Check(tracked.ReceiptAttempts==0&&tracked.Disposed==1&&!Confirmed(f.A));
        }
        await using var empty=new Pair(approvals:false);await empty.Start();var watch=new TrackingFactory(empty.A.Certificate.Value);
        Check(await empty.Sender(empty.A,watch).ConfirmAsync(empty.B.State.HostId,empty.B.Address)==PeerRecoveryCompletionExchange.NoPending&&watch.ReceiptAttempts==0&&watch.Disposed==1);
        Check(empty.A.State.Count("PeerReplacementCompletions")==0&&empty.B.State.Count("PeerRecoveryCompletionReceipts")==0);
        foreach(var address in new[]{new Uri("http://localhost"),new Uri("https://user@localhost/"),new Uri("https://localhost/path"),new Uri("https://localhost/?query=1")})
            Check(await Failed(empty.Sender(empty.A).ConfirmAsync(empty.B.State.HostId,address)) is ArgumentException);
        using var canceled=new CancellationTokenSource();canceled.Cancel();Check(await Failed(empty.Sender(empty.A).ConfirmAsync(empty.B.State.HostId,empty.B.Address,canceled.Token)) is OperationCanceledException);
    }
    public static async Task CancellationDrainsActualNativeCallback()
    {
        await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);var entered=Signal();using var release=new ManualResetEventSlim();using var cancel=new CancellationTokenSource();
        tracked.Observe=_=>{entered.TrySetResult();release.Wait();};
        var call=Task.Run(()=>f.Sender(f.A,tracked).ConfirmAsync(f.B.State.HostId,f.B.Address,cancel.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();await Task.Delay(100);
            Check(!call.IsCompleted&&tracked.Disposed==0);release.Set();await Failed(call);
        }
        finally{release.Set();try{await call;}catch{}}
        Check(tracked.Observed==1&&tracked.Disposed==1&&tracked.ReceiptAttempts==0&&!Confirmed(f.A)&&CountEvent(f.B,"PeerRecoveryCompletionReceived")==0);
    }
    public static async Task ActualDeadlineDisposesHeldResponse()
    {
        await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A.Certificate.Value);var entered=Signal();
        tracked.After=async(request,response,ct)=>{if(IsReceipt(request)){entered.TrySetResult();await Task.Delay(Timeout.Infinite,ct);}return response;};
        var call=f.Sender(f.A,tracked).ConfirmAsync(f.B.State.HostId,f.B.Address);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Failed(call.WaitAsync(TimeSpan.FromSeconds(25))); // Production deadline, no caller cancellation.
        Check(tracked.Disposed==1&&!Confirmed(f.A)&&CountEvent(f.B,"PeerRecoveryCompletionReceived")==1);
    }
    public static async Task ConcurrentNativeSendersStayIdempotent()
    {
        await using var f=new Pair();await f.Start();var sender=f.Sender(f.A);
        var replies=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>sender.ConfirmAsync(f.B.State.HostId,f.B.Address)));
        Check(replies.Any(r=>r==PeerRecoveryCompletionExchange.Confirmed)&&replies.All(r=>r is PeerRecoveryCompletionExchange.Confirmed or PeerRecoveryCompletionExchange.NoPending));
        Check(Confirmed(f.A)&&CountEvent(f.A,"PeerRecoveryCompletionConfirmed")==1&&CountEvent(f.B,"PeerRecoveryCompletionReceived")==1);
    }
}
