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

internal static class PeerRotationProposalRpcTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Rotation proposal RPC assertion failed."); }
    private static LocalPrincipalMutationActor Owner(Fixture f) => new(f.State.HostId, f.State.OwnerId, "native-owner", "fixture-public");
    private static async Task Refused<T>(Task<T> task, StatusCode code)
    { try { await task; } catch (RpcException ex) when (ex.StatusCode == code) { return; } throw new Exception("Expected proposal RPC refusal: " + code); }
    private static void Active(Fixture a, Fixture b)
    { a.Bind(b); b.Bind(a); a.State.Execute("UPDATE TrustedManagers SET State='Active';"); b.State.Execute("UPDATE TrustedManagers SET State='Active';"); }
    private static HostRotationProposal Prepare(Fixture f, string next)
    {
        var state = f.Runtime.Credentials; var owner = Owner(f); var rotation = state.PrepareRoutineRotation(owner, Guid.NewGuid());
        state.RecordCreated(rotation.NewReference, next); state.BeginRoutineRotationStaging(owner, rotation.RotationId);
        return state.PrepareRoutineRotationProposal(owner, rotation.RotationId);
    }
    private static PeerRotationProposalRpcClient Client(Fixture f) => WindowsHostComposition.CreatePeerRotationProposalClient(f.Runtime, f.Certificate.Value);
    private static void NoAuthority(Fixture a, Fixture b)
    {
        foreach (var f in new[] { a, b }) Check(f.State.Count("HostCapabilityGrants") == 0 && f.State.Count("ServerCapabilityGrants") == 0 && f.State.Count("ActivationRpcEffects") == 0);
        Check(a.Runtime.Credentials.Read().CurrentReference == "current" && b.Runtime.Credentials.Read().CurrentReference == "current");
        Check(a.Runtime.Credentials.Read().Rotations.All(r => r.State is HostCredentialRotationState.Staging or HostCredentialRotationState.Aborted));
    }
    public static async Task ActualConcurrentProposalAndClockOffset()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); Active(a, b); var proposal = Prepare(a, new string('C', 64));
        a.State.Time.Now -= TimeSpan.FromDays(19); b.State.Time.Now += TimeSpan.FromDays(7); await b.Start();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Client(a).StageAsync(b.State.HostId, b.Address, proposal.RotationId)));
        Check(results.All(r => r.Outcome == PeerRotationProposalOutcome.Acknowledged && r.RetainedRotationId == proposal.RotationId && r.RemainingAcceptance > TimeSpan.FromMinutes(29) && r.RemainingAcceptance <= TimeSpan.FromMinutes(30)));
        Check(a.State.Count("HostCredentialRotationPeers") == 1 && b.State.Count("PeerRotationProposals") == 1);
        Check(HostDatabase.QueryScalarLong(a.State.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRotationPeerAcknowledged' AND ActorKind='RemoteManager';") == 1);
        var deadline = b.Runtime.Repository.Read(a.State.HostId)!.PendingRotationExpiresUtc;
        b.State.Time.Now += TimeSpan.FromMinutes(2);
        var retry = await Client(a).StageAsync(b.State.HostId, b.Address, proposal.RotationId);
        Check(retry.RemainingAcceptance <= TimeSpan.FromMinutes(28) && b.Runtime.Repository.Read(a.State.HostId)!.PendingRotationExpiresUtc == deadline);
        Check(b.Runtime.Repository.Read(a.State.HostId)!.CurrentFingerprint == a.Pin); NoAuthority(a, b);
    }
    public static async Task LostReplyAndAuditFailureResumeDurably()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); Active(a, b); var proposal = Prepare(a, new string('C', 64)); await b.Start();
        var loss = new PeerReplyFaultTransport<PeerRotationProposalReply>(new WindowsPeerHttpTransportFactory(a.Certificate.Value), "StageRotation", PeerRotationProposalReply.Parser,
            reply => { Check(reply.State == PeerRotationStagingState.Staged); throw new IOException("Fixture staging acknowledgement lost."); });
        var lost = false;
        try { await new PeerRotationProposalRpcClient(a.Runtime, loss).StageAsync(b.State.HostId, b.Address, proposal.RotationId); }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            for (Exception? cause = ex; cause is not null; cause = cause.InnerException)
                if (cause is IOException && cause.Message == "Fixture staging acknowledgement lost.") lost = true;
        }
        Check(lost && loss.Altered == 1 && a.State.Count("HostCredentialRotationPeers") == 0 && b.State.Count("PeerRotationProposals") == 1);
        var deadline = b.Runtime.Repository.Read(a.State.HostId)!.PendingRotationExpiresUtc;
        a.State.Execute("CREATE TRIGGER FailAck BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='HostRotationPeerAcknowledged' BEGIN SELECT RAISE(ABORT,'fixture acknowledgement audit failure'); END;");
        try { await Client(a).StageAsync(b.State.HostId, b.Address, proposal.RotationId); throw new Exception("Expected acknowledgement audit refusal."); } catch (SqliteException) { }
        Check(a.State.Count("HostCredentialRotationPeers") == 0 && b.Runtime.Repository.Read(a.State.HostId)!.PendingRotationExpiresUtc == deadline);
        a.State.Execute("DROP TRIGGER FailAck;"); await b.Stop(); await b.Start();
        Check((await Client(a).StageAsync(b.State.HostId, b.Address, proposal.RotationId)).Outcome == PeerRotationProposalOutcome.Acknowledged);
        Check(a.State.Count("HostCredentialRotationPeers") == 1 && b.Runtime.Repository.Read(a.State.HostId)!.PendingRotationExpiresUtc == deadline); NoAuthority(a, b);
    }
    public static async Task LapsedAndReceiptStateCannotBeOverwritten()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); Active(a, b); var first = Prepare(a, new string('C', 64)); await b.Start();
        await Client(a).StageAsync(b.State.HostId, b.Address, first.RotationId);
        a.Runtime.Credentials.AbortRoutineRotation(Owner(a), first.RotationId); var next = Prepare(a, new string('D', 64)); b.State.Time.Now += TimeSpan.FromMinutes(30);
        var blocked = await Client(a).StageAsync(b.State.HostId, b.Address, next.RotationId);
        Check(blocked.Outcome == PeerRotationProposalOutcome.ReconfirmationRequired && blocked.RetainedRotationId == first.RotationId && blocked.RemainingAcceptance == TimeSpan.Zero);
        Check(a.State.Count("HostCredentialRotationPeers") == 1 && b.State.Count("PeerRotationProposals") == 1);
        // Synthetic already-confirmed earlier promotion under the unchanged actual sender key;
        // this solely probes the receipt gate over real RPC, not a global cutover claim.
        b.State.Execute("UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint=NULL,PendingRotationExpiresUtc=NULL,PendingReconfirmationRequired=0;");
        blocked = await Client(a).StageAsync(b.State.HostId, b.Address, next.RotationId);
        Check(blocked.Outcome == PeerRotationProposalOutcome.PromotionReceiptPending && blocked.RetainedRotationId == first.RotationId && b.State.Count("PeerRotationProposals") == 1);
        NoAuthority(a, b);
    }
    public static async Task ProposalProtocolIdentityAndFreshState()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); Active(a, b); var proposal = Prepare(a, new string('C', 64)); await b.Start();
        var request = PeerRotationProposalWire.Wire(proposal);
        Task<PeerRotationProposalReply> Stage(RawClient c, PeerRotationProposalRequest r) => c.Rpc.StageRotationAsync(r, deadline: DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
        using (var raw = new RawClient(a, b)) await Refused(Stage(raw, request), StatusCode.FailedPrecondition);
        using (var raw = new RawClient(a, b))
        {
            var legacy = PeerSecurityRpcRuntime.Hello(a.State.HostId); legacy.Handshake.Capabilities.Remove(FeatureCapability.PeerRotationProposal);
            await raw.Negotiate(legacy); await Refused(Stage(raw, request), StatusCode.FailedPrecondition);
        }
        using (var raw = new RawClient(a, b))
        {
            await raw.Negotiate(PeerSecurityRpcRuntime.Hello(a.State.HostId));
            var wrong = request.Clone(); wrong.HostId = Guid.NewGuid().ToString("D"); await Refused(Stage(raw, wrong), StatusCode.Unauthenticated);
            wrong = request.Clone(); wrong.Sequence = 0; await Refused(Stage(raw, wrong), StatusCode.Unauthenticated);
            b.State.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=1;"); await Refused(Stage(raw, request), StatusCode.Unauthenticated);
            b.State.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=0,State='PeerBound';"); await Refused(Stage(raw, request), StatusCode.Unauthenticated);
            b.State.Execute("UPDATE TrustedManagers SET State='Active'; UPDATE SecureCredentialReferences SET PublicKeyFingerprint='" + new string('D', 64) + "' WHERE CredentialRef='current';");
            await Refused(Stage(raw, request), StatusCode.Unauthenticated); // Actual receiver-local TLS key no longer current.
        }
        Check(b.State.Count("PeerRotationProposals") == 0 && a.State.Count("HostCredentialRotationPeers") == 0); NoAuthority(a, b);
    }
    public static async Task ForgedAcknowledgementAndConcurrentAbort()
    {
        for (var variant = 0; variant < 4; variant++)
        {
            await using var a = new Fixture(); await using var b = new Fixture(); Active(a, b); var proposal = Prepare(a, new string('C', 64)); await b.Start();
            var fault = new PeerReplyFaultTransport<PeerRotationProposalReply>(new WindowsPeerHttpTransportFactory(a.Certificate.Value), "StageRotation", PeerRotationProposalReply.Parser, reply =>
            {
                if (variant == 0) reply.Request.RequestId = Guid.NewGuid().ToString("D");
                if (variant == 1) reply.State = (PeerRotationStagingState)99;
                if (variant == 2) reply.RemainingAcceptanceMilliseconds = PeerRotationProposalWire.MaximumAcceptanceMilliseconds + 1;
                if (variant == 3) a.Runtime.Credentials.AbortRoutineRotation(Owner(a), proposal.RotationId);
            });
            try { await new PeerRotationProposalRpcClient(a.Runtime, fault).StageAsync(b.State.HostId, b.Address, proposal.RotationId); throw new Exception("Expected forged or stale acknowledgement refusal."); } catch (AuthenticationException) { }
            Check(fault.Altered == 1 && a.State.Count("HostCredentialRotationPeers") == 0 && b.State.Count("PeerRotationProposals") == 1);
            if (variant == 3)
            {
                await a.Start();
                Check(await WindowsHostComposition.CreatePeerRotationStatusClient(b.Runtime, b.Certificate.Value).CheckAsync(a.State.HostId, a.Address) == PeerRotationStatusExchange.Cleared);
                Check(b.Runtime.Repository.Read(a.State.HostId)!.PendingRotationId is null);
            }
            NoAuthority(a, b);
        }
    }
    private sealed class Clock : TimeProvider
    {
        internal long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.MinValue;
    }
    public static Task ConservativeRemainingTimeIsNotDurableAuthority()
    {
        var time = new Clock(); var result = new PeerRotationProposalExchange(PeerRotationProposalOutcome.Acknowledged, Guid.NewGuid(), 10000, time, time.GetTimestamp());
        time.Seconds = 3; Check(result.RemainingAcceptance == TimeSpan.FromSeconds(7));
        time.Seconds = 10; Check(result.RemainingAcceptance == TimeSpan.Zero);
        time.Seconds = -1; Check(result.RemainingAcceptance == TimeSpan.Zero);
        time.Seconds = 4; Check(result.RemainingAcceptance == TimeSpan.Zero); // An expired/faulted bound never revives.
        return Task.CompletedTask;
    }
}
