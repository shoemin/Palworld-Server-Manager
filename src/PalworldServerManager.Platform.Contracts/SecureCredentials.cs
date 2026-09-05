using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalworldServerManager.Platform.Contracts;

/// <summary>Host-side opaque key/value store. Caller owns the machine-wide Host/offline lease.
/// Keys are stable case-sensitive identifiers, not paths or secrets. Missing reads return null;
/// missing deletes succeed. Corrupt/unavailable values fail closed, never return empty success.
/// Caller owns and must clear input and returned secret buffers. Implementations never log them.</summary>
public interface ISecureCredentialStore
{
    Task StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct = default);
    Task<byte[]?> RetrieveAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>Secret-marked diagnostic value: formatting, destructuring public properties and
/// JSON expose no material. Explicit copying is only for cryptographic consumers, never logging.
/// This cannot protect raw buffers that a caller deliberately extracts and logs.</summary>
[JsonConverter(typeof(RedactedSecretJsonConverter))]
public sealed class RedactedSecret : IDisposable
{
    private byte[]? _bytes;
    public RedactedSecret(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();
    public byte[] CopyBytes() => (_bytes ?? throw new ObjectDisposedException(nameof(RedactedSecret))).ToArray();
    public override string ToString() => "[REDACTED]";
    public void Dispose() { if (_bytes is { } bytes) CryptographicOperations.ZeroMemory(bytes); _bytes = null; }
}

public sealed class RedactedSecretJsonConverter : JsonConverter<RedactedSecret>
{
    public override RedactedSecret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new JsonException("Secret values cannot be restored from diagnostic JSON.");
    public override void Write(Utf8JsonWriter writer, RedactedSecret value, JsonSerializerOptions options)
        => writer.WriteStringValue("[REDACTED]");
}
