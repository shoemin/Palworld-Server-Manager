namespace PalworldServerManager.Platform.Contracts;

// Public connection evidence only. Identity becomes available after completed mutual TLS;
// callers still recheck authoritative peer state. No Host private credential crosses here.
public sealed record PeerTlsConnectionIdentity(string LocalFingerprint, string PeerFingerprint);
public interface IPeerHttpTransport : IDisposable
{
    HttpMessageHandler Handler { get; }
    PeerTlsConnectionIdentity Identity { get; }
}
public interface IPeerHttpTransportFactory
{
    // Optional observation runs only after completed mutual TLS and before HTTP is released.
    // The pin-admission callback can run before proof; it must remain read-only.
    IPeerHttpTransport Create(Func<string, bool> acceptsServerPin, Action<PeerTlsConnectionIdentity>? observed = null);
}
