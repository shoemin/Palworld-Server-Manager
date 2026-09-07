using System.Security.Authentication;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

internal enum HostGenerationPhase { New, Serving, Transitioning, Quiesced, Faulted, Stopped }

// One trusted composition-owned instance per machine lease. Dispatch actions outside the
// generation's incoming RPC scope: cutover must drain that scope before changing Current.
internal sealed class HostGenerationTransitions(HostCredentialStateRepository state, IHostRotationMaterial material,
    ILocalHostTrustPublisher publisher, Func<CancellationToken, Task> reconcile,
    Func<HostCredentialSnapshot, CancellationToken, Task<HostNetworkGeneration>> start) : IAsyncDisposable
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HostNetworkGeneration? current;
    private HostGenerationPhase phase;
    private bool stopRequested;
    private Exception? terminalFailure;
    internal HostGenerationPhase Phase { get { lock (gate) return phase; } }
    internal Task Completion => stopped.Task;
    private void Require(HostGenerationPhase expected)
    {
        lock (gate) if (stopRequested || phase != expected) throw new InvalidOperationException("Host generation action is unavailable.");
    }
    private async Task<T> Serialized<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try { linked.Token.ThrowIfCancellationRequested(); return await action(linked.Token).ConfigureAwait(false); }
        finally { serial.Release(); }
    }
    internal Task StartAsync(CancellationToken ct = default) => Serialized(async token =>
    {
        Require(HostGenerationPhase.New); lock (gate) phase = HostGenerationPhase.Transitioning;
        await StartCurrentAsync(token).ConfigureAwait(false); return true;
    }, ct);
    private Task<T> OnCurrentAsync<T>(Func<HostNetworkGeneration, CancellationToken, Task<T>> work, CancellationToken ct)
        => Serialized(async token =>
        {
            Require(HostGenerationPhase.Serving);
            return await current!.RunAsync(inner => work(current, inner), token).ConfigureAwait(false);
        }, ct);
    internal (Uri Peer, Uri Pairing)? Endpoints
    { get { lock (gate) return !stopRequested && phase == HostGenerationPhase.Serving ? current?.Endpoints : null; } }
    internal Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
        => OnCurrentAsync((_, token) => work(token), ct);
    internal Task<IReadOnlyList<UnverifiedHostEndpoint>> DiscoverAsync(CancellationToken ct = default)
        => OnCurrentAsync((generation, token) => generation.DiscoverAsync(token), ct);
    internal Task<PeerActivationDisposition> ActivateAsync(Guid peer, Uri address, CancellationToken ct = default)
        => OnCurrentAsync((generation, token) => generation.ActivateAsync(peer, address, token), ct);
    internal Task<PeerPairingCompletion> PairAsync(Uri address, Guid invitation, RedactedSecret code, CancellationToken ct = default)
        => OnCurrentAsync((generation, token) => generation.PairAsync(address, invitation, code, token), ct);
    internal Task<PeerPairingCompletion> PairAsync(Uri address, RedactedSecret code, CancellationToken ct = default)
        => OnCurrentAsync((generation, token) => generation.PairAsync(address, code, token), ct);
    internal Task<PairingInvitation> CreateInvitationAsync(CancellationToken ct = default)
        => OnCurrentAsync((generation, token) => generation.CreateInvitationAsync(token), ct);
    internal Task CancelInvitationAsync(Guid invitation, CancellationToken ct = default)
        => OnCurrentAsync(async (generation, token) => { await generation.CancelInvitationAsync(invitation, token).ConfigureAwait(false); return true; }, ct);
    internal Task<PeerRotationStatusExchange> CheckRotationAsync(Guid peer, Uri address, LocalPrincipalMutationActor? owner = null, CancellationToken ct = default)
        => OnCurrentAsync((generation, token) => generation.CheckRotationAsync(peer, address, owner, token), ct);
    internal Task<PeerRotationProposalExchange> StageRotationAsync(Guid peer, Uri address, Guid rotation, CancellationToken ct = default)
        => OnCurrentAsync((generation, token) => generation.StageRotationAsync(peer, address, rotation, token), ct);
    internal Task<PeerRotationReceiptExchange> ConfirmRotationAsync(Guid peer, Uri address, CancellationToken ct = default)
        => OnCurrentAsync((generation, token) => generation.ConfirmRotationAsync(peer, address, token), ct);
    internal Task<bool> ConfirmCurrentCredentialAsync(Guid peer, Uri address, Guid rotation, CancellationToken ct = default)
        => OnCurrentAsync((generation, token) => generation.ConfirmCurrentCredentialAsync(peer, address, rotation, token), ct);
    internal Task<RoutineRotationPreparation> CutOverAsync(LocalPrincipalMutationActor owner, Guid rotation,
        IReadOnlyDictionary<Guid, Uri> addresses, CancellationToken ct = default) => Serialized(async token =>
    {
        Require(HostGenerationPhase.Serving);
        // Authenticate before any peer contacts or disruption, then again at the commit boundary.
        state.InspectRoutineRotationCutover(owner, state.ReadRoutineRotationPeerSet(rotation).Proposal);
        var old = current!;
        var collection = await old.CollectRotationAsync(rotation, addresses, token).ConfigureAwait(false);
        if (!old.AssessRotation(collection).PeerAcknowledgementsReady) throw new AuthenticationException("Rotation peer acceptance is not current.");
        token.ThrowIfCancellationRequested();
        lock (gate) phase = HostGenerationPhase.Transitioning;
        bool drained = false;
        try
        {
            await old.StopAsync().ConfigureAwait(false); drained = true;
            lock (gate) current = null;
            var result = await old.QuiescedCutover(material, publisher).CutOverWhileQuiescedAsync(owner, collection, token).ConfigureAwait(false);
            await StartCurrentAsync(token).ConfigureAwait(false); return result;
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (!drained) { phase = HostGenerationPhase.Faulted; terminalFailure ??= error; }
                else if (phase == HostGenerationPhase.Transitioning) phase = HostGenerationPhase.Quiesced;
            }
            throw;
        }
    }, ct);
    // Explicit recovery after a closed-generation cutover/publication failure. The actual
    // durable Current wins; no retained collection or old-generation handoff is exposed.
    internal Task RecoverAsync(CancellationToken ct = default) => Serialized(async token =>
    {
        Require(HostGenerationPhase.Quiesced); lock (gate) phase = HostGenerationPhase.Transitioning;
        await StartCurrentAsync(token).ConfigureAwait(false); return true;
    }, ct);
    private async Task StartCurrentAsync(CancellationToken ct)
    {
        HostNetworkGeneration? candidate = null;
        try
        {
            ct.ThrowIfCancellationRequested(); await reconcile(ct).ConfigureAwait(false);
            var snapshot = state.Read(); var projection = HostTrustPlanning.Build(snapshot).Publication;
            if (!snapshot.Initialized || projection is null) throw new AuthenticationException("Initialized Host trust is required.");
            candidate = await start(snapshot, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (candidate.HostId != snapshot.HostId || candidate.LocalFingerprint != projection.CurrentFingerprint || candidate.ListenerStopped.IsCompleted)
                throw new AuthenticationException("Started generation does not match current Host trust.");
            lock (gate)
            {
                if (stopRequested) throw new OperationCanceledException(ct);
                current = candidate; phase = HostGenerationPhase.Serving;
            }
            _ = ObserveListenerStopAsync(candidate);
        }
        catch (Exception error)
        {
            Exception failure = error;
            if (candidate is not null)
            {
                try { await candidate.StopAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { failure = new AggregateException("Generation startup and cleanup failed.", error, cleanup); }
            }
            lock (gate)
            {
                phase = HostGenerationPhase.Faulted;
                if (!(stopRequested && failure is OperationCanceledException && ct.IsCancellationRequested)) terminalFailure ??= failure;
            }
            if (!ReferenceEquals(failure, error)) throw failure;
            throw;
        }
    }
    private async Task ObserveListenerStopAsync(HostNetworkGeneration generation)
    {
        await generation.ListenerStopped.ConfigureAwait(false);
        lock (gate)
        {
            if (!ReferenceEquals(current, generation) || phase != HostGenerationPhase.Serving || stopRequested) return;
            terminalFailure ??= new IOException("A Host listener stopped unexpectedly.");
        }
        _ = StopAsync();
    }
    internal Task StopAsync()
    {
        bool begin;
        lock (gate) { begin = !stopRequested; stopRequested = true; }
        if (begin) _ = StopCoreAsync();
        return stopped.Task;
    }
    private async Task StopCoreAsync()
    {
        var failures = new List<Exception>();
        try { await stopping.CancelAsync().ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
        await serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (current is not null)
            {
                try { await current.StopAsync().ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); }
                current = null;
            }
            lock (gate)
            {
                if (terminalFailure is not null && !failures.Contains(terminalFailure)) failures.Add(terminalFailure);
                phase = failures.Count == 0 ? HostGenerationPhase.Stopped : HostGenerationPhase.Faulted;
            }
        }
        finally { serial.Release(); }
        // The cancellation source stays available for deterministic refusal of later calls.
        if (failures.Count == 0) stopped.TrySetResult();
        else stopped.TrySetException(new AggregateException("Host generation transitions stopped with failure.", failures));
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}
