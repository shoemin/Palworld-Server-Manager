using System.Security.Authentication;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using static PalworldServerManager.SelfTest.PeerSecurityRpcTests;

namespace PalworldServerManager.SelfTest;

internal static class PeerUnpairRpcTests
{
    private static void Check(bool condition) { if(!condition)throw new Exception("Unpair RPC assertion failed."); }
    private static async Task Reject<T>(Func<Task> action) where T:Exception
    {try{await action();}catch(T){return;}throw new Exception("Expected unpair refusal: "+typeof(T).Name);}
    private static async Task Refused(Task<PeerUnpairReply> action,StatusCode code)
    {
        try{await action;}catch(RpcException e)when(e.StatusCode==code)
        {Check(!e.Status.Detail.Contains("private-fault-marker",StringComparison.Ordinal));return;}
        throw new Exception("Expected bounded unpair RPC refusal: "+code);
    }
    private sealed class Pair:IAsyncDisposable
    {
        internal readonly Fixture A=new(),B=new();
        internal Pair(){A.Bind(B);B.Bind(A);}
        internal async Task Start(bool active=true)
        {
            await B.Start();
            if(active)await WindowsHostComposition.CreatePeerActivationClient(A.Runtime,A.Certificate.Value)
                .FinalizeAsync(B.State.HostId,B.Address);
        }
        public async ValueTask DisposeAsync(){try{await B.DisposeAsync();}finally{await A.DisposeAsync();}}
        internal PeerUnpairNotice Notice=>new(){ReceivingHostId=B.State.HostId.ToString("D")};
        internal Task<PeerUnpairReply> Send(RawClient client,PeerUnpairNotice? request=null)
            =>client.Rpc.ReceiveUnpairAsync(request??Notice,deadline:DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
        internal string Snapshot()
        {
            var rows=new List<string>();
            foreach(var table in new[]{"HostIdentity","SecureCredentialReferences","LocalPrincipals","TrustedManagers",
                "PeerRelationshipIncarnations","TrustedManagerPairings","PendingCredentialReplacements",
                "PeerUnpairReceipts","HostCapabilityGrants","ServerCapabilityGrants","AuthorizationRevision","AuditEvents"})
            {
                using var c=B.State.Writer.CreateCommand();c.CommandText=$"SELECT * FROM {table};";using var reader=c.ExecuteReader();
                while(reader.Read())rows.Add(table+":"+string.Join("|",Enumerable.Range(0,reader.FieldCount).Select(i=>reader.IsDBNull(i)?"NULL":reader.GetValue(i).ToString())));
            }
            return string.Join("\n",rows.Order(StringComparer.Ordinal));
        }
    }
    public static Task WireAndFeatureAreClosed()
    {
        var a=Guid.NewGuid();var b=Guid.NewGuid();var notice=new PeerUnpairNotice{ReceivingHostId=b.ToString("D")};
        Check(PeerUnpairWire.ReceivingHost(PeerUnpairNotice.Parser.ParseFrom(notice.ToByteArray()))==b);
        var reply=new PeerUnpairReply{ReceivingHostId=b.ToString("D"),UnpairedHostId=a.ToString("D"),Result=PeerUnpairResult.Recorded};
        PeerUnpairWire.ValidateReply(PeerUnpairReply.Parser.ParseFrom(reply.ToByteArray()),b,a);
        void Bad(Action action){try{action();}catch(ArgumentException){return;}throw new Exception("Malformed wire accepted.");}
        foreach(var text in new[]{"",Guid.Empty.ToString("D"),"bad",b.ToString("N")})Bad(()=>PeerUnpairWire.ReceivingHost(new(){ReceivingHostId=text}));
        foreach(var outcome in new[]{0,-1,999}){var bad=reply.Clone();bad.Result=(PeerUnpairResult)outcome;Bad(()=>PeerUnpairWire.ValidateReply(bad,b,a));}
        Bad(()=>PeerUnpairWire.ValidateReply(reply,a,b));Bad(()=>PeerUnpairWire.ValidateReply(reply,b,b));
        var wrong=reply.Clone();wrong.UnpairedHostId=Guid.NewGuid().ToString("D");Bad(()=>PeerUnpairWire.ValidateReply(wrong,b,a));
        wrong=reply.Clone();wrong.ReceivingHostId="bad";Bad(()=>PeerUnpairWire.ValidateReply(wrong,b,a));
        var local=PeerSecurityRpcRuntime.Hello(a);var remote=PeerSecurityRpcRuntime.Hello(b);
        Check(local.Handshake.Protocol.Minor==13&&NegotiatedProtocol.Negotiate(local.Handshake,remote.Handshake).Supports(FeatureCapability.PeerUnpair));
        remote.Handshake.Capabilities.Remove(FeatureCapability.PeerUnpair);
        Check(!NegotiatedProtocol.Negotiate(local.Handshake,remote.Handshake).Supports(FeatureCapability.PeerUnpair));
        return Task.CompletedTask;
    }
    public static async Task ActualReceiptDuplicatesAndStaleHandshake()
    {
        await using var f=new Pair();await f.Start();using var client=new RawClient(f.A,f.B);
        await client.Negotiate(PeerSecurityRpcRuntime.Hello(f.A.State.HostId));
        var ack=PeerSecurityRpcService.Wire(f.A.State.Repository.PrepareActivationAcknowledgement(f.B.State.HostId,f.B.Pin,f.A.Pin));
        var replies=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>f.Send(client)));
        foreach(var reply in replies)PeerUnpairWire.ValidateReply(reply,f.B.State.HostId,f.A.State.HostId);
        Check(replies.Count(r=>r.Result==PeerUnpairResult.Recorded)==1&&replies.Count(r=>r.Result==PeerUnpairResult.AlreadyRecorded)==7);
        Check(f.B.State.Repository.Read(f.A.State.HostId)!.State=="Revoked"&&f.A.State.Repository.Read(f.B.State.HostId)!.State=="Active");
        Check(f.B.State.Count("PeerUnpairReceipts")==1&&f.B.State.Count("HostCapabilityGrants")==0&&f.B.State.Count("ServerCapabilityGrants")==0);
        Check(HostDatabase.QueryScalarLong(f.B.State.Writer,$"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked' AND ActorPeerHostId='{f.A.State.HostId:D}' AND ActorLocalPrincipalId IS NULL;")==1);
        Check(HostDatabase.QueryScalarLong(f.B.State.Writer,"SELECT COUNT(*) FROM LocalPrincipals WHERE IsOwner=1 AND State='Active';")==1);
        var candidate=f.B.State.Repository.RecordVerifiedBinding(f.A.State.HostId,f.A.Pin,f.B.Pin);Check(candidate.Disposition==PeerBindingDisposition.ReplacementRequired);
        var snapshot=f.Snapshot();Check((await f.Send(client)).Result==PeerUnpairResult.AlreadyRecorded&&f.Snapshot()==snapshot);
        try{await client.Activate(ack);throw new Exception("Revoked activation request was accepted.");}
        catch(RpcException e)when(e.StatusCode==StatusCode.Unauthenticated){}
        using var fresh=new RawClient(f.A,f.B);
        try{await fresh.Negotiate(PeerSecurityRpcRuntime.Hello(f.A.State.HostId));throw new Exception("Revoked fresh TLS was accepted.");}
        catch(RpcException e)when(e.StatusCode is StatusCode.Internal or StatusCode.Unavailable)
        {
            var actualTls=false;for(Exception? cause=e.InnerException;cause is not null;cause=cause.InnerException)
                if(cause is AuthenticationException or IOException)actualTls=true;
            Check(actualTls);
        }
        Check(f.Snapshot()==snapshot);
    }
    public static async Task ProtocolRecipientAndNonActiveRefusals()
    {
        await using var f=new Pair();await f.Start(false);var snapshot=f.Snapshot();
        using(var missing=new RawClient(f.A,f.B))await Refused(f.Send(missing),StatusCode.FailedPrecondition);
        using(var legacy=new RawClient(f.A,f.B))
        {
            var hello=PeerSecurityRpcRuntime.Hello(f.A.State.HostId);hello.Handshake.Capabilities.Remove(FeatureCapability.PeerUnpair);
            await legacy.Negotiate(hello);await Refused(f.Send(legacy),StatusCode.FailedPrecondition);
        }
        using var client=new RawClient(f.A,f.B);await client.Negotiate(PeerSecurityRpcRuntime.Hello(f.A.State.HostId));
        await Refused(f.Send(client,new(){ReceivingHostId="bad"}),StatusCode.InvalidArgument);
        await Refused(f.Send(client,new(){ReceivingHostId=f.A.State.HostId.ToString("D")}),StatusCode.Unauthenticated);
        await Refused(f.Send(client),StatusCode.Unauthenticated);Check(f.Snapshot()==snapshot); // PeerBound
        await WindowsHostComposition.CreatePeerActivationClient(f.A.Runtime,f.A.Certificate.Value).FinalizeAsync(f.B.State.HostId,f.B.Address);
        f.B.State.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{f.A.State.HostId:D}';");
        snapshot=f.Snapshot();await Refused(f.Send(client),StatusCode.Unauthenticated);Check(f.Snapshot()==snapshot);
    }
    public static async Task ActualCurrentKeyAndLaterRelationshipRefuse()
    {
        await using var f=new Pair();await f.Start();using var client=new RawClient(f.A,f.B);
        await client.Negotiate(PeerSecurityRpcRuntime.Hello(f.A.State.HostId));await f.Send(client);
        f.B.State.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{new string('F',64)}' WHERE CredentialRef='current';");
        var snapshot=f.Snapshot();await Refused(f.Send(client),StatusCode.Unauthenticated);Check(f.Snapshot()==snapshot);
        f.B.State.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{f.B.Pin}' WHERE CredentialRef='current';");
        // Artificial restoration is negative ABA evidence, never positive Owner repair.
        f.B.State.Execute($"UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{f.A.Pin}' WHERE PeerHostId='{f.A.State.HostId:D}';");
        snapshot=f.Snapshot();await Refused(f.Send(client),StatusCode.Unauthenticated);Check(f.Snapshot()==snapshot&&f.B.State.Count("PeerUnpairReceipts")==0);
        using var fresh=new RawClient(f.A,f.B);await fresh.Negotiate(PeerSecurityRpcRuntime.Hello(f.A.State.HostId));
        Check((await f.Send(fresh)).Result==PeerUnpairResult.Recorded);
        snapshot=f.Snapshot();await Refused(f.Send(client),StatusCode.Unauthenticated);Check(f.Snapshot()==snapshot);
    }
    public static async Task ActualAuditFaultIsAtomicAndBounded()
    {
        await using var f=new Pair();await f.Start();using var client=new RawClient(f.A,f.B);
        await client.Negotiate(PeerSecurityRpcRuntime.Hello(f.A.State.HostId));
        f.B.State.Execute("CREATE TRIGGER UnpairFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerTrustRevoked' BEGIN SELECT RAISE(ABORT,'private-fault-marker'); END;");
        var snapshot=f.Snapshot();await Refused(f.Send(client),StatusCode.Internal);Check(f.Snapshot()==snapshot);
        f.B.State.Execute("DROP TRIGGER UnpairFault;");Check((await f.Send(client)).Result==PeerUnpairResult.Recorded);
    }
    public static async Task AdapterConnectionLifetimeAndRuntimeRefusals()
    {
        await using var f=new Pair();await f.Start();var snapshot=f.Snapshot();
        DefaultHttpContext Context(PeerSecurityRpcRuntime? owner=null)
        {
            var c=new DefaultHttpContext();c.Request.Scheme="https";c.Request.Protocol="HTTP/2";
            c.Features.Set(new PeerSecurityRpcConnection(owner??f.B.Runtime,f.B.Pin,f.A.Pin)
            {
                PeerId=f.A.State.HostId,PeerIncarnation=f.B.State.Repository.ReadAuthenticatedRelationshipIncarnation(f.A.State.HostId,f.A.Pin,f.B.Pin),
                Protocol=NegotiatedProtocol.Negotiate(PeerSecurityRpcRuntime.Hello(f.B.State.HostId).Handshake,PeerSecurityRpcRuntime.Hello(f.A.State.HostId).Handshake)
            });return c;
        }
        Task Invoke(DefaultHttpContext context,CancellationToken ct=default)=>f.B.Runtime.Unpair.Receive(context,f.Notice,ct);
        var foreign=Context(f.A.Runtime);await Reject<AuthenticationException>(()=>Invoke(foreign));
        var insecure=Context();insecure.Request.Scheme="http";await Reject<AuthenticationException>(()=>Invoke(insecure));
        insecure=Context();insecure.Request.Protocol="HTTP/1.1";await Reject<AuthenticationException>(()=>Invoke(insecure));
        var missing=Context();missing.Features.Set<PeerSecurityRpcConnection>(null);await Reject<AuthenticationException>(()=>Invoke(missing));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        var canceled=Context();await Reject<OperationCanceledException>(()=>Invoke(canceled,cancel.Token));
        canceled=Context();canceled.RequestAborted=cancel.Token;await Reject<OperationCanceledException>(()=>Invoke(canceled));
        var closed=Context();await closed.Features.Get<PeerSecurityRpcConnection>()!.DisposeAsync();await Reject<ObjectDisposedException>(()=>Invoke(closed));
        Check(f.Snapshot()==snapshot);
    }
}
