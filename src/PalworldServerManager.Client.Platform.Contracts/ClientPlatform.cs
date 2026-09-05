namespace PalworldServerManager.Client.Platform.Contracts;

public enum HostActivationStatus { AlreadyRunning, StartRequested, Stopped, AccessDenied, ServiceMissing, Failed }
public readonly record struct HostActivationResult(HostActivationStatus Status);

// Query/start eligibility is neither enrollment nor Host authentication. An endpoint that
// fails authentication must not be reclassified as dormant by the later #42 connection flow.
public interface IHostActivation
{
    Task<HostActivationResult> IsHostRunningAsync(CancellationToken ct = default);
    Task<HostActivationResult> RequestStartAsync(CancellationToken ct = default);
}

public sealed record ClientLaunchTarget(string ExecutablePath, IReadOnlyList<string> Arguments);
public interface IClientLoginStartPlatform
{
    Task SetEnabledAsync(bool enabled, ClientLaunchTarget target, CancellationToken ct = default);
    Task<bool> IsEnabledAsync(CancellationToken ct = default);
}
public interface IClientShellIntegration
{
    Task OpenClientDiagnosticsAsync(CancellationToken ct = default);
    Task OpenAuthorizedLocalDirectoryAsync(string localDirectory, CancellationToken ct = default);
}

// Opaque algorithm-specific material. #42 must inject its selected production generator;
// there is deliberately no algorithm choice or default generator in this foundation.
public sealed class LocalPrincipalKeyPair(byte[] publicKey, byte[] privateKey)
{
    public byte[] PublicKey { get; } = publicKey;
    public byte[] PrivateKey { get; } = privateKey;
}
public interface ILocalPrincipalKeyGenerator
{
    LocalPrincipalKeyPair Generate();
}
public sealed class LocalPrincipalClientCredential(Guid principalId, LocalPrincipalKeyPair keyPair)
{
    public Guid LocalPrincipalId { get; } = principalId;
    public LocalPrincipalKeyPair KeyPair { get; } = keyPair;
}
public interface ILocalPrincipalCredentialStore
{
    Task<bool> HasCredentialAsync(CancellationToken ct = default);
    Task<LocalPrincipalKeyPair> CreateAndStoreAsync(CancellationToken ct = default);
    Task BindPrincipalIdAsync(Guid localPrincipalId, CancellationToken ct = default);
    Task<LocalPrincipalClientCredential?> LoadAsync(CancellationToken ct = default);
    Task DeleteAsync(CancellationToken ct = default);
}

// Contract only: #42 owns protected handoff consumption and Host authentication.
public interface IOwnerBootstrapHandoffReader
{
    Task<byte[]?> ReadAsync(CancellationToken ct = default);
    Task DeleteAsync(CancellationToken ct = default);
}
