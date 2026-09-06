using System.Text.Json;

namespace PalworldServerManager.Platform.Contracts;

// Public derived artifact. The trusted caller derives it from authoritative identity/rotation
// state while holding the Host/offline lease; the publisher never chooses authority or rotation.
public sealed record LocalHostTrustPublication(Guid HostId, string CurrentHostCredentialFingerprint,
    string? PendingHostCredentialFingerprint = null, Guid? PendingRotationId = null)
{
    public byte[] ToJson()
    {
        if (HostId == Guid.Empty || PendingRotationId == Guid.Empty ||
            (PendingHostCredentialFingerprint is null) != (PendingRotationId is null))
            throw new ArgumentException("Invalid public Host trust publication.");
        static string Fingerprint(string value)
        {
            if (value is null || value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c)))
                throw new ArgumentException("Invalid Host public-key fingerprint.");
            return value.ToUpperInvariant();
        }
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, hostId = HostId,
            currentHostCredentialFingerprint = Fingerprint(CurrentHostCredentialFingerprint),
            pendingHostCredentialFingerprint = PendingHostCredentialFingerprint is null ? null : Fingerprint(PendingHostCredentialFingerprint),
            pendingRotationId = PendingRotationId
        });
    }
}

public interface ILocalHostTrustPublisher
{
    Task PublishAsync(LocalHostTrustPublication publication, CancellationToken ct = default);
}
