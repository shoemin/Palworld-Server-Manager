using System.Security.Cryptography;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

// Secret code is returned exactly to the authenticated Owner over the already-pinned channel
// for out-of-band delivery. RedactedSecret is never raw diagnostic JSON/text.
public sealed record LocalEnrollmentInvitation(Guid TicketId, DateTimeOffset ExpiresUtc, RedactedSecret Code) : IDisposable
{
    public void Dispose() => Code.Dispose();
}

// Online Host-only adapter. Does not provision keys or expose offline bootstrap preparation.
// Production composition supplies native identity solely from its authenticated TLS connection.
public sealed class LocalEnrollmentService(LocalEnrollmentRepository repository, ISecureCredentialStore store,
    Guid hostId, TimeProvider? timeProvider = null)
{
    private readonly LocalEnrollmentRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ISecureCredentialStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly Guid _hostId = hostId != Guid.Empty ? hostId : throw new ArgumentException("Host identity required.");
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<LocalEnrollmentInvitation> CreateEnrollmentAsync(LocalPrincipalConnectionAuthentication connection,
        string intendedNativePrincipal, CancellationToken ct = default)
    {
        var actor = connection.GetCurrentPrincipal().MutationActor;
        var id = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var verifier = await ComputeAsync(LocalEnrollmentPurpose.AdditionalPrincipal, id, secret, ct).ConfigureAwait(false);
            var expires = _time.GetUtcNow().AddMinutes(15); ct.ThrowIfCancellationRequested();
            _repository.CreateEnrollment(actor, id, intendedNativePrincipal, verifier, expires);
            return new(id, expires, new RedactedSecret(secret));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public async Task<Guid> CompleteBootstrapAsync(Guid ticketId, string nativePrincipal, ReadOnlyMemory<byte> secret,
        string publicKey, CancellationToken ct = default)
    {
        using var verifier = await ComputeAsync(LocalEnrollmentPurpose.InitialOwner, ticketId, secret, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); return _repository.CompleteBootstrap(ticketId, nativePrincipal, verifier, publicKey);
    }
    public async Task<Guid> CompleteEnrollmentAsync(Guid ticketId, string nativePrincipal, ReadOnlyMemory<byte> code,
        string publicKey, CancellationToken ct = default)
    {
        using var verifier = await ComputeAsync(LocalEnrollmentPurpose.AdditionalPrincipal, ticketId, code, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); return _repository.CompleteEnrollment(ticketId, nativePrincipal, verifier, publicKey);
    }
    public void RevokePrincipal(LocalPrincipalConnectionAuthentication connection, Guid targetId)
        => _repository.RevokePrincipal(connection.GetCurrentPrincipal().MutationActor, targetId);

    private async Task<LocalEnrollmentVerifier> ComputeAsync(LocalEnrollmentPurpose purpose, Guid ticketId,
        ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        var key = await _store.RetrieveAsync(LocalEnrollmentVerifier.KeyName(_hostId), ct).ConfigureAwait(false)
            ?? throw new CryptographicException("The enrollment verifier key is unavailable; offline repair is required.");
        try
        {
            if (key.Length != 32) throw new CryptographicException("The enrollment verifier key is invalid; offline repair is required.");
            return LocalEnrollmentVerifier.Compute(key, _hostId, purpose, ticketId, secret.Span);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
