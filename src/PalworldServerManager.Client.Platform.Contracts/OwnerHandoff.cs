using System.Security.Cryptography;

namespace PalworldServerManager.Client.Platform.Contracts;

public enum OwnerHandoffPurpose : byte { Bootstrap = 1, CredentialRotation = 3, Rehome = 4 }

// Client interpretation of the bounded OS-protected artifact, independent of Host assemblies.
public sealed class OwnerHandoff : IDisposable
{
    private readonly byte[] _secret;
    private bool _disposed;
    public Guid HostId { get; }
    public Guid TicketId { get; }
    public OwnerHandoffPurpose Purpose { get; }
    private OwnerHandoff(Guid host, Guid ticket, OwnerHandoffPurpose purpose, byte[] secret)
    { HostId = host; TicketId = ticket; Purpose = purpose; _secret = secret; }
    public static OwnerHandoff Parse(ReadOnlySpan<byte> bytes, Guid expectedHost, Guid expectedTicket)
    {
        if (bytes.Length != 73 || !bytes[..8].SequenceEqual("PSMOH001"u8) || expectedHost == Guid.Empty || expectedTicket == Guid.Empty)
            throw new InvalidDataException("Invalid Owner handoff artifact.");
        var host = new Guid(bytes.Slice(8, 16)); var ticket = new Guid(bytes.Slice(24, 16));
        var purpose = (OwnerHandoffPurpose)bytes[40];
        if (host != expectedHost || ticket != expectedTicket || !Enum.IsDefined(purpose))
            throw new InvalidDataException("Owner handoff identity or purpose mismatch.");
        return new(host, ticket, purpose, bytes[41..].ToArray());
    }
    public byte[] ExportSecretForTransport()
    { ObjectDisposedException.ThrowIf(_disposed, this); return _secret.ToArray(); }
    public override string ToString() => "[REDACTED Owner handoff]";
    public void Dispose() { CryptographicOperations.ZeroMemory(_secret); _disposed = true; }
}
