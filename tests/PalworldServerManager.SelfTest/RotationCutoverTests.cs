using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using Fixture = PalworldServerManager.SelfTest.PeerSecurityRpcTests.Fixture;

namespace PalworldServerManager.SelfTest;

internal static class RotationCutoverTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Rotation cutover assertion failed."); }
    private static LocalPrincipalMutationActor Owner(Fixture f) => new(f.State.HostId, f.State.OwnerId, "native-owner", "fixture-public");
    private sealed class Clock(Func<DateTimeOffset> utc) : TimeProvider
    {
        internal long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
        public override DateTimeOffset GetUtcNow() => utc();
    }
    private sealed class Material(Rotation f) : IHostRotationMaterial
    {
        internal Func<string>? Override;
        internal Func<CancellationToken, Task>? BeforeValidate;
        public async Task<string> EnsurePreparedAsync(Guid hostId, string reference, string? expected, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (BeforeValidate is not null) await BeforeValidate(ct);
            Check(hostId == f.A.State.HostId && reference == f.Prepared.NewReference && expected == f.NextPin);
            Check(f.Next.Value.HasPrivateKey);
            return Override is null ? f.NextPin : Override();
        }
    }
    private sealed class Publisher(Rotation f) : ILocalHostTrustPublisher
    {
        internal readonly List<LocalHostTrustPublication> Calls = [];
        internal LocalHostTrustPublication? Last;
        internal Action<LocalHostTrustPublication>? Before, After;
        public Task PublishAsync(LocalHostTrustPublication p, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = f.A.Runtime.Credentials.Read();
            Check(p.HostId == snapshot.HostId);
            Check(p.CurrentHostCredentialFingerprint == snapshot.Credentials.Single(c => c.Reference == snapshot.CurrentReference).PublicKeyFingerprint);
            Calls.Add(p); Before?.Invoke(p); Last = p; After?.Invoke(p); return Task.CompletedTask;
        }
    }
    private sealed class Rotation : IAsyncDisposable
    {
        internal readonly Fixture A = new(), B = new(), C = new();
        internal readonly PeerTlsTests.Certificate Next = new();
        internal string NextPin => WindowsPeerTls.PublicFingerprint(Next.Value);
        internal RoutineRotationPreparation Prepared = null!;
        internal RoutineRotationAcceptanceCollector Collector = null!;
        internal RoutineRotationCutoverCoordinator Coordinator = null!;
        internal Material Material = null!;
        internal Publisher Publisher = null!;
        internal Clock Time = null!;
        internal RotationAcceptanceCollection Round = null!;
        internal async Task Start(bool peers = true)
        {
            if (peers)
            {
                A.Bind(B); A.Bind(C); B.Bind(A); C.Bind(A);
                foreach (var f in new[] { A, B, C }) f.State.Execute("UPDATE TrustedManagers SET State='Active';");
                await B.Start(); await C.Start();
            }
            Prepared = A.Runtime.Credentials.PrepareRoutineRotation(Owner(A), Guid.NewGuid());
            A.Runtime.Credentials.RecordRoutineRotationMaterial(Owner(A), Prepared.RotationId, NextPin);
            A.Runtime.Credentials.BeginRoutineRotationStaging(Owner(A), Prepared.RotationId);
            A.Runtime.Credentials.PrepareRoutineRotationProposal(Owner(A), Prepared.RotationId);
            Time = new(() => A.State.Time.Now);
            Collector = WindowsHostComposition.CreateRotationAcceptanceCollector(new(A.State.Database, A.State.HostId, A.Runtime.Hook, Time), A.Certificate.Value);
            Round = await Collector.CollectAsync(Prepared.RotationId, peers ? new Dictionary<Guid, Uri> { [B.State.HostId] = B.Address, [C.State.HostId] = C.Address } : new Dictionary<Guid, Uri>());
            Check(Collector.Recheck(Round).PeerAcknowledgementsReady);
            Material = new(this); Publisher = new(this); Coordinator = new(A.Runtime.Credentials, Collector, Material, Publisher);
            // A has no listener and its awaited outgoing rounds have disposed all transports.
            // This fixture explicitly supplies quiescence, not an installed service owner.
        }
        internal long Audits => HostDatabase.QueryScalarLong(A.State.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationCutOver';");
        internal void Current(bool cutOver, int peerHostGrants = 0)
        {
            var s = A.Runtime.Credentials.Read();
            Check(s.CurrentReference == (cutOver ? Prepared.NewReference : "current"));
            Check(Audits == (cutOver ? 1 : 0));
            Check(HostDatabase.QueryScalarLong(A.State.Writer, "SELECT COUNT(*) FROM SecureCredentialReferences WHERE ActivatedUtc IS NOT NULL;") == (cutOver ? 2 : 1));
            foreach (var f in new[] { A, B, C }) Check(f.State.Count("HostCapabilityGrants") == (ReferenceEquals(f, B) ? peerHostGrants : 0) && f.State.Count("ServerCapabilityGrants") == 0 && f.State.Count("ActivationRpcEffects") == 0);
            Check(s.Credentials.All(c => !c.Retired));
        }
        public async ValueTask DisposeAsync()
        { try { await A.DisposeAsync(); } finally { try { await B.DisposeAsync(); } finally { try { await C.DisposeAsync(); } finally { Next.Dispose(); } } } }
    }
    public static async Task ActualProposalCutoverNewProofAndReceipts()
    {
        await using var f = new Rotation(); await f.Start();
        f.B.State.Execute($"""
            INSERT INTO HostCapabilityGrants (GrantId,TargetHostId,Capability,GranteeActorKind,GranteePeerHostId,
                GrantedByActorKind,GrantedByLocalPrincipalId,CanDelegate,CanDelegateOnwardDelegation,CreatedUtc)
                VALUES ('custom','{f.B.State.HostId:D}','ViewHost','RemoteManager','{f.A.State.HostId:D}',
                    'LocalPrincipal','{f.B.State.OwnerId:D}',1,0,'preserved');
            """);
        var result = await f.Coordinator.CutOverWhileQuiescedAsync(Owner(f.A), f.Round);
        Check(result.State == HostCredentialRotationState.CutOver && result.NewReference == f.Prepared.NewReference);
        Check(f.Publisher.Calls.Count == 2 && f.Publisher.Calls[0].CurrentHostCredentialFingerprint == f.A.Pin && f.Publisher.Calls[0].PendingHostCredentialFingerprint == f.NextPin && f.Publisher.Calls[0].PendingRotationId == result.RotationId);
        Check(f.Publisher.Last!.CurrentHostCredentialFingerprint == f.NextPin && f.Publisher.Last.PendingRotationId is null && f.Publisher.Last.PendingHostCredentialFingerprint is null);
        f.Current(true, 1);
        await f.A.Start(f.Next.Value);
        foreach (var peer in new[] { f.B, f.C })
        {
            Check(peer.Runtime.Repository.Read(f.A.State.HostId)!.CurrentFingerprint == f.A.Pin);
            await WindowsHostComposition.CreatePeerRotationReceiptClient(peer.Runtime, peer.Certificate.Value).ConfirmAsync(f.A.State.HostId, f.A.Address);
            Check(peer.Runtime.Repository.Read(f.A.State.HostId)!.CurrentFingerprint == f.NextPin && peer.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId is null);
            Check(!peer.Runtime.Repository.RecognizesTransportFingerprint(f.A.Pin));
        }
        Check(HostDatabase.QueryScalarLong(f.B.State.Writer, "SELECT COUNT(*) FROM HostCapabilityGrants WHERE GrantId='custom' AND Capability='ViewHost' AND CanDelegate=1 AND InvalidatedUtc IS NULL;") == 1);
        Check(HostDatabase.QueryScalarLong(f.A.State.Writer, "SELECT COUNT(*) FROM HostCredentialRotationPeers WHERE PromotedUtc IS NOT NULL;") == 2);
        try { f.A.Runtime.Credentials.RecordRetired("current"); throw new Exception("Expected retained Old refusal."); } catch (InvalidOperationException) { }
        f.Current(true, 1);
    }
    public static async Task PublicationTimeTrustOwnerAndProposalChangesRefuseCutover()
    {
        for (var variant = 0; variant < 7; variant++)
        {
            await using var f = new Rotation(); await f.Start();
            f.Publisher.Before = _ =>
            {
                if (variant == 0) f.A.State.Execute($"UPDATE TrustedManagers SET State='PeerBound' WHERE PeerHostId='{f.B.State.HostId:D}';");
                if (variant == 1) f.A.State.Execute($"UPDATE TrustedManagers SET State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL WHERE PeerHostId='{f.B.State.HostId:D}'; UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{f.B.Pin}' WHERE PeerHostId='{f.B.State.HostId:D}';");
                if (variant == 2) f.A.Runtime.Repository.RecordVerifiedBinding(Guid.NewGuid(), new string('E', 64), f.A.Pin);
                if (variant == 3) f.A.State.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=1;");
                if (variant == 4) f.A.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed';");
                if (variant == 5) f.A.Runtime.Credentials.AbortRoutineRotation(Owner(f.A), f.Prepared.RotationId);
                if (variant == 6) f.A.State.Execute("UPDATE HostRotationProposals SET ProposalSequence=ProposalSequence+1;");
            };
            try { await f.Coordinator.CutOverWhileQuiescedAsync(Owner(f.A), f.Round); throw new Exception("Expected changed eligibility refusal."); } catch (AuthenticationException) { }
            f.Current(false);
        }
    }
    public static async Task MarginAndCancellationAreCheckedAfterAudit()
    {
        for (var cancel = 0; cancel < 2; cancel++)
        {
            await using var f = new Rotation(); await f.Start(); var calls = 0; using var ct = new CancellationTokenSource();
            var p = f.Round.Snapshot.Proposal;
            await f.Publisher.PublishAsync(new(p.HostId, p.OldFingerprint, p.NewFingerprint, p.RotationId));
            try
            {
                f.A.Runtime.Credentials.CommitRoutineRotationCutover(Owner(f.A), p, current =>
                {
                    if (++calls == 2) { if (cancel == 1) ct.Cancel(); else f.Time.Seconds = 28 * 60; }
                    return f.Collector.AssessCurrent(f.Round, current).PeerAcknowledgementsReady;
                }, ct.Token);
                throw new Exception("Expected late time/cancellation refusal.");
            }
            catch (AuthenticationException) when (cancel == 0) { }
            catch (OperationCanceledException) when (cancel == 1) { }
            Check(calls == 2); f.Current(false);
        }
    }
    public static async Task AuditRollbackAndSerializedRetry()
    {
        await using var f = new Rotation(); await f.Start();
        f.A.State.Execute("CREATE TRIGGER fail_cutover BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='HostRoutineRotationCutOver' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        try { await f.Coordinator.CutOverWhileQuiescedAsync(Owner(f.A), f.Round); throw new Exception("Expected audit refusal."); } catch (SqliteException) { }
        f.Current(false); Check(f.Publisher.Last!.PendingRotationId == f.Prepared.RotationId);
        f.A.State.Execute("DROP TRIGGER fail_cutover;");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        f.Material.BeforeValidate = async token =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            { entered.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
        };
        var first = f.Coordinator.CutOverWhileQuiescedAsync(Owner(f.A), f.Round);
        Task<RoutineRotationPreparation>[] others = []; var waited = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            others = Enumerable.Range(0, 3).Select(_ => f.Coordinator.CutOverWhileQuiescedAsync(Owner(f.A), f.Round)).ToArray();
            waited = others.All(t => !t.IsCompleted) && calls == 1; f.Current(false);
        }
        finally { release.TrySetResult(); await Task.WhenAll(others.Prepend(first)); }
        var results = await Task.WhenAll(others.Prepend(first));
        Check(waited && calls == 4);
        Check(results.All(r => r.State == HostCredentialRotationState.CutOver)); f.Current(true);
        Check(f.Publisher.Calls.Count == 6); // Failed stage, successful stage+current, three read-only committed retries.
    }
    public static async Task MaterialPublicationFailureAndCommittedRetry()
    {
        for (var variant = 0; variant < 5; variant++)
        {
            await using var f = new Rotation(); await f.Start();
            if (variant == 0) f.Material.Override = () => throw new IOException("Fixture known material unavailable.");
            if (variant == 1) f.Material.Override = () => new string('E', 64);
            f.Publisher.Before = p => { if (variant == 2 || (variant == 3 && p.PendingRotationId is null)) throw new IOException("Fixture publication failure."); };
            f.Publisher.After = p => { if (variant == 4 && p.PendingRotationId is null) throw new IOException("Fixture publication result lost."); };
            try { await f.Coordinator.CutOverWhileQuiescedAsync(Owner(f.A), f.Round); throw new Exception("Expected material/publication refusal."); }
            catch (IOException) { } catch (AuthenticationException) when (variant == 1) { }
            f.Current(variant >= 3);
            if (variant < 2) Check(f.Publisher.Calls.Count == 0);
            f.Material.Override = null; f.Publisher.Before = null; f.Publisher.After = null;
            if (variant >= 3) f.Time.Seconds = 31 * 60; // Already committed; never require recreating old acceptance or roll back Current.
            await f.Coordinator.CutOverWhileQuiescedAsync(Owner(f.A), f.Round); f.Current(true);
            Check(f.Publisher.Last!.CurrentHostCredentialFingerprint == f.NextPin && f.Publisher.Last.PendingRotationId is null);
        }
    }
    public static async Task ReconciliationAfterCommitAndScopeRefusal()
    {
        await using var f = new Rotation(); await f.Start();
        var other = WindowsHostComposition.CreateRotationAcceptanceCollector(f.A.Runtime, f.A.Certificate.Value);
        try { await new RoutineRotationCutoverCoordinator(f.A.Runtime.Credentials, other, f.Material, f.Publisher).CutOverWhileQuiescedAsync(Owner(f.A), f.Round); throw new Exception("Expected wrong collection scope."); } catch (InvalidOperationException) { }
        using var ct = new CancellationTokenSource(); ct.Cancel();
        try { await f.Coordinator.CutOverWhileQuiescedAsync(Owner(f.A), f.Round, ct.Token); throw new Exception("Expected cancellation."); } catch (OperationCanceledException) { }
        Check(f.Publisher.Calls.Count == 0); f.Current(false);
        f.Publisher.Before = p => { if (p.PendingRotationId is null) throw new IOException("Fixture post-commit publication failure."); };
        try { await f.Coordinator.CutOverWhileQuiescedAsync(Owner(f.A), f.Round); throw new Exception("Expected publication refusal."); } catch (IOException) { }
        f.Current(true); f.Publisher.Before = null;
        var retained = Array.Empty<string>(); var retired = 0;
        var reconciler = new HostTrustReconciler(f.A.Runtime.Credentials.Read, (p, token) => f.Publisher.PublishAsync(new(p.HostId, p.CurrentFingerprint, p.PendingFingerprint, p.PendingRotationId), token),
            (refs, _) => { retained = refs.ToArray(); return Task.CompletedTask; }, (_, _) => { retired++; return Task.CompletedTask; }, _ => throw new Exception("Unexpected retirement."));
        await reconciler.ReconcileAsync();
        Check(retained.Contains("current") && retained.Contains(f.Prepared.NewReference) && retired == 0 && f.Publisher.Last!.PendingRotationId is null);
        try { f.A.Runtime.Credentials.AbortRoutineRotation(Owner(f.A), f.Prepared.RotationId); throw new Exception("Expected post-cutover abort refusal."); } catch (AuthenticationException) { }
        f.Current(true);
        await using var empty = new Rotation(); await empty.Start(peers: false);
        await empty.Coordinator.CutOverWhileQuiescedAsync(Owner(empty.A), empty.Round); empty.Current(true);
    }
    public static async Task TransactionTriggerChangesAndInitialOwnerRefusal()
    {
        for (var variant = 0; variant < 2; variant++)
        {
            await using var f = new Rotation(); await f.Start();
            try { await f.Coordinator.CutOverWhileQuiescedAsync(Owner(f.A) with { OsPrincipalRef = "other-native" }, f.Round); throw new Exception("Expected initial Owner refusal."); } catch (AuthenticationException) { }
            Check(f.Publisher.Calls.Count == 0);
            var mutation = variant == 0 ? "UPDATE TrustedManagers SET PeerRecoveryRequired=PeerRecoveryRequired;" : "UPDATE LocalPrincipals SET PublicVerificationKey='changed';";
            f.A.State.Execute("CREATE TRIGGER alter_cutover AFTER INSERT ON AuditEvents WHEN NEW.EventKind='HostRoutineRotationCutOver' BEGIN " + mutation + " END;");
            try { await f.Coordinator.CutOverWhileQuiescedAsync(Owner(f.A), f.Round); throw new Exception("Expected post-audit scope refusal."); } catch (AuthenticationException) { }
            f.Current(false);
            Check(f.Collector.Recheck(f.Round).PeerAcknowledgementsReady); // Revision and Owner mutations roll back with Current/audit.
            f.A.State.Execute("DROP TRIGGER alter_cutover;");
            await f.Coordinator.CutOverWhileQuiescedAsync(Owner(f.A), f.Round); f.Current(true);
        }
    }

}
