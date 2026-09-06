using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;

namespace PalworldServerManager.Client.Platform.Contracts;

// Client-side interpretation of the versioned public artifact. No Host-side dependency.
public sealed class LocalHostTrustAnchor
{
    public Guid HostId { get; }
    public string CurrentFingerprint { get; }
    public string? PendingFingerprint { get; }
    public Guid? PendingRotationId { get; }
    private LocalHostTrustAnchor(Guid id, string current, string? pending, Guid? rotation)
    { HostId = id; CurrentFingerprint = current; PendingFingerprint = pending; PendingRotationId = rotation; }
    public static LocalHostTrustAnchor Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            if (json.Length is 0 or > 8192) throw new FormatException();
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) throw new FormatException();
            if (!names.SetEquals(["schemaVersion", "hostId", "currentHostCredentialFingerprint", "pendingHostCredentialFingerprint", "pendingRotationId"]) ||
                root.GetProperty("schemaVersion").GetInt32() != 1) throw new FormatException();
            var id = root.GetProperty("hostId").GetGuid();
            static string Fingerprint(JsonElement value)
            {
                var text = value.GetString();
                if (text is null || text.Length != 64 || text.Any(c => !char.IsAsciiHexDigit(c))) throw new FormatException();
                return text.ToUpperInvariant();
            }
            var current = Fingerprint(root.GetProperty("currentHostCredentialFingerprint"));
            var pendingValue = root.GetProperty("pendingHostCredentialFingerprint");
            var pending = pendingValue.ValueKind == JsonValueKind.Null ? null : Fingerprint(pendingValue);
            var rotationValue = root.GetProperty("pendingRotationId");
            Guid? rotation = rotationValue.ValueKind == JsonValueKind.Null ? null : rotationValue.GetGuid();
            if (id == Guid.Empty || rotation == Guid.Empty || (pending is null) != (rotation is null)) throw new FormatException();
            return new(id, current, pending, rotation);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw new LocalHostAuthenticationException("The public Host trust descriptor is invalid.", ex); }
    }
    public bool AcceptsPublicKey(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        var fingerprint = SHA256.HashData(subjectPublicKeyInfo);
        return CryptographicOperations.FixedTimeEquals(fingerprint, Convert.FromHexString(CurrentFingerprint)) ||
            (PendingFingerprint is not null && CryptographicOperations.FixedTimeEquals(fingerprint, Convert.FromHexString(PendingFingerprint)));
    }
}

public interface ILocalHostTrustReader
{
    Task<LocalHostTrustAnchor> ReadAsync(CancellationToken ct = default);
}
public interface ILocalHostHttpTransportFactory
{
    // Exact HTTP/2 requests use https://localhost. Standard TLS authenticates the Host before
    // HttpClient sends any HTTP headers or body; no raw connection is exposed to the caller.
    HttpMessageHandler CreateHandler(Guid expectedHostId);
}
public sealed class LocalHostAuthenticationException(string message, Exception? inner = null) : AuthenticationException(message, inner);
public sealed class LocalHostTrustUnavailableException(string message) : IOException(message);
public sealed class LocalHostEndpointUnavailableException(string message, Exception? inner = null) : IOException(message, inner);
