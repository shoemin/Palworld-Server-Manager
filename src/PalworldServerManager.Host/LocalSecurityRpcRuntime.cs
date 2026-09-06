using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Contracts;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

// Trusted composition only; caller owns the authoritative Host lease and authenticated listener.
public sealed class LocalSecurityRpcRuntime
{
    public Guid HostId { get; }
    internal LocalEnrollmentService Enrollment { get; }
    internal Func<bool> IsInitialized { get; }
    internal Func<HttpContext, string> NativePrincipal { get; }
    private readonly LocalPrincipalAuthenticationRepository _authentication;
    private readonly Action<LocalAuthenticationFailure> _report;
    public LocalSecurityRpcRuntime(HostDatabase database, Guid hostId, ISecureCredentialStore store,
        Func<HttpContext, string> nativePrincipal, Action<LocalAuthenticationFailure> report, TimeProvider? time = null)
    {
        if (hostId == Guid.Empty) throw new ArgumentException("Host identity required.");
        HostId = hostId; NativePrincipal = nativePrincipal ?? throw new ArgumentNullException(nameof(nativePrincipal));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _authentication = new(database); Enrollment = new(new LocalEnrollmentRepository(database, hostId, time), store, hostId, time);
        var state = new HostCredentialStateRepository(database, hostId); IsInitialized = () => state.Read().Initialized;
    }
    // Install after TLS and before HTTP in the listener pipeline. State has the connection's
    // actual lifetime, never a request lifetime or an attacker-selected session identifier.
    public ConnectionDelegate BindConnection(ConnectionDelegate next) => async connection =>
    {
        await using var state = new LocalSecurityRpcConnection(this);
        connection.Features.Set(state);
        await next(connection).ConfigureAwait(false);
    };
    internal LocalPrincipalConnectionAuthentication Authentication(string native) => new(_authentication, HostId, native, _report);
}

internal sealed class LocalSecurityRpcConnection(LocalSecurityRpcRuntime runtime) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _native;
    private bool _closed;
    internal LocalPrincipalConnectionAuthentication Authentication { get; private set; } = null!;
    internal NegotiatedProtocol? Protocol { get; set; }
    internal bool NegotiationAttempted { get; set; }
    internal string Native => _native ?? throw new InvalidOperationException("Native connection identity unavailable.");
    internal async Task<T> Invoke<T>(string native, Func<LocalSecurityRpcConnection, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_native is null) { Authentication = runtime.Authentication(native); _native = native; }
            else if (_native != native) throw new System.Security.Authentication.AuthenticationException("Native connection identity changed.");
            ct.ThrowIfCancellationRequested(); return await action(this).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _closed = true; Authentication?.Dispose(); Protocol = null; }
        finally { _gate.Release(); }
    }
}
