using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Platform.Windows;
using Fixture = PalworldServerManager.SelfTest.PeerSecurityRpcTests.Fixture;

namespace PalworldServerManager.SelfTest;

internal static class RotationAcceptanceCollectorTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Rotation acceptance assertion failed."); }
    private static LocalPrincipalMutationActor Owner(Fixture f) => new(f.State.HostId, f.State.OwnerId, "native-owner", "fixture-public");
    private static bool Has(RotationAcceptanceAssessment result, RotationAcceptanceBlock reason, Guid? peer = null)
        => result.Blockers.Any(b => b.Reason == reason && (peer is null || b.PeerHostId == peer));
    private sealed class Peers : IAsyncDisposable
    {
        internal readonly Fixture A = new(), B = new(), C = new();
        internal Guid RotationId;
        internal IReadOnlyDictionary<Guid, Uri> Routes => new Dictionary<Guid, Uri> { [B.State.HostId] = B.Address, [C.State.HostId] = C.Address };
        internal RoutineRotationAcceptanceCollector Collector => WindowsHostComposition.CreateRotationAcceptanceCollector(A.Runtime, A.Certificate.Value);
        internal async Task Start()
        {
            A.Bind(B); A.Bind(C); B.Bind(A); C.Bind(A);
            foreach (var f in new[] { A, B, C }) f.State.Execute("UPDATE TrustedManagers SET State='Active';");
            var state = A.Runtime.Credentials; var prepared = state.PrepareRoutineRotation(Owner(A), Guid.NewGuid()); RotationId = prepared.RotationId;
            state.RecordCreated(prepared.NewReference, new string('D', 64)); state.BeginRoutineRotationStaging(Owner(A), RotationId);
            state.PrepareRoutineRotationProposal(Owner(A), RotationId); await B.Start(); await C.Start();
        }
        internal void Preserved()
        {
            foreach (var f in new[] { A, B, C })
                Check(f.State.Count("HostCapabilityGrants") == 0 && f.State.Count("ServerCapabilityGrants") == 0 && f.State.Count("ActivationRpcEffects") == 0 && f.Runtime.Credentials.Read().CurrentReference == "current");
            Check(A.Runtime.Credentials.Read().Rotations.Single().State is HostCredentialRotationState.Staging or HostCredentialRotationState.Aborted);
        }
        public async ValueTask DisposeAsync()
        { try { await A.DisposeAsync(); } finally { try { await B.DisposeAsync(); } finally { await C.DisposeAsync(); } } }
    }
    public static async Task ActualAllPeerCollectionAndHistoryIsNotFreshEvidence()
    {
        await using var f = new Peers(); await f.Start(); var routes = f.Routes; var collector = f.Collector;
        var round = await collector.CollectAsync(f.RotationId, routes);
        Check(collector.Recheck(round).PeerAcknowledgementsReady && round.Exchanges.Count == 2);
        Check(f.A.State.Count("HostCredentialRotationPeers") == 2 && f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId == f.RotationId && f.C.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId == f.RotationId);
        await f.B.Stop(); await f.C.Stop();
        Check(collector.Recheck(round).PeerAcknowledgementsReady); // An already-accepted peer going offline alone does not erase its live bound.
        var reopened = f.Collector; var retry = await reopened.CollectAsync(f.RotationId, routes);
        Check(Has(reopened.Recheck(retry), RotationAcceptanceBlock.ContactFailed, f.B.State.HostId) && Has(reopened.Recheck(retry), RotationAcceptanceBlock.ContactFailed, f.C.State.HostId));
        Check(f.A.State.Count("HostCredentialRotationPeers") == 2); f.Preserved();
    }
    public static async Task MissingAddressUnreachableAndFreshRetry()
    {
        await using var f = new Peers(); await f.Start(); var collector = f.Collector;
        var routes = f.Routes.ToDictionary(p => p.Key, p => p.Value); routes.Remove(f.B.State.HostId);
        var round = await collector.CollectAsync(f.RotationId, routes);
        Check(Has(collector.Recheck(round), RotationAcceptanceBlock.MissingAddress, f.B.State.HostId));
        var deadline = f.C.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationExpiresUtc;
        routes[f.B.State.HostId] = f.B.Address; await f.B.Stop();
        round = await collector.CollectAsync(f.RotationId, routes);
        Check(Has(collector.Recheck(round), RotationAcceptanceBlock.ContactFailed, f.B.State.HostId));
        await f.B.Start(); round = await collector.CollectAsync(f.RotationId, f.Routes);
        Check(collector.Recheck(round).PeerAcknowledgementsReady && f.C.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationExpiresUtc == deadline); f.Preserved();
    }
    public static async Task MarginLapseAndReceiptRemainBlocked()
    {
        await using var f = new Peers(); await f.Start(); var collector = f.Collector;
        await collector.CollectAsync(f.RotationId, f.Routes); f.B.State.Time.Now += TimeSpan.FromMinutes(29);
        var round = await collector.CollectAsync(f.RotationId, f.Routes);
        Check(Has(collector.Recheck(round), RotationAcceptanceBlock.InsufficientTime, f.B.State.HostId));
        f.B.State.Time.Now += TimeSpan.FromMinutes(1); round = await collector.CollectAsync(f.RotationId, f.Routes);
        Check(Has(collector.Recheck(round), RotationAcceptanceBlock.ReconfirmationRequired, f.B.State.HostId));
        // Synthetic prior promotion receipt slot; no global cutover is claimed.
        f.B.State.Execute("UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint=NULL,PendingRotationExpiresUtc=NULL,PendingReconfirmationRequired=0;");
        round = await collector.CollectAsync(f.RotationId, f.Routes);
        Check(Has(collector.Recheck(round), RotationAcceptanceBlock.PromotionReceiptPending, f.B.State.HostId)); f.Preserved();
    }
    public static async Task PendingAndRecoveryAreNotSilentlyExcluded()
    {
        await using var f = new Peers(); await f.Start();
        f.A.State.Execute($"UPDATE TrustedManagers SET State='PeerBound' WHERE PeerHostId='{f.B.State.HostId:D}'; UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{f.C.State.HostId:D}';");
        var collector = f.Collector; var round = await collector.CollectAsync(f.RotationId, f.Routes); var result = collector.Recheck(round);
        Check(Has(result, RotationAcceptanceBlock.PendingPairing, f.B.State.HostId) && Has(result, RotationAcceptanceBlock.RecoveryRequired, f.C.State.HostId));
        Check(round.Exchanges.Count == 0 && f.A.State.Count("HostCredentialRotationPeers") == 0);
        f.A.State.Time.Now += TimeSpan.FromMinutes(31); f.A.Runtime.Repository.MaintainPendingPairingTrust();
        Check(Has(collector.Recheck(round), RotationAcceptanceBlock.PeerSetChanged));
        f.A.State.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=0 WHERE PeerHostId='{f.C.State.HostId:D}';");
        round = await collector.CollectAsync(f.RotationId, f.Routes);
        Check(collector.Recheck(round).PeerAcknowledgementsReady && round.Exchanges.Count == 1 && f.A.Runtime.Repository.Read(f.B.State.HostId)!.State == "Revoked"); f.Preserved();
    }
    public static async Task LateMembershipAbaAndAbortInvalidateTheRound()
    {
        for (var variant = 0; variant < 3; variant++)
        {
            await using var f = new Peers(); await f.Start(); var altered = 0;
            var fault = new PeerReplyFaultTransport<PeerRotationProposalReply>(new WindowsPeerHttpTransportFactory(f.A.Certificate.Value), "StageRotation", PeerRotationProposalReply.Parser, _ =>
            {
                if (Interlocked.Exchange(ref altered, 1) != 0) return;
                if (variant == 0)
                {
                    var peer = Guid.NewGuid(); f.A.Runtime.Repository.RecordVerifiedBinding(peer, new string('E', 64), f.A.Pin);
                    f.A.State.Execute($"UPDATE TrustedManagers SET State='Active' WHERE PeerHostId='{peer:D}';");
                }
                if (variant == 1) f.A.State.Execute($"UPDATE TrustedManagers SET State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL WHERE PeerHostId='{f.B.State.HostId:D}'; UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{f.B.Pin}' WHERE PeerHostId='{f.B.State.HostId:D}';");
                if (variant == 2) f.A.Runtime.Credentials.AbortRoutineRotation(Owner(f.A), f.RotationId);
            });
            var collector = new RoutineRotationAcceptanceCollector(f.A.Runtime, fault); var round = await collector.CollectAsync(f.RotationId, f.Routes);
            var result = collector.Recheck(round);
            Check(altered == 1 && !result.PeerAcknowledgementsReady && Has(result, variant == 2 ? RotationAcceptanceBlock.ProposalChanged : RotationAcceptanceBlock.PeerSetChanged)); f.Preserved();
        }
    }
    public static async Task ActualPeerKeyPromotionRequiresANewCollection()
    {
        using var next = new PeerTlsTests.Certificate(); await using var f = new Peers(); await f.Start(); var nextPin = WindowsPeerTls.PublicFingerprint(next.Value);
        f.A.State.Execute($"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{nextPin}',PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{f.A.State.Time.Now.AddMinutes(30):O}' WHERE PeerHostId='{f.B.State.HostId:D}';");
        await f.B.Stop(); f.B.State.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{nextPin}' WHERE CredentialRef='current';"); await f.B.Start(next.Value);
        var collector = f.Collector; var round = await collector.CollectAsync(f.RotationId, f.Routes); var result = collector.Recheck(round);
        Check(Has(result, RotationAcceptanceBlock.EvidenceMismatch, f.B.State.HostId) && Has(result, RotationAcceptanceBlock.PeerSetChanged));
        Check(f.A.Runtime.Repository.Read(f.B.State.HostId)!.CurrentFingerprint == nextPin && f.A.State.Count("TrustedManagerCredentialHistory") == 1);
        round = await collector.CollectAsync(f.RotationId, f.Routes); Check(collector.Recheck(round).PeerAcknowledgementsReady); f.Preserved();
    }
    private sealed class Clock(Func<DateTimeOffset> utc) : TimeProvider
    {
        internal long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
        public override DateTimeOffset GetUtcNow() => utc();
    }
    public static async Task ElapsedBoundsScopeAndCancellation()
    {
        await using var f = new Peers(); await f.Start(); var time = new Clock(() => f.A.State.Time.Now);
        var runtime = new PeerSecurityRpcRuntime(f.A.State.Database, f.A.State.HostId, f.A.Runtime.Hook, time);
        var collector = WindowsHostComposition.CreateRotationAcceptanceCollector(runtime, f.A.Certificate.Value);
        var round = await collector.CollectAsync(f.RotationId, f.Routes); Check(collector.Recheck(round).PeerAcknowledgementsReady);
        time.Seconds = 28 * 60; Check(Has(collector.Recheck(round), RotationAcceptanceBlock.InsufficientTime));
        time.Seconds = 0; Check(!collector.Recheck(round).PeerAcknowledgementsReady);
        try { f.Collector.Recheck(round); throw new Exception("Expected collection scope refusal."); } catch (InvalidOperationException) { }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await collector.CollectAsync(f.RotationId, f.Routes, canceled.Token); throw new Exception("Expected canceled collection."); } catch (OperationCanceledException) { }
        Check(f.A.State.Count("HostCredentialRotationPeers") == 2);
        using var interrupted = new CancellationTokenSource();
        var fault = new PeerReplyFaultTransport<PeerRotationProposalReply>(new WindowsPeerHttpTransportFactory(f.A.Certificate.Value), "StageRotation", PeerRotationProposalReply.Parser, _ => interrupted.Cancel());
        var cancelingCollector = new RoutineRotationAcceptanceCollector(runtime, fault);
        var originalB = f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationExpiresUtc;
        var originalC = f.C.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationExpiresUtc;
        try { await cancelingCollector.CollectAsync(f.RotationId, f.Routes, interrupted.Token); throw new Exception("Expected in-flight cancellation."); } catch (OperationCanceledException) { }
        Check(fault.Altered >= 1); // Cancellation follows a real durable peer reply, not just a pre-canceled call.
        var retry = await collector.CollectAsync(f.RotationId, f.Routes);
        Check(collector.Recheck(retry).PeerAcknowledgementsReady && !collector.Recheck(round).PeerAcknowledgementsReady);
        Check(f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationExpiresUtc == originalB && f.C.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationExpiresUtc == originalC);
        f.Preserved();
    }
    public static async Task DisposalDrainsActualTlsTrustCallbacks()
    {
        for (var phase = 0; phase < 2; phase++)
        {
            await using var f = new Peers(); await f.Start();
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Block()
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Fixture callback release timed out.");
            }
            using var connection = new WindowsPeerHttpTransportFactory(f.A.Certificate.Value).Create(pin =>
            {
                if (phase == 0) Block();
                return f.A.Runtime.Authentication.AdmitHandshake(f.B.State.HostId, pin, PeerTrafficPurpose.TrustMaintenance);
            }, actual =>
            {
                if (phase == 1) Block();
                f.A.Runtime.Authentication.Authenticate(f.B.State.HostId, actual.PeerFingerprint, PeerTrafficPurpose.TrustMaintenance);
            });
            using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(f.B.Address, new Grpc.Net.Client.GrpcChannelOptions
            { HttpHandler = connection.Handler, HttpVersion = System.Net.HttpVersion.Version20, HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var call = new PeerSecurityProtocol.PeerSecurityProtocolClient(channel).NegotiateAsync(PeerSecurityRpcRuntime.Hello(f.A.State.HostId), cancellationToken: deadline.Token);
            Task? disposal = null; var waited = false;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                disposal = Task.Run(() => { started.SetResult(); connection.Dispose(); });
                await started.Task; await Task.Delay(100); waited = !disposal.IsCompleted;
            }
            finally
            {
                release.Set();
                if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(5));
                try { await call.ResponseAsync; } catch (Grpc.Core.RpcException) { }
            }
            Check(waited); f.Preserved();
        }
    }
}
