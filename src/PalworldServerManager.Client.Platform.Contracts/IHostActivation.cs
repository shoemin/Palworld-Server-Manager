namespace PalworldServerManager.Client.Platform.Contracts;

public enum HostActivationResult
{
    /// <summary>Host was already Running; nothing was requested (idempotent).</summary>
    AlreadyRunning,

    /// <summary>A start was requested, or one was already pending.</summary>
    StartRequested,

    /// <summary>Caller is not a member of the dedicated activation group.</summary>
    AccessDenied,

    /// <summary>No Host service is installed on this machine.</summary>
    ServiceNotInstalled,

    /// <summary>The service exists and the caller may start it, but the start did not succeed.</summary>
    StartFailed,
}

/// <summary>
/// The bounded ordinary-user Host activation seam (SS2a).
///
/// SCOPE, deliberately narrow: this answers only "is the SERVICE running / please start it". It
/// has no notion of an endpoint, a connection, or TLS, so it structurally cannot be used to treat
/// a Host AUTHENTICATION failure as "still starting" - SS3b's distinction (absent/refused endpoint
/// may activate; endpoint present but failing Host authentication is a SECURITY FAILURE) belongs
/// to #42's connection state machine, which consumes this primitive.
///
/// The Windows implementation uses only SERVICE_START + SERVICE_QUERY_STATUS - never stop, pause,
/// reconfigure, or delete - and never exposes a raw SCM handle through this contract.
/// </summary>
public interface IHostActivation
{
    Task<bool> IsHostRunningAsync(CancellationToken ct = default);

    Task<HostActivationResult> RequestStartAsync(CancellationToken ct = default);
}
