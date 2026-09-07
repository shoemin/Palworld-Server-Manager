using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Grpc.Core;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Contracts;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class HostGenerationTransitionTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Generation transition assertion failed."); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Bounded(Task value) => value.WaitAsync(TimeSpan.FromSeconds(15));
    private static async Task Reject<T>(Func<Task> work) where T : Exception
    { try { await Bounded(work()); } catch (T) { return; } throw new Exception("Expected transition refusal: " + typeof(T).Name); }
    private sealed class RefusingFactory : IPairingKeyExchangeFactory
    {
        public IPairingKeyExchange Start(PairingRole role, byte[] code, byte[] nonce, CancellationToken ct = default)
            => throw new CryptographicException("Fixture cannot produce PAKE proof.");
    }
    private sealed class Clock : TimeProvider
    {
        internal long AdvanceTicks;
        public override long GetTimestamp() => base.GetTimestamp() + Interlocked.Read(ref AdvanceTicks);
        internal void Advance(TimeSpan value) => Interlocked.Add(ref AdvanceTicks, checked((long)(value.TotalSeconds * TimestampFrequency)));
    }
    private sealed class Rig : IAsyncDisposable, IHostRotationMaterial, ILocalHostTrustPublisher
    {
        internal readonly PeerSecurityRpcTests.Fixture F = new();
        internal readonly PeerTlsTests.Certificate Next = new();
        internal readonly Clock Time = new();
        internal readonly string Pipe = "PSMTransitions" + Guid.NewGuid().ToString("N");
        internal readonly HostGenerationTransitions Actions;
        internal readonly List<HostNetworkGeneration> Generations = [];
        internal readonly List<X509Certificate2> Borrowed = [];
        internal readonly List<LocalHostTrustPublication> Publications = [];
        internal Func<HostNetworkGeneration, CancellationToken, Task>? AfterStart;
        internal Func<UnverifiedHostAdvertisement, CancellationToken, Task<HostDiscoveryRuntime>>? DiscoveryFactory;
        internal bool FailNewPublication, FailPendingPublication, FailReconcile;
        internal int Starts, Reconciliations, FailStartNumber;
        internal HostCredentialStateRepository State => F.Runtime.Credentials;
        internal LocalPrincipalMutationActor Owner => new(F.State.HostId, F.State.OwnerId, "native-owner", "fixture-public");
        internal string NextPin => WindowsPeerTls.PublicFingerprint(Next.Value);
        internal Uri Address => Actions.Endpoints!.Value.Peer;
        internal Rig()
        {
            F.State.Time.Now = DateTimeOffset.UtcNow;
            Actions = new(State, this, this, async ct =>
            {
                Reconciliations++; if (FailReconcile) throw new IOException("Injected reconciliation failure.");
                var plan = HostTrustPlanning.Build(State.Read()); var p = plan.Publication!;
                // Explicit public-publication fixture; real Windows cache/store reconciliation is qualified separately.
                await PublishAsync(new(p.HostId, p.CurrentFingerprint, p.PendingFingerprint, p.PendingRotationId), ct);
            }, async (snapshot, ct) =>
            {
                Starts++; if (Starts == FailStartNumber) throw new IOException("Injected replacement startup failure.");
                var pin = snapshot.Credentials.Single(c => c.Reference == snapshot.CurrentReference).PublicKeyFingerprint;
                var certificate = new X509Certificate2(pin == F.Pin ? F.Certificate.Value : Next.Value);
                Borrowed.Add(certificate); using var identity = WindowsIdentity.GetCurrent();
                var generation = await WindowsHostComposition.CreateNetworkGenerationAsync(F.State.Database, F.State.HostId,
                    new LocalEnrollmentTests.Store(new byte[32]), identity.User!, identity.User!, certificate, Pipe,
                    new(IPAddress.Loopback, 0), new(IPAddress.Loopback, 0), new RefusingFactory(), F.Runtime.Hook, ct, Time, DiscoveryFactory);
                Generations.Add(generation);
                // The test may hold return after actual startup; if its own callback fails it still owns cleanup.
                try { if (AfterStart is not null) await AfterStart(generation, ct); return generation; }
                catch { await generation.StopAsync(); throw; }
            });
        }
        internal RoutineRotationPreparation Prepare()
        {
            var p = State.PrepareRoutineRotation(Owner, Guid.NewGuid()); State.RecordRoutineRotationMaterial(Owner, p.RotationId, NextPin);
            State.BeginRoutineRotationStaging(Owner, p.RotationId); State.PrepareRoutineRotationProposal(Owner, p.RotationId); return p;
        }
        public Task<string> EnsurePreparedAsync(Guid host, string reference, string? expected, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Check(host == F.State.HostId && expected == NextPin && State.Read().Credentials.Any(c => c.Reference == reference && c.PublicKeyFingerprint == expected)); return Task.FromResult(NextPin); }
        public Task PublishAsync(LocalHostTrustPublication p, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Publications.Add(p);
            if ((FailNewPublication && p.CurrentHostCredentialFingerprint == NextPin) || (FailPendingPublication && p.PendingHostCredentialFingerprint is not null))
                throw new IOException("Injected public trust publication failure.");
            return Task.CompletedTask;
        }
        internal async Task LocalNegotiation(string pin)
        {
            var reader = new LocalSecurityRpcTests.Reader(LocalHostTrustAnchor.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new { schemaVersion = 1, hostId = F.State.HostId, currentHostCredentialFingerprint = pin, pendingHostCredentialFingerprint = (string?)null, pendingRotationId = (Guid?)null })));
            using var local = new LocalSecurityRpcTests.Client(F.State.HostId, Pipe, reader);
            Check((await local.Negotiate()).Host.HostId == F.State.HostId.ToString("D"));
        }
        public async ValueTask DisposeAsync()
        {
            try { await Actions.StopAsync(); }
            finally { Next.Dispose(); await F.DisposeAsync(); }
        }
        internal async Task DisposeFailed()
        { await Reject<AggregateException>(() => Actions.StopAsync()); Next.Dispose(); await F.DisposeAsync(); }
    }
    private static async Task Bind(Rig a, Rig b)
    {
        a.F.Bind(b.F); b.F.Bind(a.F);
        Check(await a.Actions.ActivateAsync(b.F.State.HostId, b.Address) == PeerActivationDisposition.Activated);
    }
    private static Dictionary<Guid, Uri> Routes(Rig peer) => new() { [peer.F.State.HostId] = peer.Address };

    public static async Task ActualCutoverNewTlsAndReceipt()
    {
        await using var a = new Rig(); await using var b = new Rig(); await a.Actions.StartAsync(); await b.Actions.StartAsync(); await Bind(a, b);
        var p = a.Prepare(); var old = a.Generations.Single();
        using var invitation = await a.Actions.CreateInvitationAsync(); await a.Actions.CancelInvitationAsync(invitation.Id);
        Check((await a.Actions.StageRotationAsync(b.F.State.HostId, b.Address, p.RotationId)).PeerHostId == b.F.State.HostId);
        Check(await b.Actions.CheckRotationAsync(a.F.State.HostId, a.Address) == PeerRotationStatusExchange.Unchanged);
        var first = a.Actions.CutOverAsync(a.Owner, p.RotationId, Routes(b));
        var competing = a.Actions.CutOverAsync(a.Owner, p.RotationId, Routes(b));
        var result = await first; await Reject<AuthenticationException>(() => competing);
        Check(result.State == HostCredentialRotationState.CutOver && a.Actions.Phase == HostGenerationPhase.Serving && a.Starts == 2 && a.Reconciliations == 2);
        Check(a.Borrowed[0].Handle == IntPtr.Zero && old.ListenerStopped.IsCompleted);
        await a.LocalNegotiation(a.NextPin);
        Check(await b.Actions.ConfirmRotationAsync(a.F.State.HostId, a.Address) == PeerRotationReceiptExchange.Confirmed);
        Check(await a.Actions.ConfirmCurrentCredentialAsync(b.F.State.HostId, b.Address, p.RotationId));
        Check(b.F.State.Repository.Read(a.F.State.HostId)!.CurrentFingerprint == a.NextPin);
        await Reject<AuthenticationException>(() => a.Actions.CutOverAsync(a.Owner, p.RotationId, Routes(b)));
        Check(a.Starts == 2 && a.State.Read().Credentials.All(c => !c.Retired) && a.F.State.Count("HostCapabilityGrants") == 0);
        var completed=await a.Actions.CompleteRotationAsync(a.Owner,p.RotationId);
        Check(completed.State==HostCredentialRotationState.Completed && a.Starts==3 && a.Reconciliations==3);
        Check(a.Borrowed[1].Handle==IntPtr.Zero && HostTrustPlanning.Build(a.State.Read()).Retire.Contains(p.OldReference));
        await a.LocalNegotiation(a.NextPin);
        Check((await a.Actions.CompleteRotationAsync(a.Owner,p.RotationId)).State==HostCredentialRotationState.Completed && a.Starts==3);

    }
    public static async Task DiscoveryDrainPrecedesCutoverAndFreshGeneration()
    {
        await using var a = new Rig(); var probes = new List<HostGenerationDiscoveryTests.Probe>();
        a.DiscoveryFactory = async (ad, token) =>
        {
            if (probes.Count > 0) Check(probes[^1].Runtime.Completion.IsCompletedSuccessfully);
            var probe = new HostGenerationDiscoveryTests.Probe { BlockSend = probes.Count == 0 }; probes.Add(probe);
            return await probe.CreateAsync(ad, token);
        };
        await a.Actions.StartAsync(); var old = probes.Single();
        try
        {
            await Bounded(old.Sent.Task);
            await old.Receiver.Emit(HostDiscoveryCodec.Encode(old.Advertisement with { ClaimedHostId = Guid.NewGuid() }));
            Check((await a.Actions.DiscoverAsync()).Count == 1);
            var p = a.Prepare(); var cutover = a.Actions.CutOverAsync(a.Owner, p.RotationId, new Dictionary<Guid, Uri>());
            await Bounded(old.Receiver.Disposing.Task);
            Check(!cutover.IsCompleted && a.Starts == 1 && a.State.Read().CurrentReference == p.OldReference && a.Borrowed[0].Handle != IntPtr.Zero);
            old.ReleaseSend.SetResult(); var result = await cutover.WaitAsync(TimeSpan.FromSeconds(15));
            Check(result.State == HostCredentialRotationState.CutOver && a.Starts == 2 && probes.Count == 2 && old.Runtime.Completion.IsCompletedSuccessfully);
            Check(!ReferenceEquals(old.Runtime, probes[1].Runtime) && old.Receiver.Disposes == 1 && a.Borrowed[0].Handle == IntPtr.Zero);
            Check((await a.Actions.DiscoverAsync()).Count == 0 && a.State.Read().Credentials.All(c => !c.Retired));
            await a.LocalNegotiation(a.NextPin); Check(a.F.State.Count("HostCapabilityGrants") == 0);
            Check((await a.Actions.CompleteRotationAsync(a.Owner,p.RotationId)).State==HostCredentialRotationState.Completed);
            Check(probes.Count==3 && probes[1].Runtime.Completion.IsCompletedSuccessfully && a.Borrowed[1].Handle==IntPtr.Zero);
            await a.LocalNegotiation(a.NextPin);

        }
        finally { old.ReleaseSend.TrySetResult(); }
    }
    public static async Task PreflightRefusalLeavesGenerationServing()
    {
        await using var a = new Rig(); await using var b = new Rig(); await a.Actions.StartAsync(); await b.Actions.StartAsync(); var p = a.Prepare();
        await Reject<AuthenticationException>(() => a.Actions.CutOverAsync(a.Owner with { PublicVerificationKey = "stale" }, p.RotationId, Routes(b)));
        a.F.Bind(b.F); b.F.Bind(a.F);
        await Reject<AuthenticationException>(() => a.Actions.CutOverAsync(a.Owner, p.RotationId, Routes(b)));
        await a.Actions.ActivateAsync(b.F.State.HostId, b.Address);
        await Reject<AuthenticationException>(() => a.Actions.CutOverAsync(a.Owner, p.RotationId, new Dictionary<Guid, Uri>()));
        Check(a.Starts == 1 && a.Actions.Phase == HostGenerationPhase.Serving && a.Borrowed.Single().Handle != IntPtr.Zero);
        Check(a.State.Read().CurrentReference == p.OldReference); await a.LocalNegotiation(a.F.Pin);
    }
    public static async Task PublicationFailuresRequireExplicitAuthoritativeRecovery()
    {
        foreach (var afterCommit in new[] { false, true })
        {
            await using var a = new Rig(); await a.Actions.StartAsync(); var p = a.Prepare();
            a.FailNewPublication = afterCommit; a.FailPendingPublication = !afterCommit;
            await Reject<IOException>(() => a.Actions.CutOverAsync(a.Owner, p.RotationId, new Dictionary<Guid, Uri>()));
            Check(a.Actions.Phase == HostGenerationPhase.Quiesced && a.Starts == 1 && a.Borrowed.Single().Handle == IntPtr.Zero);
            Check(a.State.Read().CurrentReference == (afterCommit ? p.NewReference : p.OldReference));
            await Reject<InvalidOperationException>(() => a.Actions.CreateInvitationAsync());
            a.FailNewPublication = a.FailPendingPublication = false; await a.Actions.RecoverAsync();
            Check(a.Starts == 2 && a.Actions.Phase == HostGenerationPhase.Serving);
            await a.LocalNegotiation(afterCommit ? a.NextPin : a.F.Pin);
            Check(a.State.Read().Credentials.All(c => !c.Retired));
        }
    }
    public static async Task FinalOwnerAndAcceptanceChecksSurviveSlowShutdown()
    {
        foreach (var expire in new[] { false, true })
        {
            await using var a = new Rig(); await using var b = new Rig(); await a.Actions.StartAsync(); await b.Actions.StartAsync(); await Bind(a, b);
            var p = a.Prepare(); var canceled = Signal(); var release = Signal();
            var work = a.Generations.Single().RunAsync(async ct => { using var r = ct.Register(() => canceled.TrySetResult()); await release.Task; });
            var cutover = a.Actions.CutOverAsync(a.Owner, p.RotationId, Routes(b));
            try
            {
                await Bounded(canceled.Task); Check(!cutover.IsCompleted && a.Actions.Phase == HostGenerationPhase.Transitioning);
                if (expire) a.Time.Advance(TimeSpan.FromMinutes(29));
                else a.F.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed-owner-key' WHERE IsOwner=1;");
            }
            finally { release.TrySetResult(); await Bounded(work); }
            await Reject<AuthenticationException>(() => cutover);
            Check(a.Actions.Phase == HostGenerationPhase.Quiesced && a.State.Read().CurrentReference == p.OldReference && a.Starts == 1);
            await a.Actions.RecoverAsync(); await a.LocalNegotiation(a.F.Pin);
        }
    }
    public static async Task StopDuringActualStartupClosesReturnedCandidate()
    {
        await using var a = new Rig(); var entered = Signal(); var canceled = Signal(); var release = Signal();
        a.AfterStart = async (_, ct) => { using var r = ct.Register(() => canceled.TrySetResult()); entered.TrySetResult(); await release.Task; };
        var start = a.Actions.StartAsync();
        try
        {
            await Bounded(entered.Task); var stop = a.Actions.StopAsync(); await Bounded(canceled.Task);
            Check(!stop.IsCompleted && a.Borrowed.Single().Handle != IntPtr.Zero);
        }
        finally { release.TrySetResult(); await Reject<OperationCanceledException>(() => start); await Bounded(a.Actions.StopAsync()); }
        Check(a.Actions.Phase == HostGenerationPhase.Stopped && a.Borrowed.Single().Handle == IntPtr.Zero);
        await Reject<OperationCanceledException>(() => a.Actions.StartAsync());
        await Reject<OperationCanceledException>(() => a.Actions.RecoverAsync());
        Check(a.Actions.Endpoints is null);
        using var code = new RedactedSecret(new byte[10]); var address = new Uri("https://127.0.0.1:1"); var peer = Guid.NewGuid();
        await Reject<OperationCanceledException>(() => a.Actions.ActivateAsync(peer, address));
        await Reject<OperationCanceledException>(() => a.Actions.PairAsync(address, peer, code));
        await Reject<OperationCanceledException>(() => a.Actions.PairAsync(address, code));
        await Reject<OperationCanceledException>(() => a.Actions.CreateInvitationAsync());
        await Reject<OperationCanceledException>(() => a.Actions.CancelInvitationAsync(peer));
        await Reject<OperationCanceledException>(() => a.Actions.CheckRotationAsync(peer, address));
        await Reject<OperationCanceledException>(() => a.Actions.StageRotationAsync(peer, address, Guid.NewGuid()));
        await Reject<OperationCanceledException>(() => a.Actions.ConfirmRotationAsync(peer, address));
        await Reject<OperationCanceledException>(() => a.Actions.ConfirmCurrentCredentialAsync(peer, address, Guid.NewGuid()));
        await Reject<OperationCanceledException>(() => a.Actions.CompleteRotationAsync(a.Owner,Guid.NewGuid()));
    }
    public static async Task ConcurrentWorkAndStopWaitForFailureCleanup()
    {
        var a = new Rig(); await a.Actions.StartAsync(); var entered = Signal(); var canceled = Signal(); var release = Signal(); int second = 0;
        var first = a.Actions.RunAsync(async ct =>
        {
            using var r = ct.Register(() => { canceled.TrySetResult(); throw new IOException("Injected action cancellation failure."); });
            entered.TrySetResult(); await release.Task; a.F.State.Execute("CREATE TABLE TransitionFinished (Value INTEGER); INSERT INTO TransitionFinished VALUES(1);"); return true;
        });
        var queued = a.Actions.RunAsync(_ => { Interlocked.Increment(ref second); return Task.FromResult(true); });
        try
        {
            await Bounded(entered.Task); var stops = Enumerable.Range(0, 8).Select(_ => a.Actions.StopAsync()).ToArray();
            Check(stops.All(t => ReferenceEquals(t, stops[0]))); await Bounded(canceled.Task);
            Check(!stops[0].IsCompleted && a.Borrowed.Single().Handle != IntPtr.Zero && second == 0);
        }
        finally { release.TrySetResult(); await Bounded(first); await Reject<OperationCanceledException>(() => queued); }
        await Reject<AggregateException>(() => a.Actions.Completion);
        Check(a.Borrowed.Single().Handle == IntPtr.Zero && second == 0 && a.F.State.Count("TransitionFinished") == 1);
        await a.DisposeFailed();
    }
    public static async Task UnexpectedListenerStopClosesEntireOwner()
    {
        var a = new Rig(); await a.Actions.StartAsync(); await a.Generations.Single().StopAsync();
        await Reject<AggregateException>(() => a.Actions.Completion);
        Check(a.Actions.Phase == HostGenerationPhase.Faulted && a.Borrowed.Single().Handle == IntPtr.Zero);
        await Reject<OperationCanceledException>(() => a.Actions.CreateInvitationAsync());
        await a.DisposeFailed();
    }
    public static async Task ReplacementOrReconciliationFailureNeverRestoresOld()
    {
        foreach (var duringRecovery in new[] { false, true })
        {
            var a = new Rig(); await a.Actions.StartAsync(); var p = a.Prepare();
            if (duringRecovery)
            {
                a.FailNewPublication = true;
                await Reject<IOException>(() => a.Actions.CutOverAsync(a.Owner, p.RotationId, new Dictionary<Guid, Uri>()));
                a.FailNewPublication = false; a.FailReconcile = true;
                await Reject<IOException>(() => a.Actions.RecoverAsync());
            }
            else
            {
                a.FailStartNumber = 2;
                await Reject<IOException>(() => a.Actions.CutOverAsync(a.Owner, p.RotationId, new Dictionary<Guid, Uri>()));
            }
            Check(a.Actions.Phase == HostGenerationPhase.Faulted && a.State.Read().CurrentReference == p.NewReference && a.Borrowed.All(c => c.Handle == IntPtr.Zero));
            await Reject<InvalidOperationException>(() => a.Actions.RecoverAsync());
            await a.DisposeFailed();
        }
    }
    public static async Task AuditCleanupFailurePreventsEveryReplacement()
    {
        var a = new Rig(); await using var b = new Rig(); await a.Actions.StartAsync(); await b.Actions.StartAsync();
        try
        {
            a.F.State.Execute("CREATE TRIGGER transition_audit_failure BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='PairingAttemptFailed' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
            using var invitation = await b.Actions.CreateInvitationAsync();
            await Reject<RpcException>(() => a.Actions.PairAsync(b.Actions.Endpoints!.Value.Pairing, invitation.Code));
            var p = a.Prepare();
            await Reject<AggregateException>(() => a.Actions.CutOverAsync(a.Owner, p.RotationId, new Dictionary<Guid, Uri>()));
            Check(a.Actions.Phase == HostGenerationPhase.Faulted && a.Starts == 1 && a.Reconciliations == 1 && a.State.Read().CurrentReference == p.OldReference);
            Check(a.Borrowed.Single().Handle == IntPtr.Zero && a.Publications.Count == 1);
            await Reject<InvalidOperationException>(() => a.Actions.RecoverAsync());
        }
        finally { await a.DisposeFailed(); }
    }
    public static async Task CompletionPreflightAndReconciliationRecovery()
    {
        await using var a=new Rig();await using var b=new Rig();await a.Actions.StartAsync();await b.Actions.StartAsync();await Bind(a,b);
        var p=a.Prepare();await a.Actions.CutOverAsync(a.Owner,p.RotationId,Routes(b));
        await Reject<AuthenticationException>(()=>a.Actions.CompleteRotationAsync(a.Owner,p.RotationId));
        Check(a.Actions.Phase==HostGenerationPhase.Serving && a.Starts==2 && a.State.Read().Rotations.Single().State==HostCredentialRotationState.CutOver);
        Check(await a.Actions.ConfirmCurrentCredentialAsync(b.F.State.HostId,b.Address,p.RotationId));
        a.FailReconcile=true;await Reject<IOException>(()=>a.Actions.CompleteRotationAsync(a.Owner,p.RotationId));
        Check(a.Actions.Phase==HostGenerationPhase.Quiesced && a.Starts==2 && a.Borrowed.All(c=>c.Handle==IntPtr.Zero));
        Check(a.State.Read().Rotations.Single().State==HostCredentialRotationState.Completed && a.State.Read().CurrentReference==p.NewReference);
        a.FailReconcile=false;await a.Actions.RecoverAsync();await a.LocalNegotiation(a.NextPin);
        Check(a.Starts==3 && (await a.Actions.CompleteRotationAsync(a.Owner,p.RotationId)).State==HostCredentialRotationState.Completed && a.Starts==3);
        Check(HostDatabase.QueryScalarLong(a.F.State.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationCompleted';")==1);
    }
    public static async Task CompletionDrainRechecksOwnerAndRelationship()
    {
        foreach(var ownerChange in new[]{true,false})
        {
            await using var a=new Rig();await using var b=new Rig();await a.Actions.StartAsync();await b.Actions.StartAsync();await Bind(a,b);
            var p=a.Prepare();await a.Actions.CutOverAsync(a.Owner,p.RotationId,Routes(b));
            await a.Actions.ConfirmCurrentCredentialAsync(b.F.State.HostId,b.Address,p.RotationId);
            var canceled=Signal();var release=Signal();
            var work=a.Generations[^1].RunAsync(async ct=>{using var registration=ct.Register(()=>canceled.TrySetResult());await release.Task;});
            var complete=a.Actions.CompleteRotationAsync(a.Owner,p.RotationId);
            try
            {
                await Bounded(canceled.Task);Check(!complete.IsCompleted && a.Actions.Phase==HostGenerationPhase.Transitioning);
                if(ownerChange)a.F.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");
                else a.F.State.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=1; UPDATE TrustedManagers SET PeerRecoveryRequired=0;");
            }
            finally {release.TrySetResult();await Bounded(work);}
            await Reject<AuthenticationException>(()=>complete);
            Check(a.Actions.Phase==HostGenerationPhase.Quiesced && a.Starts==2 && a.State.Read().Rotations.Single().State==HostCredentialRotationState.CutOver);
            Check(HostTrustPlanning.Build(a.State.Read()).Retained.Contains(p.OldReference));await a.Actions.RecoverAsync();await a.LocalNegotiation(a.NextPin);
        }
    }
    public static async Task CompletionAuditCleanupFailureCannotCommit()
    {
        var a=new Rig();await using var b=new Rig();await a.Actions.StartAsync();await b.Actions.StartAsync();
        try
        {
            var p=a.Prepare();await a.Actions.CutOverAsync(a.Owner,p.RotationId,new Dictionary<Guid,Uri>());
            a.F.State.Execute("CREATE TRIGGER completion_audit_failure BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='PairingAttemptFailed' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
            using var invitation=await b.Actions.CreateInvitationAsync();
            await Reject<RpcException>(()=>a.Actions.PairAsync(b.Actions.Endpoints!.Value.Pairing,invitation.Code));
            await Reject<AggregateException>(()=>a.Actions.CompleteRotationAsync(a.Owner,p.RotationId));
            Check(a.Actions.Phase==HostGenerationPhase.Faulted && a.Starts==2 && a.State.Read().Rotations.Single().State==HostCredentialRotationState.CutOver);
            Check(HostTrustPlanning.Build(a.State.Read()).Retained.Contains(p.OldReference));
            bool refused=false;try {a.Generations[^1].QuiescedCompletion();}catch(InvalidOperationException){refused=true;}Check(refused);
        }
        finally {await a.DisposeFailed();}
    }

}
