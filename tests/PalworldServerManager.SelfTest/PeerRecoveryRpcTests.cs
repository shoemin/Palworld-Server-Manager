using System.Security.Authentication;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.PeerSecurityRpcTests;
using Result=PalworldServerManager.Contracts.Wire.PeerRecoveryCompletionResult;

namespace PalworldServerManager.SelfTest;

internal static class PeerRecoveryRpcTests
{
    private static void Check(bool value){if(!value)throw new Exception("Recovery RPC assertion failed.");}
    private static async Task Reject<T>(Func<Task> action) where T:Exception
    {try{await action();}catch(T){return;}throw new Exception("Expected recovery refusal: "+typeof(T).Name);}
    private static async Task Refused<T>(Task<T> task,StatusCode code)
    {
        try{await task;}catch(RpcException e)when(e.StatusCode==code)
        {Check(!e.Status.Detail.Contains("private-fault-marker",StringComparison.Ordinal));return;}
        throw new Exception("Expected bounded recovery RPC refusal: "+code);
    }
    private static async Task TlsRefused(Task<PeerHello> task)
    {
        try{await task;}catch(RpcException e)when(e.StatusCode is StatusCode.Internal or StatusCode.Unavailable)
        {
            for(Exception? cause=e.InnerException;cause is not null;cause=cause.InnerException)
                if(cause is AuthenticationException or IOException)return;
            throw;
        }
        throw new Exception("Expected actual TLS refusal.");
    }
    private sealed class Pair:IAsyncDisposable
    {
        internal readonly Fixture A=new(),B=new();
        internal readonly Guid Approval=Guid.NewGuid();
        // Prior independently verified bindings, then actual TLS activation. This fixture
        // is not evidence of a full fresh PAKE and two local Owner approval ceremonies.
        internal Pair(){A.Bind(B);B.Bind(A);}
        internal async Task Start(bool active=true,bool recovery=true)
        {
            await B.Start();
            if(active)await WindowsHostComposition.CreatePeerActivationClient(A.Runtime,A.Certificate.Value).FinalizeAsync(B.State.HostId,B.Address);
            if(recovery)
            {
                A.State.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{B.State.HostId:D}';");
                B.State.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{A.State.HostId:D}';");
            }
        }
        internal PeerRecoveryCompletionRequest Request=>new(){ReceivingHostId=B.State.HostId.ToString("D"),ApprovalId=Approval.ToString("D"),AcknowledgedFingerprint=B.Pin};
        internal Task<PeerHello> Negotiate(RawClient client,PeerHello? hello=null)=>client.Rpc.NegotiateRecoveryAsync(
            hello??PeerSecurityRpcRuntime.RecoveryHello(A.State.HostId),deadline:DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
        internal Task<PeerRecoveryCompletionReply> Send(RawClient client,PeerRecoveryCompletionRequest? request=null)=>client.Rpc.ReceiveRecoveryCompletionAsync(
            request??Request,deadline:DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
        internal string Snapshot()
        {
            var rows=new List<string>();
            foreach(var table in new[]{"HostIdentity","SecureCredentialReferences","LocalPrincipals","TrustedManagers","PeerRelationshipIncarnations",
                "TrustedManagerPairings","PendingCredentialReplacements","PeerReplacementBindingEvidence","PeerLocalBindingEvidence",
                "PeerReplacementCompletions","PeerRecoveryCompletionReceipts","PeerUnpairReceipts","HostCredentialRotations",
                "HostCapabilityGrants","ServerCapabilityGrants","AuthorizationRevision","DefaultGrantTemplateState","HostDefaultGrants","ServerDefaultGrants","AuditEvents"})
            {
                using var command=B.State.Writer.CreateCommand();command.CommandText=$"SELECT * FROM {table};";using var reader=command.ExecuteReader();
                while(reader.Read())rows.Add(table+":"+string.Join("|",Enumerable.Range(0,reader.FieldCount).Select(i=>reader.IsDBNull(i)?"NULL":reader.GetValue(i).ToString())));
            }
            return string.Join("\n",rows.Order(StringComparer.Ordinal));
        }
        public async ValueTask DisposeAsync(){try{await B.DisposeAsync();}finally{await A.DisposeAsync();}}
    }
    public static async Task WireAndImmutableHistory()
    {
        await ProtocolTests.SchemaEvolution();
        var a=Guid.NewGuid();var b=Guid.NewGuid();var request=new PeerRecoveryCompletionRequest{ReceivingHostId=b.ToString("D"),ApprovalId=Guid.NewGuid().ToString("D"),AcknowledgedFingerprint=new string('A',64)};
        Check(PeerRecoveryCompletionWire.ValidateRequest(PeerRecoveryCompletionRequest.Parser.ParseFrom(request.ToByteArray())).ReceivingHost==b);
        var reply=new PeerRecoveryCompletionReply{ReceivingHostId=request.ReceivingHostId,ApprovingHostId=a.ToString("D"),ApprovalId=request.ApprovalId,AcknowledgedFingerprint=request.AcknowledgedFingerprint,Result=Result.Recorded};
        void Bad(Action action){try{action();}catch(ArgumentException){return;}throw new Exception("Malformed recovery wire accepted.");}
        foreach(var result in new[]{Result.Recorded,Result.AlreadyRecorded,Result.KeyMismatch})
        {reply.Result=result;PeerRecoveryCompletionWire.ValidateReply(PeerRecoveryCompletionReply.Parser.ParseFrom(reply.ToByteArray()),request,a);}
        foreach(var result in new[]{0,-1,999}){reply.Result=(Result)result;Bad(()=>PeerRecoveryCompletionWire.ValidateReply(reply,request,a));}
        reply.Result=Result.Recorded;
        foreach(var value in new[]{"",Guid.Empty.ToString("D"),"bad",b.ToString("N")})
        {
            var bad=request.Clone();bad.ReceivingHostId=value;Bad(()=>PeerRecoveryCompletionWire.ValidateRequest(bad));
            bad=request.Clone();bad.ApprovalId=value;Bad(()=>PeerRecoveryCompletionWire.ValidateRequest(bad));
        }
        foreach(var value in new[]{"",new string('a',64),new string('G',64),new string('A',63),new string('A',65)})
        {var bad=request.Clone();bad.AcknowledgedFingerprint=value;Bad(()=>PeerRecoveryCompletionWire.ValidateRequest(bad));}
        foreach(var field in new[]{0,1,2,3})
        {
            var bad=reply.Clone();if(field==0)bad.ReceivingHostId=a.ToString("D");if(field==1)bad.ApprovingHostId=b.ToString("D");
            if(field==2)bad.ApprovalId=Guid.NewGuid().ToString("D");if(field==3)bad.AcknowledgedFingerprint=new string('B',64);
            Bad(()=>PeerRecoveryCompletionWire.ValidateReply(bad,request,a));
        }
        Bad(()=>PeerRecoveryCompletionWire.ValidateReply(reply,request,b));
        var hello=PeerSecurityRpcRuntime.RecoveryHello(a);Check(hello.Handshake.Protocol.Minor==13&&hello.Handshake.Capabilities.SequenceEqual(new[]{FeatureCapability.PeerRecoveryCompletion}));
        Check(!PeerSecurityRpcRuntime.Hello(a).Handshake.Capabilities.Contains(FeatureCapability.PeerRecoveryCompletion));
    }
    public static async Task BothRecoveryExactReceiptAndLostReplyDuplicate()
    {
        await using var f=new Pair();await f.Start();using var client=new RawClient(f.A,f.B);
        var snapshot=f.Snapshot();await f.Negotiate(client);Check(snapshot==f.Snapshot());
        var replies=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>f.Send(client)));
        foreach(var reply in replies)PeerRecoveryCompletionWire.ValidateReply(reply,f.Request,f.A.State.HostId);
        Check(replies.Count(r=>r.Result==Result.Recorded)==1&&replies.Count(r=>r.Result==Result.AlreadyRecorded)==7);
        Check(!f.B.State.Repository.Read(f.A.State.HostId)!.RecoveryRequired&&f.A.State.Repository.Read(f.B.State.HostId)!.RecoveryRequired);
        Check(f.B.State.Count("PeerRecoveryCompletionReceipts")==1&&f.B.State.Count("HostCapabilityGrants")==0&&f.B.State.Count("ServerCapabilityGrants")==0);
        Check(HostDatabase.QueryScalarLong(f.B.State.Writer,$"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerRecoveryCompletionReceived' AND ActorPeerHostId='{f.A.State.HostId:D}' AND ActorLocalPrincipalId IS NULL;")==1);
        // Ignore the recorded reply, reconnect and retry with the same approval.
        snapshot=f.Snapshot();using var fresh=new RawClient(f.A,f.B);await f.Negotiate(fresh);
        Check((await f.Send(fresh)).Result==Result.AlreadyRecorded&&f.Snapshot()==snapshot);
    }
    public static async Task RecoverySessionCannotBecomeOrdinary()
    {
        await using var f=new Pair();await f.Start();var snapshot=f.Snapshot();
        using(var ordinary=new RawClient(f.A,f.B))await Refused(ordinary.Negotiate(PeerSecurityRpcRuntime.Hello(f.A.State.HostId)),StatusCode.Unauthenticated);
        Check(snapshot==f.Snapshot());using var client=new RawClient(f.A,f.B);
        var all=PeerSecurityRpcRuntime.Hello(f.A.State.HostId);all.Handshake.Capabilities.Add(FeatureCapability.PeerRecoveryCompletion);
        var hello=await f.Negotiate(client,all);Check(hello.Handshake.Capabilities.SequenceEqual(new[]{FeatureCapability.PeerRecoveryCompletion}));
        async Task OrdinaryRefused()
        {
            await Refused(client.Activate(new()),StatusCode.FailedPrecondition);
            await Refused(client.Rpc.ReadRotationStatusAsync(new()).ResponseAsync,StatusCode.FailedPrecondition);
            await Refused(client.Rpc.StageRotationAsync(new()).ResponseAsync,StatusCode.FailedPrecondition);
            await Refused(client.Rpc.ConfirmRotationPromotionAsync(new()).ResponseAsync,StatusCode.FailedPrecondition);
            await Refused(client.Rpc.ConfirmCurrentCredentialAsync(new()).ResponseAsync,StatusCode.FailedPrecondition);
            await Refused(client.Rpc.ReceiveUnpairAsync(new(){ReceivingHostId=f.B.State.HostId.ToString("D")}).ResponseAsync,StatusCode.FailedPrecondition);
        }
        await OrdinaryRefused();Check(snapshot==f.Snapshot());await f.Send(client);snapshot=f.Snapshot();
        await OrdinaryRefused();await Refused(client.Negotiate(all),StatusCode.FailedPrecondition);await Refused(f.Negotiate(client),StatusCode.FailedPrecondition);
        Check(snapshot==f.Snapshot());
        using var normal=new RawClient(f.A,f.B);await normal.Negotiate(PeerSecurityRpcRuntime.Hello(f.A.State.HostId));
        await Refused(f.Send(normal),StatusCode.FailedPrecondition);Check(snapshot==f.Snapshot());
    }
    public static async Task ProtocolBoundsAndRecipientRefusals()
    {
        await using var f=new Pair();await f.Start();var snapshot=f.Snapshot();
        using(var missing=new RawClient(f.A,f.B))await Refused(f.Send(missing),StatusCode.FailedPrecondition);
        for(var mode=0;mode<7;mode++)
        {
            using var client=new RawClient(f.A,f.B);var hello=PeerSecurityRpcRuntime.RecoveryHello(f.A.State.HostId);
            if(mode==0)hello.Handshake.Capabilities.Clear();if(mode==1)hello.Handshake.Protocol.Major=2;
            if(mode==2)hello.Host.HostId=f.B.State.HostId.ToString("D");if(mode==3)hello.Host.HostId=Guid.NewGuid().ToString("D");
            if(mode==4)hello.Handshake.ProductVersion=new string('X',257);if(mode==5)for(var i=0;i<65;i++)hello.Handshake.Capabilities.Add((FeatureCapability)999);
            if(mode==6)hello.Handshake.Protocol=null;
            await Refused(f.Negotiate(client,hello),mode<2?StatusCode.FailedPrecondition:mode<4?StatusCode.Unauthenticated:StatusCode.InvalidArgument);
            await Refused(f.Negotiate(client),StatusCode.FailedPrecondition);
        }
        using(var large=new RawClient(f.A,f.B,unboundedSend:true))
        {var hello=PeerSecurityRpcRuntime.RecoveryHello(f.A.State.HostId);hello.Handshake.ProductVersion=new string('X',20000);await Refused(f.Negotiate(large,hello),StatusCode.ResourceExhausted);}
        using var good=new RawClient(f.A,f.B);var backport=PeerSecurityRpcRuntime.RecoveryHello(f.A.State.HostId);backport.Handshake.Protocol.Minor=1;backport.Handshake.Capabilities.Add((FeatureCapability)999);
        var reply=await f.Negotiate(good,backport);Check(reply.Handshake.Protocol.Minor==1&&reply.Handshake.Capabilities.Count==1);
        var bad=f.Request;bad.ReceivingHostId=f.A.State.HostId.ToString("D");await Refused(f.Send(good,bad),StatusCode.Unauthenticated);
        bad=f.Request;bad.ApprovalId="bad";await Refused(f.Send(good,bad),StatusCode.InvalidArgument);
        bad=f.Request;bad.AcknowledgedFingerprint="bad";await Refused(f.Send(good,bad),StatusCode.InvalidArgument);
        Check(snapshot==f.Snapshot());bad=f.Request;bad.AcknowledgedFingerprint=new string('F',64);
        var mismatch=await f.Send(good,bad);PeerRecoveryCompletionWire.ValidateReply(mismatch,bad,f.A.State.HostId);
        Check(mismatch.Result==Result.KeyMismatch&&f.B.State.Repository.Read(f.A.State.HostId)!.RecoveryRequired&&f.B.State.Count("PeerRecoveryCompletionReceipts")==0);
        Check((await f.Send(good)).Result==Result.Recorded);
    }
    public static async Task UnknownRevokedAndBoundRecoveryKeysFailTls()
    {
        await using(var bound=new Pair())
        {await bound.Start(active:false);var snapshot=bound.Snapshot();using var client=new RawClient(bound.A,bound.B);await TlsRefused(bound.Negotiate(client));Check(snapshot==bound.Snapshot());}
        await using var f=new Pair();await f.Start();
        Check(!f.B.State.Repository.RecognizesTransportFingerprint(f.A.Pin)&&f.B.State.Repository.RecognizesActiveRecoveryFingerprint(f.A.Pin));
        using(var unknownCertificate=new PeerTlsTests.Certificate())
        using(var unknown=new RawClient(f.A,f.B,certificate:unknownCertificate.Value))await TlsRefused(f.Negotiate(unknown));
        f.B.State.Execute($"UPDATE TrustedManagers SET State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL,PendingTrustedPublicKeyFingerprint=NULL,PendingRotationId=NULL,PendingRotationExpiresUtc=NULL,PendingReconfirmationRequired=0,PeerRecoveryRequired=0 WHERE PeerHostId='{f.A.State.HostId:D}';");
        Check(!f.B.State.Repository.RecognizesActiveRecoveryFingerprint(f.A.Pin));var snapshot2=f.Snapshot();
        using var revoked=new RawClient(f.A,f.B);await TlsRefused(f.Negotiate(revoked));Check(snapshot2==f.Snapshot());
    }
    public static async Task HeldKeysAndIncarnationCannotOutliveState()
    {
        for(var mode=0;mode<4;mode++)
        {
            await using var f=new Pair();await f.Start();using var client=new RawClient(f.A,f.B);await f.Negotiate(client);
            if(mode==0)f.B.State.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{new string('F',64)}' WHERE CredentialRef='current';");
            if(mode==1)f.B.State.Execute($"UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{new string('E',64)}' WHERE PeerHostId='{f.A.State.HostId:D}';");
            if(mode==2)f.B.State.Execute($"UPDATE TrustedManagers SET State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL,PendingTrustedPublicKeyFingerprint=NULL,PendingRotationId=NULL,PendingRotationExpiresUtc=NULL,PendingReconfirmationRequired=0,PeerRecoveryRequired=0 WHERE PeerHostId='{f.A.State.HostId:D}';");
            if(mode==3)f.B.State.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=0 WHERE PeerHostId='{f.A.State.HostId:D}'; UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{f.A.State.HostId:D}';");
            var snapshot=f.Snapshot();await Refused(f.Send(client),StatusCode.Unauthenticated);Check(snapshot==f.Snapshot());
        }
        await using var changed=new Pair();await changed.Start();
        changed.B.State.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{new string('F',64)}' WHERE CredentialRef='current';");
        using var oldListener=new RawClient(changed.A,changed.B);var before=changed.Snapshot();
        await Refused(changed.Negotiate(oldListener),StatusCode.Unauthenticated);Check(before==changed.Snapshot());
    }
    public static async Task OldAndPendingKeyDoNotPromoteDuringRecovery()
    {
        foreach(var usePending in new[]{false,true})
        {
            await using var f=new Pair();await f.Start();using var pending=new PeerTlsTests.Certificate();
            var pin=WindowsPeerTls.PublicFingerprint(pending.Value);var rotation=Guid.NewGuid();
            f.B.State.Execute($"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{pin}',PendingRotationId='{rotation:D}',PendingRotationExpiresUtc='{f.B.State.Time.Now.AddHours(-1):O}',PendingReconfirmationRequired=1 WHERE PeerHostId='{f.A.State.HostId:D}';");
            var before=f.B.State.Repository.Read(f.A.State.HostId)!;var snapshot=f.Snapshot();
            using var client=new RawClient(f.A,f.B,certificate:usePending?pending.Value:f.A.Certificate.Value);
            await f.Negotiate(client);Check(snapshot==f.Snapshot());await f.Send(client);
            Check(f.B.State.Repository.Read(f.A.State.HostId)==before with{RecoveryRequired=false});
            using var ordinary=new RawClient(f.A,f.B,certificate:pending.Value);
            await ordinary.Negotiate(PeerSecurityRpcRuntime.Hello(f.A.State.HostId));
            Check(f.B.State.Repository.Read(f.A.State.HostId)!.CurrentFingerprint==pin);
            snapshot=f.Snapshot();
            if(usePending)Check((await f.Send(client)).Result==Result.AlreadyRecorded);
            else await Refused(f.Send(client),StatusCode.Unauthenticated);
            // Routine promotion preserves incarnation; the exact receipt may remain a
            // read-only duplicate for New. Original pre-clear proof cannot write anew.
            var another=f.Request;another.ApprovalId=Guid.NewGuid().ToString("D");
            await Refused(f.Send(client,another),StatusCode.Unauthenticated);Check(snapshot==f.Snapshot());
        }
    }
    public static async Task AuditFaultRollsBackAndErrorsAreBounded()
    {
        await using var f=new Pair();await f.Start();using var client=new RawClient(f.A,f.B);await f.Negotiate(client);
        f.B.State.Execute("CREATE TRIGGER RecoveryRpcFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerRecoveryCompletionReceived' BEGIN SELECT RAISE(ABORT,'private-fault-marker'); END;");
        var snapshot=f.Snapshot();await Refused(f.Send(client),StatusCode.Internal);Check(snapshot==f.Snapshot());
        f.B.State.Execute("DROP TRIGGER RecoveryRpcFault;");
        f.B.State.Execute("CREATE TRIGGER RecoveryRpcFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerRecoveryCompletionReceived' BEGIN DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId; END;");
        snapshot=f.Snapshot();await Refused(f.Send(client),StatusCode.FailedPrecondition);Check(snapshot==f.Snapshot());
        f.B.State.Execute("DROP TRIGGER RecoveryRpcFault;");Check((await f.Send(client)).Result==Result.Recorded);
    }
    public static async Task OwnedAdapterCancellationAndLifetime()
    {
        await using var f=new Pair();await f.Start();var snapshot=f.Snapshot();
        DefaultHttpContext Context(PeerSecurityRpcRuntime? runtime=null)
        {var c=new DefaultHttpContext();c.Request.Scheme="https";c.Request.Protocol="HTTP/2";c.Features.Set(new PeerSecurityRpcConnection(runtime??f.B.Runtime,f.B.Pin,f.A.Pin));return c;}
        Task Negotiate(DefaultHttpContext context,CancellationToken ct=default)=>f.B.Runtime.Recovery.Negotiate(context,PeerSecurityRpcRuntime.RecoveryHello(f.A.State.HostId),ct);
        var foreign=Context(f.A.Runtime);await Reject<AuthenticationException>(()=>Negotiate(foreign));
        await Reject<AuthenticationException>(()=>f.B.Runtime.Recovery.Receive(foreign,f.Request,default));
        var insecure=Context();insecure.Request.Scheme="http";await Reject<AuthenticationException>(()=>Negotiate(insecure));
        insecure=Context();insecure.Request.Protocol="HTTP/1.1";await Reject<AuthenticationException>(()=>Negotiate(insecure));
        var missing=Context();missing.Features.Set<PeerSecurityRpcConnection>(null);await Reject<AuthenticationException>(()=>Negotiate(missing));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Reject<OperationCanceledException>(()=>Negotiate(Context(),cancel.Token));
        var aborted=Context();aborted.RequestAborted=cancel.Token;await Reject<OperationCanceledException>(()=>Negotiate(aborted));
        var closed=Context();await closed.Features.Get<PeerSecurityRpcConnection>()!.DisposeAsync();await Reject<ObjectDisposedException>(()=>Negotiate(closed));
        var live=Context();await Negotiate(live);await Reject<InvalidOperationException>(()=>f.B.Runtime.Permissions.Invoke(live,_=>true,default));await Reject<OperationCanceledException>(()=>f.B.Runtime.Recovery.Receive(live,f.Request,cancel.Token));
        await live.Features.Get<PeerSecurityRpcConnection>()!.DisposeAsync();await Reject<ObjectDisposedException>(()=>f.B.Runtime.Recovery.Receive(live,f.Request,default));
        Check(snapshot==f.Snapshot());
        var restricted=Context();await Negotiate(restricted);await f.B.Runtime.Recovery.Receive(restricted,f.Request,default);
        snapshot=f.Snapshot();await Reject<InvalidOperationException>(()=>f.B.Runtime.Permissions.Invoke(restricted,_=>true,default));
        Check(snapshot==f.Snapshot());await restricted.Features.Get<PeerSecurityRpcConnection>()!.DisposeAsync();
    }
}
