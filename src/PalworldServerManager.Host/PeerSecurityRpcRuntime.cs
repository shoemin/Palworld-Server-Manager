using Microsoft.AspNetCore.Connections;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

// Trusted Host composition; caller holds the authoritative lease and borrowed certificate.
public sealed class PeerSecurityRpcRuntime
{
    public Guid HostId { get; }
    internal PeerTrustRepository Repository { get; }
    internal HostCredentialStateRepository Credentials { get; }
    internal TimeProvider Clock { get; }
    internal IPeerActivationHook Hook { get; }
    internal PeerTransportAuthentication Authentication { get; }
    internal PeerPermissionDispatcher Permissions { get; }
    internal PeerUnpairReceiver Unpair { get; }
    internal PeerRecoveryCompletionReceiver Recovery { get; }
    internal PeerUnpairCoordinator? UnpairNotifications {get;private set;}
    internal void ConfigureUnpairNotifications(PeerUnpairCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        if(UnpairNotifications is not null)throw new InvalidOperationException("Unpair coordinator is already configured.");
        UnpairNotifications=PeerUnpairCoordinator.MatchHost(coordinator,HostId);
    }
    private readonly GrantPolicyRepository grants;
    internal void RequireCommittedUnpair(PeerGrantMutationActor original,PeerTrustRevocationResult revoked,CancellationToken ct)
        =>grants.RequireCommittedUnpair(original,revoked,ct);
    public PeerSecurityRpcRuntime(HostDatabase database, Guid hostId, IPeerActivationHook hook, TimeProvider? time = null)
    {
        if (hostId == Guid.Empty) throw new ArgumentException("Host identity required.");
        HostId = hostId; Hook = hook ?? throw new ArgumentNullException(nameof(hook));
        Repository = new(database, hostId, time); Authentication = new(Repository, time);
        Credentials = new(database, hostId);
        Clock = time ?? TimeProvider.System;
        grants = new GrantPolicyRepository(database, hostId, time);
        Permissions = new(this, grants); Unpair = new(this, grants); Recovery = new(this, grants);
    }
    internal static PeerHello Hello(Guid hostId)
    {
        var hello = new PeerHello { Host = new() { HostId = hostId.ToString("D") },
            Handshake = new() { Protocol = new() { Major = 1, Minor = 13 }, ProductVersion = "0.5.0-astra" } };
        hello.Handshake.Capabilities.Add(FeatureCapability.PeerTrustActivation);
        hello.Handshake.Capabilities.Add(FeatureCapability.PeerRotationStatus);
        hello.Handshake.Capabilities.Add(FeatureCapability.PeerRotationProposal);
        hello.Handshake.Capabilities.Add(FeatureCapability.PeerRotationReceipt);
        hello.Handshake.Capabilities.Add(FeatureCapability.PeerCurrentCredentialConfirmation);
        hello.Handshake.Capabilities.Add(FeatureCapability.PeerUnpair); return hello;
    }
    internal static PeerHello RecoveryHello(Guid hostId)
    {
        var hello=Hello(hostId);hello.Handshake.Capabilities.Clear();
        hello.Handshake.Capabilities.Add(FeatureCapability.PeerRecoveryCompletion);return hello;
    }
    internal Func<ConnectionDelegate, ConnectionDelegate> BindConnection(string local, Func<ConnectionContext, string> readRemoteFingerprint)
    {
        return next => async connection =>
        {
            var peer = readRemoteFingerprint(connection);
            await using var state = new PeerSecurityRpcConnection(this, local, peer);
            connection.Features.Set(state); await next(connection).ConfigureAwait(false);
        };
    }
}

internal sealed class PeerSecurityRpcConnection(PeerSecurityRpcRuntime runtime, string localFingerprint, string peerFingerprint) : IAsyncDisposable
{
    internal bool BelongsTo(PeerSecurityRpcRuntime expected) => ReferenceEquals(runtime, expected);
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool closed;
    internal string LocalFingerprint { get; } = localFingerprint;
    internal string PeerFingerprint { get; } = peerFingerprint;
    internal Guid PeerId { get; set; }
    internal long PeerIncarnation { get; set; }
    internal NegotiatedProtocol? Protocol { get; set; }
    internal bool NegotiationAttempted { get; set; }
    internal async Task<T> Invoke<T>(Func<PeerSecurityRpcConnection, T> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(closed, this); ct.ThrowIfCancellationRequested(); return action(this); }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { closed = true; Protocol = null; PeerId = Guid.Empty; PeerIncarnation = 0; }
        finally { gate.Release(); }
    }
}
