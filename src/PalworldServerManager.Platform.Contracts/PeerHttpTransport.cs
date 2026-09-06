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
    IPeerHttpTransport Create(Func<string, bool> acceptsServerPin);
}
