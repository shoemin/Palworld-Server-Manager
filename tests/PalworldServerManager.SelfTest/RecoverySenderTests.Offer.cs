using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.PeerSecurityRpcTests;
using PeerRecoveryCompletionResult=PalworldServerManager.Contracts.Wire.PeerRecoveryCompletionResult;

namespace PalworldServerManager.SelfTest;

internal static partial class RecoverySenderTests
{
    // Test-only caller using the real Windows native transport. Production orchestration follows separately.
    private sealed class OfferClient:IDisposable
    {
        private readonly IPeerHttpTransport transport;private readonly GrpcChannel channel;
        internal readonly PeerSecurityProtocol.PeerSecurityProtocolClient Rpc;
        internal PeerGrantMutationActor Proof=>proof??throw new Exception("No actual TLS proof.");
        private PeerGrantMutationActor? proof;
        internal OfferClient(Fixture local,Fixture peer,X509Certificate2? certificate=null)
        {
            transport=new WindowsPeerHttpTransportFactory(certificate??local.Certificate.Value).Create(pin=>pin==peer.Pin,actual=>
            {
                var inc=local.State.Repository.ReadRecoveryRelationshipIncarnation(peer.State.HostId,actual.PeerFingerprint,actual.LocalFingerprint);
                if(Interlocked.CompareExchange(ref proof,new(local.State.HostId,peer.State.HostId,actual.PeerFingerprint,actual.LocalFingerprint,inc),null) is not null)
                    throw new AuthenticationException("Fresh test connection required.");
            });
            channel=GrpcChannel.ForAddress(peer.Address,new GrpcChannelOptions {HttpHandler=transport.Handler,HttpVersion=HttpVersion.Version20,
                HttpVersionPolicy=HttpVersionPolicy.RequestVersionExact,MaxReceiveMessageSize=16384,MaxSendMessageSize=16384});
            Rpc=new(channel);
        }
        internal Task<PeerHello> Negotiate(Fixture local,PeerHello? hello=null)=>Rpc.NegotiateRecoveryAsync(hello??PeerSecurityRpcRuntime.RecoveryHello(local.State.HostId),deadline:DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
        internal Task<PeerRecoveryOfferReply> Read(Fixture peer)=>Rpc.ReadRecoveryCompletionOfferAsync(new(){OfferingHostId=peer.State.HostId.ToString("D")},deadline:DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
        internal Task<PeerRecoveryOfferConfirmationReply> Confirm(PeerRecoveryCompletionReply receipt)=>Rpc.ConfirmRecoveryCompletionOfferAsync(receipt,deadline:DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
        public void Dispose(){try{channel.Dispose();}finally{transport.Dispose();}}
    }
    private static PeerRecoveryCompletionReply OfferReceipt(Pair f)=>new(){ReceivingHostId=f.A.State.HostId.ToString("D"),ApprovingHostId=f.B.State.HostId.ToString("D"),
        ApprovalId=f.BApproval.ToString("D"),AcknowledgedFingerprint=f.A.Pin,Result=PeerRecoveryCompletionResult.Recorded};
    private static string OfferSnapshot(Fixture f)
    {
        var rows=new List<string>();
        foreach(var table in new[]{"HostIdentity","SecureCredentialReferences","LocalPrincipals","TrustedManagers","PeerRelationshipIncarnations",
            "TrustedManagerPairings","PendingCredentialReplacements","PeerReplacementBindingEvidence","PeerLocalBindingEvidence","PeerReplacementCompletions",
            "PeerRecoveryCompletionReceipts","PeerUnpairReceipts","HostCredentialRotations","HostCapabilityGrants","ServerCapabilityGrants","AuthorizationRevision",
            "DefaultGrantTemplateState","HostDefaultGrants","ServerDefaultGrants","AuditEvents"})
        {
            using var command=f.State.Writer.CreateCommand();command.CommandText=$"SELECT * FROM {table};";using var reader=command.ExecuteReader();
            while(reader.Read())rows.Add(table+":"+string.Join("|",Enumerable.Range(0,reader.FieldCount).Select(i=>reader.IsDBNull(i)?"NULL":reader.GetValue(i).ToString())));
        }
        return string.Join("\n",rows.Order(StringComparer.Ordinal));
    }
    private static async Task OfferRefused(Task task,StatusCode expected)
    {
        try{await task;}catch(RpcException e)when(e.StatusCode==expected)
        {Check(!e.Status.Detail.Contains("private-offer-fault",StringComparison.Ordinal));return;}
        throw new Exception("Expected offer refusal: "+expected);
    }
    public static Task OfferWireShapesAndHistory()
    {
        using var a=new PeerTlsTests.Certificate();var local=Guid.NewGuid();var peer=Guid.NewGuid();var pin=WindowsPeerTls.PublicFingerprint(a.Value);
        var ack=new PeerRecoveryCompletionRequest {ReceivingHostId=local.ToString("D"),ApprovalId=Guid.NewGuid().ToString("D"),AcknowledgedFingerprint=pin};
        var offer=new PeerRecoveryOfferReply {OfferingHostId=peer.ToString("D"),Acknowledgment=ack};
        void Bad(Action action){try{action();}catch(ArgumentException){return;}throw new Exception("Malformed offer accepted.");}
        PeerRecoveryOfferWire.ValidateOffer(PeerRecoveryOfferReply.Parser.ParseFrom(offer.ToByteArray()),peer,local);
        offer.Acknowledgment.AcknowledgedFingerprint=new string('F',64);PeerRecoveryOfferWire.ValidateOffer(offer,peer,local); // canonical mismatch audit remains reachable
        offer.Acknowledgment=ack.Clone();var receipt=new PeerRecoveryCompletionReply {ReceivingHostId=local.ToString("D"),ApprovingHostId=peer.ToString("D"),ApprovalId=ack.ApprovalId,AcknowledgedFingerprint=pin,Result=PeerRecoveryCompletionResult.Recorded};
        foreach(var invalid in new[]{"",Guid.Empty.ToString("D"),"bad",peer.ToString("N")})
        {Bad(()=>PeerRecoveryOfferWire.ValidateRequest(new(){OfferingHostId=invalid}));var bad=offer.Clone();bad.Acknowledgment.ApprovalId=invalid;Bad(()=>PeerRecoveryOfferWire.ValidateOffer(bad,peer,local));}
        Bad(()=>PeerRecoveryOfferWire.ValidateOffer(offer,local,peer));Bad(()=>PeerRecoveryOfferWire.ValidateOffer(offer,peer,peer));
        foreach(var value in new[]{0,-1,999,3}){receipt.Result=(PeerRecoveryCompletionResult)value;Bad(()=>PeerRecoveryOfferWire.ValidatePositiveReceipt(receipt,peer,local,pin));}
        receipt.Result=PeerRecoveryCompletionResult.AlreadyRecorded;PeerRecoveryOfferWire.ValidatePositiveReceipt(receipt,peer,local,pin);
        var confirmation=new PeerRecoveryOfferConfirmationReply {Receipt=receipt.Clone(),Result=PeerRecoveryOfferConfirmationResult.Confirmed};
        PeerRecoveryOfferWire.ValidateConfirmation(confirmation,receipt);
        foreach(var value in new[]{0,-1,999}){confirmation.Result=(PeerRecoveryOfferConfirmationResult)value;Bad(()=>PeerRecoveryOfferWire.ValidateConfirmation(confirmation,receipt));}
        confirmation.Result=PeerRecoveryOfferConfirmationResult.AlreadyConfirmed;confirmation.Receipt.ApprovalId=Guid.NewGuid().ToString("D");Bad(()=>PeerRecoveryOfferWire.ValidateConfirmation(confirmation,receipt));
        return ProtocolTests.SchemaEvolution();
    }
    public static async Task OfferActualReceiptFreshConfirmationAndMutualRecovery()
    {
        await using var f=new Pair();await f.Start();var receipt=OfferReceipt(f);var before=OfferSnapshot(f.B);
        using(var connection=new OfferClient(f.A,f.B))
        {
            await connection.Negotiate(f.A);var offer=await connection.Read(f.B);PeerRecoveryOfferWire.ValidateOffer(offer,f.B.State.HostId,f.A.State.HostId);
            Check(offer.Acknowledgment.ApprovalId==f.BApproval.ToString("D")&&offer.Acknowledgment.AcknowledgedFingerprint==f.A.Pin&&before==OfferSnapshot(f.B));
            var recorded=Grants(f.A).ReceiveAuthenticatedRecoveryCompletion(connection.Proof,f.BApproval,offer.Acknowledgment.AcknowledgedFingerprint);
            Check(recorded.Disposition==RecoveryCompletionDisposition.Recorded&&!f.A.State.Repository.Read(f.B.State.HostId)!.RecoveryRequired);
        }
        Check(f.B.State.Repository.Read(f.A.State.HostId)!.RecoveryRequired&&!Confirmed(f.B));
        using(var fresh=new OfferClient(f.A,f.B))
        {
            await fresh.Negotiate(f.A);var result=await fresh.Confirm(receipt);PeerRecoveryOfferWire.ValidateConfirmation(result,receipt);
            Check(result.Result==PeerRecoveryOfferConfirmationResult.Confirmed&&Confirmed(f.B)&&f.B.State.Repository.Read(f.A.State.HostId)!.RecoveryRequired);
            before=OfferSnapshot(f.B);Check((await fresh.Confirm(receipt)).Result==PeerRecoveryOfferConfirmationResult.AlreadyConfirmed);
            Check((await fresh.Read(f.B)).Acknowledgment is null&&before==OfferSnapshot(f.B));
        }
        Check(await f.Sender(f.A).ConfirmAsync(f.B.State.HostId,f.B.Address)==PeerRecoveryCompletionExchange.Confirmed);
        Check(!f.B.State.Repository.Read(f.A.State.HostId)!.RecoveryRequired&&Confirmed(f.A)&&Confirmed(f.B));
        Check(CountEvent(f.B,"PeerRecoveryCompletionConfirmed")==1&&CountEvent(f.A,"PeerRecoveryCompletionReceived")==1);
    }
    public static async Task OfferPositiveExactAttestationOnlyAndConcurrentWinner()
    {
        await using var f=new Pair();await f.Start();using var client=new OfferClient(f.A,f.B);await client.Negotiate(f.A);
        var receipt=OfferReceipt(f);var before=OfferSnapshot(f.B);
        for(var mode=0;mode<9;mode++)
        {
            var bad=receipt.Clone();if(mode<4)bad.Result=(PeerRecoveryCompletionResult)new[]{0,-1,999,3}[mode];
            if(mode==4)bad.ReceivingHostId=f.B.State.HostId.ToString("D");if(mode==5)bad.ApprovingHostId=f.A.State.HostId.ToString("D");
            if(mode==6)bad.AcknowledgedFingerprint=new string('E',64);if(mode==7)bad.ApprovalId="bad";if(mode==8)bad.ApprovalId=Guid.NewGuid().ToString("D");
            await OfferRefused(client.Confirm(bad),mode==8?StatusCode.Unauthenticated:StatusCode.InvalidArgument);Check(before==OfferSnapshot(f.B));
        }
        var replies=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>client.Confirm(receipt)));
        Check(replies.Count(r=>r.Result==PeerRecoveryOfferConfirmationResult.Confirmed)==1&&replies.Count(r=>r.Result==PeerRecoveryOfferConfirmationResult.AlreadyConfirmed)==7);
        Check(CountEvent(f.B,"PeerRecoveryCompletionConfirmed")==1&&f.B.State.Repository.Read(f.A.State.HostId)!.RecoveryRequired);
    }
    public static async Task OfferFeatureNegotiationAndNoPending()
    {
        await using var f=new Pair();await f.Start();var before=OfferSnapshot(f.B);
        foreach(var mode in new[]{0,1,2})
        {
            using var client=new OfferClient(f.A,f.B);var hello=PeerSecurityRpcRuntime.RecoveryHello(f.A.State.HostId);
            if(mode==0)hello.Handshake.Capabilities.Remove(FeatureCapability.PeerRecoveryCompletionOffer);
            if(mode==1)hello.Handshake.Capabilities.Remove(FeatureCapability.PeerRecoveryCompletion);
            if(mode==2)hello=PeerSecurityRpcRuntime.Hello(f.A.State.HostId);
            if(mode==0)await client.Negotiate(f.A,hello);else await OfferRefused(client.Negotiate(f.A,hello),StatusCode.FailedPrecondition);
            await OfferRefused(client.Read(f.B),StatusCode.FailedPrecondition);await OfferRefused(client.Confirm(OfferReceipt(f)),StatusCode.FailedPrecondition);
            Check(before==OfferSnapshot(f.B));
        }
        using(var backport=new OfferClient(f.A,f.B))
        {
            var hello=PeerSecurityRpcRuntime.RecoveryHello(f.A.State.HostId);hello.Handshake.Protocol.Minor=1;
            Check((await backport.Negotiate(f.A,hello)).Handshake.Protocol.Minor==1);Check((await backport.Read(f.B)).Acknowledgment is not null);
            await OfferRefused(backport.Rpc.ReadRecoveryCompletionOfferAsync(new(){OfferingHostId=f.A.State.HostId.ToString("D")}).ResponseAsync,StatusCode.Unauthenticated);
        }
        await using var none=new Pair(false);await none.Start();using var empty=new OfferClient(none.A,none.B);await empty.Negotiate(none.A);
        var snapshot=OfferSnapshot(none.B);Check((await empty.Read(none.B)).Acknowledgment is null);
        var invented=OfferReceipt(none);invented.ApprovalId=Guid.NewGuid().ToString("D");await OfferRefused(empty.Confirm(invented),StatusCode.Unauthenticated);Check(snapshot==OfferSnapshot(none.B));
    }
    public static async Task OfferOriginalSessionCannotRecaptureRecoveryIncarnation()
    {
        await using var f=new Pair();await f.Start();using var held=new OfferClient(f.A,f.B);await held.Negotiate(f.A);
        await held.Rpc.ReceiveRecoveryCompletionAsync(new(){ReceivingHostId=f.B.State.HostId.ToString("D"),ApprovalId=f.AApproval.ToString("D"),AcknowledgedFingerprint=f.B.Pin});
        var before=OfferSnapshot(f.B);await OfferRefused(held.Read(f.B),StatusCode.Unauthenticated);await OfferRefused(held.Confirm(OfferReceipt(f)),StatusCode.Unauthenticated);Check(before==OfferSnapshot(f.B));
        using var fresh=new OfferClient(f.A,f.B);await fresh.Negotiate(f.A);Check((await fresh.Read(f.B)).Acknowledgment!.ApprovalId==f.BApproval.ToString("D"));
        Check((await fresh.Confirm(OfferReceipt(f))).Result==PeerRecoveryOfferConfirmationResult.Confirmed);
    }
    public static async Task OfferCurrentAuthorityAndLateAuditRollback()
    {
        foreach(var fault in new[]{"DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId;","UPDATE LocalPrincipals SET IsOwner=0 WHERE IsOwner=1;",
            "UPDATE PeerReplacementCompletions SET ConfirmedUtc=NULL;","UPDATE HostCapabilityGrants SET CanDelegate=1;"})
        {
            await using var f=new Pair();await f.Start();using var client=new OfferClient(f.A,f.B);await client.Negotiate(f.A);
            f.B.State.Execute("CREATE TRIGGER OfferFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerRecoveryCompletionConfirmed' BEGIN "+fault+" END;");
            var before=OfferSnapshot(f.B);await Failed(client.Confirm(OfferReceipt(f)));Check(before==OfferSnapshot(f.B));
            f.B.State.Execute("DROP TRIGGER OfferFault;");Check((await client.Confirm(OfferReceipt(f))).Result==PeerRecoveryOfferConfirmationResult.Confirmed);
        }
        for(var mode=0;mode<4;mode++)
        {
            await using var f=new Pair();await f.Start();using var client=new OfferClient(f.A,f.B);await client.Negotiate(f.A);
            if(mode==0)f.B.State.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{new string('E',64)}' WHERE CredentialRef='current';");
            if(mode==1)f.B.State.Execute("UPDATE LocalPrincipals SET IsOwner=0 WHERE IsOwner=1;");
            if(mode==2)f.B.State.Execute($"UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{new string('F',64)}' WHERE PeerHostId='{f.A.State.HostId:D}';");
            if(mode==3)f.B.State.Execute($"UPDATE TrustedManagers SET State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL,PendingTrustedPublicKeyFingerprint=NULL,PendingRotationId=NULL,PendingRotationExpiresUtc=NULL,PendingReconfirmationRequired=0,PeerRecoveryRequired=0 WHERE PeerHostId='{f.A.State.HostId:D}';");
            var before=OfferSnapshot(f.B);await Failed(client.Read(f.B));await Failed(client.Confirm(OfferReceipt(f)));Check(before==OfferSnapshot(f.B));
        }
    }
    public static async Task OfferOwnedChannelAndCancellation()
    {
        await using var f=new Pair();var before=OfferSnapshot(f.B);var contexts=new List<DefaultHttpContext>();
        DefaultHttpContext Context(PeerSecurityRpcRuntime? runtime=null)
        {var c=new DefaultHttpContext();c.Request.Scheme="https";c.Request.Protocol="HTTP/2";c.Features.Set(new PeerSecurityRpcConnection(runtime??f.B.Runtime,f.B.Pin,f.A.Pin));contexts.Add(c);return c;}
        Task Read(DefaultHttpContext c,CancellationToken ct=default)=>f.B.Runtime.Recovery.Offer(c,new(){OfferingHostId=f.B.State.HostId.ToString("D")},ct);
        Task Confirm(DefaultHttpContext c,CancellationToken ct=default)=>f.B.Runtime.Recovery.ConfirmOffer(c,OfferReceipt(f),ct);
        try
        {
            var foreign=Context(f.A.Runtime);await Failed(Read(foreign));await Failed(Confirm(foreign));
            var insecure=Context();insecure.Request.Scheme="http";await Failed(Read(insecure));await Failed(Confirm(insecure));
            var legacy=Context();legacy.Request.Protocol="HTTP/1.1";await Failed(Read(legacy));await Failed(Confirm(legacy));
            var live=Context();await f.B.Runtime.Recovery.Negotiate(live,PeerSecurityRpcRuntime.RecoveryHello(f.A.State.HostId),default);
            using var canceled=new CancellationTokenSource();canceled.Cancel();await Failed(Read(live,canceled.Token));await Failed(Confirm(live,canceled.Token));
            live.RequestAborted=canceled.Token;await Failed(Read(live));await Failed(Confirm(live));live.RequestAborted=default;
            await Failed(f.B.Runtime.Permissions.Invoke(live,_=>true,default));
            await live.Features.Get<PeerSecurityRpcConnection>()!.DisposeAsync();await Failed(Read(live));await Failed(Confirm(live));
            Check(before==OfferSnapshot(f.B));
        }
        finally{foreach(var context in contexts)await context.Features.Get<PeerSecurityRpcConnection>()!.DisposeAsync();}
    }
    public static async Task OfferStagedKeyCannotConfirmApprovedOldKey()
    {
        using var pending=new PeerTlsTests.Certificate();await using var f=new Pair();await f.Start();var pin=WindowsPeerTls.PublicFingerprint(pending.Value);
        f.A.State.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{pin}' WHERE CredentialRef='current';");
        f.B.State.Execute($"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{pin}',PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{f.B.State.Time.Now.AddMinutes(-1):O}',PendingReconfirmationRequired=1 WHERE PeerHostId='{f.A.State.HostId:D}';");
        var before=OfferSnapshot(f.B);using var client=new OfferClient(f.A,f.B,pending.Value);await client.Negotiate(f.A);
        await OfferRefused(client.Read(f.B),StatusCode.Unauthenticated);
        var receipt=OfferReceipt(f);receipt.AcknowledgedFingerprint=pin;
        await OfferRefused(client.Confirm(receipt),StatusCode.Unauthenticated);Check(before==OfferSnapshot(f.B));
    }
}
