using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Connections;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

// Trusted composition; caller owns the Host lease, native factory and credential lifetime.
// The required public-event sink is not claimed as durable delivery by this transport unit.
public sealed class PeerPairingRpcRuntime : IDisposable
{
    public Guid HostId { get; }
    internal byte[] PublicCredential => publicCredential.ToArray();
    internal string LocalFingerprint { get; }
    internal IPairingKeyExchangeFactory Factory { get; }
    internal PeerTrustRepository Repository { get; }
    internal PairingAttemptCoordinator Attempts { get; }
    private readonly byte[] publicCredential;
    public PeerPairingRpcRuntime(HostDatabase database, Guid hostId, byte[] publicCredential,
        IPairingKeyExchangeFactory factory, Action<Guid, string> report, TimeProvider? time = null)
    {
        if (hostId == Guid.Empty || publicCredential.Length is 0 or > 1024) throw new ArgumentException("Host public identity required.");
        ArgumentNullException.ThrowIfNull(report);
        HostId = hostId; this.publicCredential = publicCredential.ToArray();
        LocalFingerprint = Convert.ToHexString(SHA256.HashData(publicCredential));
        Factory = factory ?? throw new ArgumentNullException(nameof(factory)); Repository = new(database, hostId, time);
        Attempts = new(factory, (id, outcome) => report(id, outcome.ToString()), time);
    }
    internal static Handshake Hello()
    {
        var hello = new Handshake { Protocol = new() { Major = 1, Minor = 3 }, ProductVersion = "0.5.0-astra" };
        hello.Capabilities.Add(FeatureCapability.PeerPairing); return hello;
    }
    internal PeerBindingResult Store(VerifiedPairingIdentity peer, PeerTlsConnectionIdentity tls)
    {
        var pin = Convert.ToHexString(SHA256.HashData(peer.PublicCredential));
        if (tls.LocalFingerprint != LocalFingerprint || pin != tls.PeerFingerprint) throw new AuthenticationException("Pairing identity does not match TLS.");
        return Repository.RecordVerifiedBinding(peer.HostId, pin, tls.LocalFingerprint);
    }
    internal Func<ConnectionDelegate, ConnectionDelegate> BindConnection(string local,
        Func<ConnectionContext, string> readRemote, Func<ConnectionContext, IPAddress> readSource) => next => async connection =>
    {
        if (local != LocalFingerprint) throw new AuthenticationException();
        var state = new PeerPairingConnection(new(local, readRemote(connection)), readSource(connection));
        connection.Features.Set(state); await next(connection).ConfigureAwait(false);
    };
    public void Dispose() => Attempts.Dispose();
}
internal sealed class PeerPairingConnection(PeerTlsConnectionIdentity identity, IPAddress source)
{
    private int begun;
    internal PeerTlsConnectionIdentity Identity { get; } = identity;
    internal IPAddress Source { get; } = source;
    internal void Begin() { if (Interlocked.Exchange(ref begun, 1) != 0) throw new InvalidOperationException("Use a fresh pairing connection."); }
}
