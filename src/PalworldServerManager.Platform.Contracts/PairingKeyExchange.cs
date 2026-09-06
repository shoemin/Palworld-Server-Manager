namespace PalworldServerManager.Platform.Contracts;

public enum PairingRole { Initiator = 0, Responder = 1 }
public enum PairingExchangeState { Created, AwaitingConfirmation, Confirmed, Failed, Disposed }

/// Host-side platform boundary. Pairing never creates management authority.
public interface IPairingKeyExchange : IDisposable
{
    PairingExchangeState State { get; }
    byte[] InitialMessage { get; }
    // Initiator returns its confirmation; responder returns empty until ConfirmPeer succeeds.
    byte[] ReceivePeerMessage(byte[] message, CancellationToken cancellationToken = default);
    // Responder releases its confirmation only after verifying the initiator. Initiator returns empty.
    byte[] ConfirmPeer(byte[] confirmation, CancellationToken cancellationToken = default);
    byte[] CreateIdentityBinding(Guid hostId, byte[] publicCredential, CancellationToken cancellationToken = default);
    VerifiedPairingIdentity VerifyIdentityBinding(byte[] message, CancellationToken cancellationToken = default);
}

/// Verified cryptographic identity only; neither a persisted pin nor an authorization grant.
public sealed class VerifiedPairingIdentity
{
    private readonly byte[] credential;
    public Guid HostId { get; }
    public byte[] PublicCredential => credential.ToArray();
    public VerifiedPairingIdentity(Guid hostId, byte[] publicCredential)
    { HostId = hostId; credential = publicCredential.ToArray(); }
}
