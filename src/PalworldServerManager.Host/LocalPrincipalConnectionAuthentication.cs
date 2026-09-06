using System.Security.Authentication;
using System.Security.Cryptography;
using PalworldServerManager.Contracts;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

public enum LocalAuthenticationFailure
{
    InactiveOrUnknownPrincipal, HostIdentityMismatch, NativeIdentityMismatch,
    InvalidPublicKey, MissingOrExpiredChallenge, InvalidSignature, CredentialChanged
}

// Identity evidence only; capabilities and transactional authorization remain separate.
public sealed class AuthenticatedLocalPrincipal
{
    public Guid LocalPrincipalId { get; }
    public bool IsOwner { get; }
    internal AuthenticatedLocalPrincipal(Guid id, bool owner) { LocalPrincipalId = id; IsOwner = owner; }
}

// Exactly one instance per authenticated TLS connection. Only the trusted platform composition
// supplies nativeOsPrincipalRef; never populate it from a request claim. Dispose on disconnect.
public sealed class LocalPrincipalConnectionAuthentication : IDisposable
{
    private readonly object _gate = new();
    private readonly LocalPrincipalAuthenticationRepository _repository;
    private readonly Guid _hostId;
    private readonly string _nativePrincipal;
    private readonly Action<LocalAuthenticationFailure> _reportFailure;
    private readonly TimeProvider _time;
    private readonly byte[] _connectionBinding = RandomNumberGenerator.GetBytes(32);
    private byte[]? _pendingPayload;
    private long _issuedTimestamp;
    private Guid _pendingPrincipal;
    private string? _pendingKey;
    private Guid _authenticatedPrincipal;
    private string? _authenticatedKey;
    private bool _disposed;
    public LocalPrincipalConnectionAuthentication(LocalPrincipalAuthenticationRepository repository, Guid hostId,
        string nativeOsPrincipalRef, Action<LocalAuthenticationFailure> reportFailure, TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        if (hostId == Guid.Empty || string.IsNullOrWhiteSpace(nativeOsPrincipalRef) || nativeOsPrincipalRef.Length > 256)
            throw new ArgumentException("Trusted Host and native principal identity are required.");
        _hostId = hostId; _nativePrincipal = nativeOsPrincipalRef;
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        _time = timeProvider ?? TimeProvider.System;
    }
    public byte[] IssueChallenge(Guid principalId)
    {
        lock (_gate)
        {
            CheckAlive(); ClearAuthentication(); _pendingPayload = null;
            var record = ReadExact(principalId);
            _pendingPrincipal = principalId; _pendingKey = record.PublicVerificationKey;
            _pendingPayload = LocalPrincipalAuthentication.EncodeChallenge(_hostId, principalId, _connectionBinding, RandomNumberGenerator.GetBytes(32));
            _issuedTimestamp = _time.GetTimestamp();
            return _pendingPayload.ToArray();
        }
    }
    public AuthenticatedLocalPrincipal Authenticate(ReadOnlySpan<byte> signature)
    {
        lock (_gate)
        {
            CheckAlive(); ClearAuthentication();
            var payload = _pendingPayload; _pendingPayload = null; // every attempt consumes its nonce, including failure
            if (payload is null || _time.GetElapsedTime(_issuedTimestamp) >= TimeSpan.FromSeconds(30))
                throw Refuse(LocalAuthenticationFailure.MissingOrExpiredChallenge);
            var record = ReadExact(_pendingPrincipal);
            if (record.PublicVerificationKey != _pendingKey) throw Refuse(LocalAuthenticationFailure.CredentialChanged);
            if (!LocalPrincipalAuthentication.Verify(record.PublicVerificationKey, payload, signature))
                throw Refuse(LocalAuthenticationFailure.InvalidSignature);
            _authenticatedPrincipal = record.LocalPrincipalId; _authenticatedKey = record.PublicVerificationKey;
            return new(record.LocalPrincipalId, record.IsOwner);
        }
    }
    // Fresh read on every use: an existing connection is not a stale authority cache.
    // Mutation handlers must also check authorization inside their own transaction.
    public AuthenticatedLocalPrincipal GetCurrentPrincipal()
    {
        lock (_gate)
        {
            CheckAlive();
            try
            {
                if (_authenticatedKey is null) throw Refuse(LocalAuthenticationFailure.MissingOrExpiredChallenge);
                var record = ReadExact(_authenticatedPrincipal);
                if (record.PublicVerificationKey != _authenticatedKey) throw Refuse(LocalAuthenticationFailure.CredentialChanged);
                return new(record.LocalPrincipalId, record.IsOwner);
            }
            catch { ClearAuthentication(); throw; }
        }
    }
    private LocalPrincipalAuthenticationRecord ReadExact(Guid principalId)
    {
        var record = _repository.TryReadActive(principalId) ?? throw Refuse(LocalAuthenticationFailure.InactiveOrUnknownPrincipal);
        if (record.HostId != _hostId) throw Refuse(LocalAuthenticationFailure.HostIdentityMismatch);
        if (record.OsPrincipalRef != _nativePrincipal) throw Refuse(LocalAuthenticationFailure.NativeIdentityMismatch);
        if (!LocalPrincipalAuthentication.IsValidPublicKey(record.PublicVerificationKey)) throw Refuse(LocalAuthenticationFailure.InvalidPublicKey);
        return record;
    }
    private AuthenticationException Refuse(LocalAuthenticationFailure reason)
    {
        _reportFailure(reason); // enum only: no nonce, signature, key or bearer material
        return new AuthenticationException("The local principal could not be authenticated.");
    }
    private void CheckAlive() { ObjectDisposedException.ThrowIf(_disposed, this); }
    private void ClearAuthentication() { _authenticatedPrincipal = Guid.Empty; _authenticatedKey = null; }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _pendingPayload = null; _pendingKey = null; ClearAuthentication(); }
    }
}
