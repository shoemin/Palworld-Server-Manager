using System.Security.Authentication;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using RawClient = PalworldServerManager.SelfTest.PeerSecurityRpcTests.RawClient;

namespace PalworldServerManager.SelfTest;

internal static partial class PeerRotationReceiptRpcTests
{
    private static PeerCurrentCredentialRpcClient CurrentClient(Rotation f, IPeerHttpTransportFactory? transport=null)
        => transport is null ? WindowsHostComposition.CreatePeerCurrentCredentialClient(f.A.Runtime,f.Next.Value) : new(f.A.Runtime,transport);
    private static PeerCurrentCredentialRequest CurrentRequest(Rotation f) => new()
    {RequestId=Guid.NewGuid().ToString("D"),HostId=f.A.State.HostId.ToString("D"),RotationId=f.Proposal.RotationId.ToString("D"),NewFingerprint=f.NextPin};
    private static Task<PeerCurrentCredentialReply> CurrentSend(RawClient client,PeerCurrentCredentialRequest request)
        => client.Rpc.ConfirmCurrentCredentialAsync(request,deadline:DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
    private static Task<bool> CurrentConfirm(Rotation f,IPeerHttpTransportFactory? transport=null)
        => CurrentClient(f,transport).ConfirmAsync(f.B.State.HostId,f.B.Address,f.Proposal.RotationId);
    public static async Task CurrentConfirmationRepairsClearedLegacyEvidence()
    {
        await using var f=new Rotation();await f.Start();
        Check(await f.Client.ConfirmAsync(f.A.State.HostId,f.A.Address)==PeerRotationReceiptExchange.Confirmed);
        Check(f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId is null);
        // Explicit fixture of the schema6 upgrade gap: historical receipt survives, current provenance absent.
        f.A.State.Execute("DELETE FROM HostRotationPromotionEvidence;");
        var history=HostDatabase.QueryScalarText(f.A.State.Writer,"SELECT StagedUtc||'/'||AcknowledgedUtc||'/'||PromotedUtc FROM HostCredentialRotationPeers;");
        await f.B.Start();Check(await CurrentConfirm(f));Check(!await CurrentConfirm(f));
        Check(f.A.State.Count("HostRotationCurrentCredentialEvidence")==1 && f.A.State.Count("HostRotationPromotionEvidence")==0);
        Check(HostDatabase.QueryScalarText(f.A.State.Writer,"SELECT StagedUtc||'/'||AcknowledgedUtc||'/'||PromotedUtc FROM HostCredentialRotationPeers;")==history);
        Check(Count(f.A,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRotationCurrentCredentialConfirmed';")==1);
        Check(f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId is null);f.Preserved();
    }
    public static async Task CurrentConfirmationDoesNotInventHistoryOrClearReceipt()
    {
        await using var f=new Rotation();await f.Start();await f.B.Start();
        f.A.State.Execute("DELETE FROM HostCredentialRotationPeers;"); // A missing history row is not invented staging/promotion.
        Check(await CurrentConfirm(f));
        Check(f.B.Runtime.Repository.Read(f.A.State.HostId)!.CurrentFingerprint==f.NextPin);
        Check(f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId==f.Proposal.RotationId);
        Check(Count(f.A,"SELECT COUNT(*) FROM HostCredentialRotationPeers WHERE StagedUtc IS NULL AND AcknowledgedUtc IS NULL AND PromotedUtc IS NULL;")==1);
        Check(f.A.State.Count("HostRotationPromotionEvidence")==0 && f.A.State.Count("HostRotationCurrentCredentialEvidence")==1);
        Check(await f.Client.ConfirmAsync(f.A.State.HostId,f.A.Address)==PeerRotationReceiptExchange.Confirmed);
        Check(Count(f.A,"SELECT COUNT(*) FROM HostCredentialRotationPeers WHERE PromotedUtc IS NOT NULL;")==1);f.Preserved();
    }
    public static async Task CurrentConfirmationProtocolAndTrustRefusals()
    {
        await using var f=new Rotation();await f.Start();await f.B.Start();var request=CurrentRequest(f);
        using(var none=new RawClient(f.A,f.B,certificate:f.Next.Value))await Refused(CurrentSend(none,request),StatusCode.FailedPrecondition);
        using(var legacy=new RawClient(f.A,f.B,certificate:f.Next.Value))
        {
            var hello=PeerSecurityRpcRuntime.Hello(f.A.State.HostId);hello.Handshake.Capabilities.Remove(FeatureCapability.PeerCurrentCredentialConfirmation);
            await legacy.Negotiate(hello);await Refused(CurrentSend(legacy,request),StatusCode.FailedPrecondition);
        }
        using var client=new RawClient(f.A,f.B,certificate:f.Next.Value);await client.Negotiate(PeerSecurityRpcRuntime.Hello(f.A.State.HostId));
        var malformed=request.Clone();malformed.RequestId="bad";await Refused(CurrentSend(client,malformed),StatusCode.InvalidArgument);
        malformed=request.Clone();malformed.HostId=Guid.NewGuid().ToString("D");await Refused(CurrentSend(client,malformed),StatusCode.Unauthenticated);
        malformed=request.Clone();malformed.NewFingerprint=f.A.Pin;await Refused(CurrentSend(client,malformed),StatusCode.Unauthenticated);
        f.B.State.Execute("UPDATE TrustedManagers SET State='PeerBound';");await Refused(CurrentSend(client,request),StatusCode.Unauthenticated);
        f.B.State.Execute("UPDATE TrustedManagers SET State='Active',PeerRecoveryRequired=1;");await Refused(CurrentSend(client,request),StatusCode.Unauthenticated);
        f.B.State.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=0;");await Refused(CurrentSend(client,request),StatusCode.Unauthenticated);
        Check(f.A.State.Count("HostRotationCurrentCredentialEvidence")==0);f.Preserved();
    }
    public static async Task CurrentConfirmationRejectsOldAndUnobservedPending()
    {
        await using var f=new Rotation();await f.Start();await f.B.Start();
        var incarnation=f.B.Runtime.Repository.ReadAuthenticatedRelationshipIncarnation(f.A.State.HostId,f.A.Pin,f.B.Pin);
        try {f.B.Runtime.Repository.ConfirmObservedCurrentCredential(PeerCurrentCredentialWire.Durable(CurrentRequest(f)),f.A.State.HostId,f.NextPin,f.B.Pin,incarnation);throw new Exception("Unobserved pending confirmed.");}
        catch(AuthenticationException) { }
        using var old=new RawClient(f.A,f.B);await old.Negotiate(PeerSecurityRpcRuntime.Hello(f.A.State.HostId));
        await Refused(CurrentSend(old,CurrentRequest(f)),StatusCode.Unauthenticated);
        Check(f.B.Runtime.Repository.Read(f.A.State.HostId)!.CurrentFingerprint==f.A.Pin && f.A.State.Count("HostRotationCurrentCredentialEvidence")==0);f.Preserved();
    }
    public static async Task CurrentConfirmationReplyFaultsAndStateChangeRetry()
    {
        foreach(var mode in Enumerable.Range(0,5))
        {
            await using var f=new Rotation();await f.Start();await f.B.Start();
            var fault=new PeerReplyFaultTransport<PeerCurrentCredentialReply>(new WindowsPeerHttpTransportFactory(f.Next.Value),"ConfirmCurrentCredential",PeerCurrentCredentialReply.Parser,reply=>
            {
                if(mode==0)reply.Request.RequestId=Guid.NewGuid().ToString("D");
                if(mode==1)reply.Result=(PeerCurrentCredentialResult)73;
                if(mode==2)throw new IOException("Fixture confirmation reply lost.");
                if(mode==3)f.A.State.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=1; UPDATE TrustedManagers SET PeerRecoveryRequired=0;");
                if(mode==4)f.A.State.Execute("UPDATE HostIdentity SET CurrentCredentialRef='current';");
            });
            var refused=false;
            try {await CurrentConfirm(f,fault);} catch(Exception ex) when(ex is AuthenticationException or RpcException or InvalidDataException) {refused=true;}
            Check(refused && fault.Altered==1 && f.A.State.Count("HostRotationCurrentCredentialEvidence")==0);
            if(mode==4)f.A.State.Execute($"UPDATE HostIdentity SET CurrentCredentialRef='{f.A.Runtime.Credentials.Read().Rotations.Single().NewReference}';");
            Check(await CurrentConfirm(f));Check(f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId==f.Proposal.RotationId);f.Preserved();
        }
    }
    public static async Task CurrentConfirmationAuditAndQueuedWriterRollback()
    {
        await using var f=new Rotation();await f.Start();await f.B.Start();
        f.A.State.Execute("CREATE TRIGGER FailCurrentProof BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='HostRotationCurrentCredentialConfirmed' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        try {await CurrentConfirm(f);throw new Exception("Expected confirmation audit failure.");} catch(SqliteException) { }
        Check(f.A.State.Count("HostRotationCurrentCredentialEvidence")==0);
        f.A.State.Execute("DROP TRIGGER FailCurrentProof; CREATE TRIGGER ChangeCurrentProof AFTER INSERT ON AuditEvents WHEN NEW.EventKind='HostRotationCurrentCredentialConfirmed' BEGIN UPDATE TrustedManagers SET PeerRecoveryRequired=1; UPDATE TrustedManagers SET PeerRecoveryRequired=0; END;");
        try {await CurrentConfirm(f);throw new Exception("Expected final incarnation refusal.");} catch(AuthenticationException) { }
        Check(f.A.State.Count("HostRotationCurrentCredentialEvidence")==0 && Count(f.A,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRotationCurrentCredentialConfirmed';")==0);
        f.A.State.Execute("DROP TRIGGER ChangeCurrentProof;");
        var incarnation=f.A.Runtime.Repository.ReadAuthenticatedRelationshipIncarnation(f.B.State.HostId,f.B.Pin,f.NextPin);
        var proof=f.A.Runtime.Credentials.PrepareCurrentCredentialConfirmation(f.Proposal.RotationId,f.NextPin);
        using(var tx=f.A.State.Writer.BeginTransaction())
        {
            HostDatabase.Execute(f.A.State.Writer,"UPDATE TrustedManagers SET PeerRecoveryRequired=1; UPDATE TrustedManagers SET PeerRecoveryRequired=0;",tx);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work=Task.Run(()=>{entered.SetResult();return f.A.Runtime.Credentials.RecordCurrentCredentialConfirmation(proof,f.B.State.HostId,f.B.Pin,f.NextPin,incarnation);});
            try {await entered.Task;await Task.Delay(100);Check(!work.IsCompleted);} finally {tx.Commit();}
            try {await work.WaitAsync(TimeSpan.FromSeconds(10));throw new Exception("Queued stale confirmation succeeded.");} catch(AuthenticationException) { }
        }
        Check(f.A.State.Count("HostRotationCurrentCredentialEvidence")==0);Check(await CurrentConfirm(f));f.Preserved();
    }
}
