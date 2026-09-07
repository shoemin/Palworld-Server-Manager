using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

// Construct only in the trusted composition root under the machine lease. Owns the supplied
// certificate from construction, including startup failure. Never deletes protected/native keys.
internal sealed class HostNetworkGeneration(X509Certificate2 certificate) : IAsyncDisposable
{
    private readonly X509Certificate2 certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
    private readonly HostTrafficLifetime traffic = new();
    private readonly object gate = new();
    private readonly List<WebApplication> listeners = [];
    private readonly List<CancellationTokenRegistration> listenerSignals = [];
    private readonly TaskCompletionSource listenerStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task startFinished = Task.CompletedTask;
    private bool sealedConfiguration, closing, ready;
    private PeerPairingRpcRuntime? pairing;
    private PeerActivationRpcClient? activation;
    private PeerPairingRpcClient? pairingClient;
    private PeerRotationStatusRpcClient? status;
    private PeerRotationProposalRpcClient? proposal;
    private PeerRotationReceiptRpcClient? receipt;
    private RoutineRotationAcceptanceCollector? collector;
    private RoutineRotationCutoverCoordinator? cutover;
    private HostCredentialStateRepository? credentialState;

    internal Task ListenerStopped => listenerStopped.Task;
    internal (Uri Peer, Uri Pairing)? Endpoints { get; private set; }
    internal void SetBoundEndpoints(Uri peer, Uri pairingAddress)
    { lock (gate) { if (!ready || closing) throw new InvalidOperationException("Generation is not serving."); Endpoints = (peer, pairingAddress); } }
    internal ConnectionDelegate BindConnection(ConnectionDelegate next) => traffic.BindConnection(next);
    internal void AddListener(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        lock (gate)
        {
            if (sealedConfiguration) throw new InvalidOperationException("Generation configuration is closed.");
            listeners.Add(app);
            listenerSignals.Add(app.Lifetime.ApplicationStopping.Register(() => listenerStopped.TrySetResult()));
        }
    }
    internal void SetPeerWork(PeerSecurityRpcRuntime runtime, PeerPairingRpcRuntime pairingRuntime, IPeerHttpTransportFactory transport)
    {
        ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(pairingRuntime); ArgumentNullException.ThrowIfNull(transport);
        lock (gate)
        {
            if (sealedConfiguration || pairing is not null) throw new InvalidOperationException("Generation peer work is already configured.");
            if (runtime.HostId != pairingRuntime.HostId) throw new ArgumentException("Generation Host identities differ.");
            pairing = pairingRuntime; credentialState = runtime.Credentials;
            activation = new(runtime, transport); pairingClient = new(pairingRuntime, transport);
            status = new(runtime, transport); proposal = new(runtime, transport); receipt = new(runtime, transport); collector = new(runtime, transport);
        }
    }
    internal async Task StartAsync(CancellationToken ct)
    {
        TaskCompletionSource finished;
        lock (gate)
        {
            if (sealedConfiguration) throw new InvalidOperationException("Generation cannot start again.");
            sealedConfiguration = true; finished = new(TaskCreationOptions.RunContinuationsAsynchronously); startFinished = finished.Task;
        }
        try
        {
            await traffic.RunAsync(async token =>
            {
                foreach (var app in listeners) { token.ThrowIfCancellationRequested(); await app.StartAsync(token).ConfigureAwait(false); }
                token.ThrowIfCancellationRequested();
                lock (gate) { if (closing) throw new OperationCanceledException(token); ready = true; }
            }, ct).ConfigureAwait(false);
        }
        finally { finished.TrySetResult(); }
    }
    internal Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        lock (gate) if (!ready || closing) throw new InvalidOperationException("Generation is not serving.");
        return traffic.RunAsync(work, ct);
    }
    internal Task RunAsync(Func<CancellationToken, Task> work, CancellationToken ct = default)
        => RunAsync(async token => { await work(token).ConfigureAwait(false); return true; }, ct);
    private static T Required<T>(T? value) where T : class => value ?? throw new InvalidOperationException("Peer networking is not configured.");
    internal Task<PeerActivationDisposition> ActivateAsync(Guid peer, Uri address, CancellationToken ct = default)
        => RunAsync(token => Required(activation).FinalizeAsync(peer, address, token), ct);
    internal Task<PeerPairingCompletion> PairAsync(Uri address, Guid invitation, RedactedSecret code, CancellationToken ct = default)
        => RunAsync(token => Required(pairingClient).PairAsync(address, invitation, code, token), ct);
    internal Task<PairingInvitation> CreateInvitationAsync(CancellationToken ct = default)
        => RunAsync(_ => Task.FromResult(Required(pairing).CreateInvitation()), ct);
    internal Task CancelInvitationAsync(Guid invitation, CancellationToken ct = default)
        => RunAsync(_ => { Required(pairing).CancelInvitation(invitation); return Task.CompletedTask; }, ct);
    internal Task<PeerRotationStatusExchange> CheckRotationAsync(Guid peer, Uri address, LocalPrincipalMutationActor? owner = null, CancellationToken ct = default)
        => RunAsync(token => Required(status).CheckAsync(peer, address, owner, token), ct);
    internal Task<PeerRotationProposalExchange> StageRotationAsync(Guid peer, Uri address, Guid rotation, CancellationToken ct = default)
        => RunAsync(token => Required(proposal).StageAsync(peer, address, rotation, token), ct);
    internal Task<PeerRotationReceiptExchange> ConfirmRotationAsync(Guid peer, Uri address, CancellationToken ct = default)
        => RunAsync(token => Required(receipt).ConfirmAsync(peer, address, token), ct);
    internal Task<RotationAcceptanceCollection> CollectRotationAsync(Guid rotation, IReadOnlyDictionary<Guid, Uri> addresses, CancellationToken ct = default)
        => RunAsync(token => Required(collector).CollectAsync(rotation, addresses, token), ct);

    // No public snapshots or timeout can manufacture this handoff. The existing coordinator
    // still validates the exact collection, Owner, current proposal and every peer in its transaction.
    internal RoutineRotationCutoverCoordinator QuiescedCutover(IHostRotationMaterial material, ILocalHostTrustPublisher publisher)
    {
        lock (gate)
        {
            if (!stopped.Task.IsCompletedSuccessfully) throw new InvalidOperationException("Generation has not successfully stopped.");
            return cutover ??= new(Required(credentialState), Required(collector), material, publisher);
        }
    }
    internal Task StopAsync()
    {
        bool start;
        lock (gate) { start = !closing; closing = sealedConfiguration = true; ready = false; }
        if (start) _ = StopCoreAsync();
        return stopped.Task;
    }
    private async Task StopCoreAsync()
    {
        var failures = new List<Exception>();
        async Task Capture(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception ex) { lock (failures) if (!failures.Contains(ex)) failures.Add(ex); }
        }
        try
        {
            var drain = traffic.DrainAsync(); // closes admission and cancels in-progress startup/work
            await startFinished.ConfigureAwait(false);
            // Start every stop before waiting for drain: even an abort callback failure must not
            // prevent the other applications from closing their connections.
            var stops = listeners.Select(app => Capture(() => app.StopAsync(CancellationToken.None))).ToArray();
            await Capture(() => drain).ConfigureAwait(false); await Task.WhenAll(stops).ConfigureAwait(false);
            foreach (var app in listeners) await Capture(() => app.DisposeAsync().AsTask()).ConfigureAwait(false);
            await Capture(() => { pairing?.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
            foreach (var signal in listenerSignals) signal.Dispose();
            await Capture(() => traffic.DisposeAsync().AsTask()).ConfigureAwait(false);
            await Capture(() => { certificate.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
            if (failures.Count == 0) stopped.TrySetResult(); else stopped.TrySetException(new AggregateException("Host generation cleanup failed.", failures));
        }
        catch (Exception ex) { stopped.TrySetException(ex); }
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}
