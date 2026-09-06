using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalworldServerManager.Core.Security;

public enum LocalEnrollmentPurpose { InitialOwner = 1, AdditionalPrincipal = 2, OwnerRotation = 3, OwnerRehome = 4 }

// Secret-marked verifier: diagnostic formatting/JSON/destructuring expose no material.
// Export is explicitly for the authoritative database, never diagnostics.
[JsonConverter(typeof(LocalEnrollmentVerifierJsonConverter))]
public sealed class LocalEnrollmentVerifier : IDisposable
{
    private byte[]? _bytes;
    private LocalEnrollmentVerifier(byte[] bytes) => _bytes = bytes;
    // Shared by online Host and approved offline composition without a Host.Cli -> Host edge.
    public static string KeyName(Guid hostId) => hostId != Guid.Empty
        ? "local-enrollment-verifier-v1-" + hostId.ToString("N") : throw new ArgumentException("Host identity required.");
    public static LocalEnrollmentVerifier Compute(ReadOnlySpan<byte> key, Guid hostId, LocalEnrollmentPurpose purpose,
        Guid ticketId, ReadOnlySpan<byte> secret)
    {
        if (key.Length != 32 || hostId == Guid.Empty || ticketId == Guid.Empty || !Enum.IsDefined(purpose) || secret.Length > 4096)
            throw new ArgumentException("Invalid enrollment verifier input.");
        var prefix = Encoding.ASCII.GetBytes($"PSM.LOCAL.ENROLLMENT/1\n{hostId:D}\n{(int)purpose}\n{ticketId:D}\n");
        var input = new byte[prefix.Length + secret.Length];
        prefix.CopyTo(input, 0); secret.CopyTo(input.AsSpan(prefix.Length));
        try { return new(HMACSHA256.HashData(key, input)); }
        finally { CryptographicOperations.ZeroMemory(input); }
    }
    public string ExportForPersistence() => Convert.ToBase64String(Bytes());
    public bool MatchesPersisted(string encoded)
    {
        var bytes = Bytes();
        if (encoded is null || encoded.Length != 44) return false;
        Span<byte> expected = stackalloc byte[32];
        return Convert.TryFromBase64String(encoded, expected, out var length) && length == 32 &&
            Convert.ToBase64String(expected) == encoded && CryptographicOperations.FixedTimeEquals(bytes, expected);
    }
    private byte[] Bytes() => _bytes ?? throw new ObjectDisposedException(nameof(LocalEnrollmentVerifier));
    public override string ToString() => "[REDACTED]";
    public void Dispose() { if (_bytes is { } bytes) CryptographicOperations.ZeroMemory(bytes); _bytes = null; }
}

public sealed class LocalEnrollmentVerifierJsonConverter : JsonConverter<LocalEnrollmentVerifier>
{
    public override LocalEnrollmentVerifier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new JsonException("Enrollment verifiers cannot be restored from diagnostic JSON.");
    public override void Write(Utf8JsonWriter writer, LocalEnrollmentVerifier value, JsonSerializerOptions options)
        => writer.WriteStringValue("[REDACTED]");
}

// Supplied by trusted Host authentication composition, never deserialized from a request.
// A repository rechecks all fields and Owner status inside its writer transaction.
public sealed record LocalPrincipalMutationActor(Guid HostId, Guid LocalPrincipalId, string OsPrincipalRef, string PublicVerificationKey);
