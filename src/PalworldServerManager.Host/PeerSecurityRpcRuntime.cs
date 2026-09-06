using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.Host;

// Trusted Host composition; caller holds the authoritative lease and borrowed certificate.
public sealed class PeerSecurityRpcRuntime
{
    public Guid HostId { get; }
    internal PeerTrustRepository Repository { get; }
    internal IPeerActivationHook Hook { get; }
    internal PeerTransportAuthentication Authentication { get; }
    public PeerSecurityRpcRuntime(HostDatabase database, Guid hostId, IPeerActivationHook hook, TimeProvider? time = null)
    {
        if (hostId == Guid.Empty) throw new ArgumentException("Host identity required.");
        HostId = hostId; Hook = hook ?? throw new ArgumentNullException(nameof(hook));
        Repository = new(database, hostId, time); Authentication = new(Repository, time);
    }
    internal static PeerHello Hello(Guid hostId)
    {
        var hello = new PeerHello { Host = new() { HostId = hostId.ToString("D") },
            Handshake = new() { Protocol = new() { Major = 1, Minor = 2 }, ProductVersion = "0.5.0-astra" } };
        hello.Handshake.Capabilities.Add(FeatureCapability.PeerTrustActivation); return hello;
    }
    internal Func<ConnectionDelegate, ConnectionDelegate> BindConnection(X509Certificate2 certificate)
    {
        var local = WindowsPeerTls.PublicFingerprint(certificate);
        return next => async connection =>
        {
            var peer = WindowsPeerEndpoint.ReadRemoteFingerprint(connection);
            await using var state = new PeerSecurityRpcConnection(local, peer);
            connection.Features.Set(state); await next(connection).ConfigureAwait(false);
        };
    }
}

internal sealed class PeerSecurityRpcConnection(string localFingerprint, string peerFingerprint) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool closed;
    internal string LocalFingerprint { get; } = localFingerprint;
    internal string PeerFingerprint { get; } = peerFingerprint;
    internal Guid PeerId { get; set; }
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
        try { closed = true; Protocol = null; PeerId = Guid.Empty; }
        finally { gate.Release(); }
    }
}
