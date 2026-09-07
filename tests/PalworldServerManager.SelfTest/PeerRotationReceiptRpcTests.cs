using System.Security.Authentication;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Windows;
using Fixture = PalworldServerManager.SelfTest.PeerSecurityRpcTests.Fixture;
using RawClient = PalworldServerManager.SelfTest.PeerSecurityRpcTests.RawClient;

namespace PalworldServerManager.SelfTest;

internal static class PeerRotationReceiptRpcTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Rotation receipt RPC assertion failed."); }
    private static async Task Refused<T>(Task<T> task, StatusCode code)
    { try { await task; } catch (RpcException ex) when (ex.StatusCode == code) { return; } throw new Exception("Expected receipt refusal: " + code); }
    private static LocalPrincipalMutationActor Owner(Fixture f) => new(f.State.HostId, f.State.OwnerId, "native-owner", "fixture-public");
    private static long Count(Fixture f, string sql) => HostDatabase.QueryScalarLong(f.State.Writer, sql);
    private sealed class Rotation : IAsyncDisposable
    {
        internal readonly Fixture A = new(), B = new();
        internal readonly PeerTlsTests.Certificate Next = new();
        internal HostRotationProposal Proposal = null!;
        internal string NextPin => WindowsPeerTls.PublicFingerprint(Next.Value);
        internal PeerRotationReceiptRpcClient Client => WindowsHostComposition.CreatePeerRotationReceiptClient(B.Runtime, B.Certificate.Value);
        internal async Task Start()
        {
            A.Bind(B); B.Bind(A); A.State.Execute("UPDATE TrustedManagers SET State='Active';"); B.State.Execute("UPDATE TrustedManagers SET State='Active';");
            var state = A.Runtime.Credentials; var rotation = state.PrepareRoutineRotation(Owner(A), Guid.NewGuid());
            state.RecordCreated(rotation.NewReference, NextPin); state.BeginRoutineRotationStaging(Owner(A), rotation.RotationId);
            Proposal = state.PrepareRoutineRotationProposal(Owner(A), rotation.RotationId);
            await B.Start();
            await WindowsHostComposition.CreatePeerRotationProposalClient(A.Runtime, A.Certificate.Value).StageAsync(B.State.HostId, B.Address, rotation.RotationId);
            await B.Stop();
            // Explicit synthetic global cutover; proposal delivery and subsequent New-key TLS are real.
            A.State.Execute($"UPDATE HostIdentity SET CurrentCredentialRef='{rotation.NewReference}'; UPDATE HostCredentialRotations SET State='CutOver' WHERE RotationId='{rotation.RotationId:D}';");
            await A.Start(Next.Value);
        }
        internal void Preserved(int peerHostGrants = 0)
        {
            Check(A.State.Count("HostCapabilityGrants") == 0 && A.State.Count("ServerCapabilityGrants") == 0 && B.State.Count("ServerCapabilityGrants") == 0);
            Check(B.State.Count("HostCapabilityGrants") == peerHostGrants);
            Check(A.State.Count("ActivationRpcEffects") == 0 && B.State.Count("ActivationRpcEffects") == 0);
            Check(A.Runtime.Credentials.Read().Credentials.All(c => !c.Retired));
            Check(A.Runtime.Credentials.Read().Rotations.Single().State == HostCredentialRotationState.CutOver);
            Check(B.Runtime.Credentials.Read().CurrentReference == "current");
        }
        public async ValueTask DisposeAsync()
        { try { await A.DisposeAsync(); } finally { try { await B.DisposeAsync(); } finally { Next.Dispose(); } } }
    }
    public static async Task ActualObservationAndConcurrentReceipt()
    {
        await using var f = new Rotation(); await f.Start();
        f.B.State.Execute($"""
            INSERT INTO HostCapabilityGrants (GrantId,TargetHostId,Capability,GranteeActorKind,GranteePeerHostId,
                GrantedByActorKind,GrantedByLocalPrincipalId,CanDelegate,CanDelegateOnwardDelegation,CreatedUtc)
                VALUES ('custom','{f.B.State.HostId:D}','ViewHost','RemoteManager','{f.A.State.HostId:D}',
                    'LocalPrincipal','{f.B.State.OwnerId:D}',1,0,'preserved');
            """);
        Check(f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingFingerprint == f.NextPin);
        var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Client.ConfirmAsync(f.A.State.HostId, f.A.Address)));
        Check(replies.Contains(PeerRotationReceiptExchange.Confirmed));
        Check(replies.All(r => r is PeerRotationReceiptExchange.Confirmed or PeerRotationReceiptExchange.NoReceiptPending));
        var trust = f.B.Runtime.Repository.Read(f.A.State.HostId)!;
        Check(trust.CurrentFingerprint == f.NextPin && trust.PendingFingerprint is null && trust.PendingRotationId is null);
        Check(f.B.State.Count("TrustedManagerCredentialHistory") == 1);
        Check(Count(f.A, "SELECT COUNT(*) FROM HostCredentialRotationPeers WHERE StagedUtc IS NOT NULL AND AcknowledgedUtc IS NOT NULL AND PromotedUtc IS NOT NULL;") == 1);
        Check(Count(f.A, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRotationPeerPromotionReceived' AND ActorKind='RemoteManager';") == 1);
        Check(Count(f.B, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerRotationReceiptConfirmed';") == 1);
        Check(await f.Client.ConfirmAsync(f.A.State.HostId, f.A.Address) == PeerRotationReceiptExchange.NoReceiptPending);
        Check(Count(f.B, "SELECT COUNT(*) FROM HostCapabilityGrants WHERE GrantId='custom' AND Capability='ViewHost' AND CanDelegate=1 AND InvalidatedUtc IS NULL;") == 1);
        Check(!f.B.Runtime.Repository.RecognizesTransportFingerprint(f.A.Pin)); f.Preserved(1);
    }
    public static async Task LostReceiptAndReceiverAuditRetryAfterReopen()
    {
        await using var f = new Rotation(); await f.Start();
        var lost = new PeerReplyFaultTransport<PeerRotationReceiptReply>(new WindowsPeerHttpTransportFactory(f.B.Certificate.Value), "ConfirmRotationPromotion", PeerRotationReceiptReply.Parser,
            reply => { Check(reply.Result == PeerRotationReceiptResult.Recorded); throw new IOException("Fixture durable promotion reply lost."); });
        var observedLoss = false;
        try { await new PeerRotationReceiptRpcClient(f.B.Runtime, lost).ConfirmAsync(f.A.State.HostId, f.A.Address); }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        { for (Exception? cause = ex; cause is not null; cause = cause.InnerException) if (cause is IOException && cause.Message == "Fixture durable promotion reply lost.") observedLoss = true; }
        Check(observedLoss && lost.Altered == 1 && f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId == f.Proposal.RotationId);
        Check(Count(f.A, "SELECT COUNT(*) FROM HostCredentialRotationPeers WHERE PromotedUtc IS NOT NULL;") == 1);
        f.B.State.Execute("CREATE TRIGGER FailReceipt BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='PeerRotationReceiptConfirmed' BEGIN SELECT RAISE(ABORT,'fixture receipt audit failure'); END;");
        try { await f.Client.ConfirmAsync(f.A.State.HostId, f.A.Address); throw new Exception("Expected receipt audit refusal."); } catch (SqliteException) { }
        Check(f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId == f.Proposal.RotationId);
        f.B.State.Execute("DROP TRIGGER FailReceipt;"); await f.A.Stop(); await f.A.Start(f.Next.Value);
        var reopened = new PeerSecurityRpcRuntime(f.B.State.Database, f.B.State.HostId, f.B.Runtime.Hook, f.B.State.Time);
        Check(await WindowsHostComposition.CreatePeerRotationReceiptClient(reopened, f.B.Certificate.Value).ConfirmAsync(f.A.State.HostId, f.A.Address) == PeerRotationReceiptExchange.Confirmed);
        Check(f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId is null);
        Check(Count(f.A, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRotationPeerPromotionReceived';") == 1); f.Preserved();
    }
    public static async Task SenderAuditFailureKeepsPromotionRetryable()
    {
        await using var f = new Rotation(); await f.Start();
        f.A.State.Execute("CREATE TRIGGER FailReceipt BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='HostRotationPeerPromotionReceived' BEGIN SELECT RAISE(ABORT,'fixture receipt audit failure'); END;");
        await Refused(f.Client.ConfirmAsync(f.A.State.HostId, f.A.Address), StatusCode.Internal);
        var trust = f.B.Runtime.Repository.Read(f.A.State.HostId)!;
        Check(trust.CurrentFingerprint == f.NextPin && trust.PendingFingerprint is null && trust.PendingRotationId == f.Proposal.RotationId);
        Check(Count(f.A, "SELECT COUNT(*) FROM HostCredentialRotationPeers WHERE PromotedUtc IS NOT NULL;") == 0);
        f.A.State.Execute("DROP TRIGGER FailReceipt;");
        Check(await f.Client.ConfirmAsync(f.A.State.HostId, f.A.Address) == PeerRotationReceiptExchange.Confirmed); f.Preserved();
    }
    public static async Task ForgedReplyAndStaleReceiverStateCannotClear()
    {
        for (var variant = 0; variant < 4; variant++)
        {
            await using var f = new Rotation(); await f.Start();
            var fault = new PeerReplyFaultTransport<PeerRotationReceiptReply>(new WindowsPeerHttpTransportFactory(f.B.Certificate.Value), "ConfirmRotationPromotion", PeerRotationReceiptReply.Parser, reply =>
            {
                if (variant == 0) reply.Request.RequestId = Guid.NewGuid().ToString("D");
                if (variant == 1) reply.Result = (PeerRotationReceiptResult)99;
                if (variant == 2) f.B.State.Execute("UPDATE SecureCredentialReferences SET PublicKeyFingerprint='" + new string('D', 64) + "' WHERE CredentialRef='current';");
                if (variant == 3) f.B.State.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=1;");
            });
            try { await new PeerRotationReceiptRpcClient(f.B.Runtime, fault).ConfirmAsync(f.A.State.HostId, f.A.Address); throw new Exception("Expected forged or stale receipt refusal."); } catch (AuthenticationException) { }
            Check(fault.Altered == 1 && f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId == f.Proposal.RotationId);
            Check(Count(f.B, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerRotationReceiptConfirmed';") == 0);
            Check(Count(f.A, "SELECT COUNT(*) FROM HostCredentialRotationPeers WHERE PromotedUtc IS NOT NULL;") == 1); f.Preserved();
        }
    }
    public static async Task CapabilityIdentityCurrentAndTrustGates()
    {
        await using var f = new Rotation(); await f.Start();
        var request = PeerRotationReceiptWire.Wire(new(Guid.NewGuid(), f.A.State.HostId, f.Proposal.RotationId, f.NextPin));
        Task<PeerRotationReceiptReply> Send(RawClient c, PeerRotationReceiptRequest r) => c.Rpc.ConfirmRotationPromotionAsync(r, deadline: DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
        using (var c = new RawClient(f.B, f.A, f.NextPin)) await Refused(Send(c, request), StatusCode.FailedPrecondition);
        using (var c = new RawClient(f.B, f.A, f.NextPin))
        {
            var hello = PeerSecurityRpcRuntime.Hello(f.B.State.HostId); hello.Handshake.Capabilities.Remove(FeatureCapability.PeerRotationReceipt);
            await c.Negotiate(hello); await Refused(Send(c, request), StatusCode.FailedPrecondition);
        }
        using (var c = new RawClient(f.B, f.A, f.NextPin))
        {
            await c.Negotiate(PeerSecurityRpcRuntime.Hello(f.B.State.HostId));
            var wrong = request.Clone(); wrong.HostId = Guid.NewGuid().ToString("D"); await Refused(Send(c, wrong), StatusCode.Unauthenticated);
            wrong = request.Clone(); wrong.RotationId = Guid.NewGuid().ToString("D"); await Refused(Send(c, wrong), StatusCode.Unauthenticated);
            wrong = request.Clone(); wrong.NewFingerprint = f.A.Pin; await Refused(Send(c, wrong), StatusCode.Unauthenticated);
            wrong = request.Clone(); wrong.RequestId = "invalid"; await Refused(Send(c, wrong), StatusCode.InvalidArgument);
            f.A.State.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=1;"); await Refused(Send(c, request), StatusCode.Unauthenticated);
            f.A.State.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=0,State='PeerBound';"); await Refused(Send(c, request), StatusCode.Unauthenticated);
            f.A.State.Execute("UPDATE TrustedManagers SET State='Active'; UPDATE HostIdentity SET CurrentCredentialRef='current'; UPDATE HostCredentialRotations SET State='Staging';");
            await Refused(Send(c, request), StatusCode.Unauthenticated); // New listener is no longer current.
        }
        Check(Count(f.A, "SELECT COUNT(*) FROM HostCredentialRotationPeers WHERE PromotedUtc IS NOT NULL;") == 0);
        Check(f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId == f.Proposal.RotationId);
    }
    public static async Task LegacyReceiptDoesNotInventStagingHistory()
    {
        await using var f = new Rotation(); await f.Start();
        // Upgrade input lacks per-peer history. Completed/retired metadata is synthetic,
        // and a subsequent Prepared rotation still presents the earlier New key.
        f.A.State.Execute("DELETE FROM HostCredentialRotationPeers; UPDATE HostCredentialRotations SET State='Completed'; UPDATE SecureCredentialReferences SET RetiredUtc='synthetic-retired' WHERE CredentialRef='current';");
        var subsequent = f.A.Runtime.Credentials.PrepareRoutineRotation(Owner(f.A), Guid.NewGuid());
        Check(await f.Client.ConfirmAsync(f.A.State.HostId, f.A.Address) == PeerRotationReceiptExchange.Confirmed);
        Check(Count(f.A, "SELECT COUNT(*) FROM HostCredentialRotationPeers WHERE StagedUtc IS NULL AND AcknowledgedUtc IS NULL AND PromotedUtc IS NOT NULL;") == 1);
        Check(f.A.Runtime.Credentials.Read().Rotations.Single(r => r.RotationId == subsequent.RotationId).State == HostCredentialRotationState.Prepared);
        Check(f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId is null);
        Check(f.A.State.Count("HostCapabilityGrants") == 0 && f.B.State.Count("HostCapabilityGrants") == 0);
    }
    public static async Task OldPresentationIncompleteProofAndChangedReceipt()
    {
        await using var f = new Rotation(); await f.Start();
        var incomplete = new PeerSecurityRpcTests.UnprovenTransport(f.NextPin);
        try { await new PeerRotationReceiptRpcClient(f.B.Runtime, incomplete).ConfirmAsync(f.A.State.HostId, f.A.Address); throw new Exception("Expected incomplete proof refusal."); }
        catch (AuthenticationException ex) when (ex.Message == "Fixture handshake did not complete.") { }
        Check(incomplete.Admitted && f.B.Runtime.Repository.Read(f.A.State.HostId)!.CurrentFingerprint == f.A.Pin);
        await f.A.Stop();
        f.A.State.Execute("UPDATE HostIdentity SET CurrentCredentialRef='current'; UPDATE HostCredentialRotations SET State='Staging';");
        await f.A.Start();
        try { await f.Client.ConfirmAsync(f.A.State.HostId, f.A.Address); throw new Exception("Expected unobserved New refusal."); } catch (InvalidOperationException) { }
        using (var c = new RawClient(f.B, f.A))
        {
            await c.Negotiate(PeerSecurityRpcRuntime.Hello(f.B.State.HostId));
            var request = PeerRotationReceiptWire.Wire(new(Guid.NewGuid(), f.A.State.HostId, f.Proposal.RotationId, f.NextPin));
            await Refused(c.Rpc.ConfirmRotationPromotionAsync(request, deadline: DateTime.UtcNow.AddSeconds(5)).ResponseAsync, StatusCode.Unauthenticated);
        }
        Check(Count(f.A, "SELECT COUNT(*) FROM HostCredentialRotationPeers WHERE PromotedUtc IS NOT NULL;") == 0);
        Check(f.B.State.Count("TrustedManagerCredentialHistory") == 0);
        await f.A.Stop(); var rotation = f.A.Runtime.Credentials.Read().Rotations.Single();
        f.A.State.Execute($"UPDATE HostIdentity SET CurrentCredentialRef='{rotation.NewReference}'; UPDATE HostCredentialRotations SET State='CutOver';");
        await f.A.Start(f.Next.Value); var changed = Guid.NewGuid();
        var fault = new PeerReplyFaultTransport<PeerRotationReceiptReply>(new WindowsPeerHttpTransportFactory(f.B.Certificate.Value), "ConfirmRotationPromotion", PeerRotationReceiptReply.Parser,
            _ => f.B.State.Execute($"UPDATE TrustedManagers SET PendingRotationId='{changed:D}';"));
        try { await new PeerRotationReceiptRpcClient(f.B.Runtime, fault).ConfirmAsync(f.A.State.HostId, f.A.Address); throw new Exception("Expected changed receipt refusal."); } catch (AuthenticationException) { }
        Check(fault.Altered == 1 && f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId == changed); f.Preserved();
    }
}
