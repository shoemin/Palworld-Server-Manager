namespace PalworldServerManager.Platform.Contracts;

public enum HostServiceState
{
    NotInstalled,
    Stopped,
    StartPending,
    Running,
    StopPending,
    Other,
}

public enum HostServiceStartMode
{
    /// <summary>Host boot-start OFF - the accepted desktop default (SS2).</summary>
    Manual,

    /// <summary>Host boot-start ON.</summary>
    Automatic,

    Disabled,
}

public sealed record HostServiceStatus(HostServiceState State, HostServiceStartMode StartMode, string? AccountName);

/// <summary>
/// Options for provisioning the Host service. The executable path is supplied BY THE CALLER -
/// #41 deliberately does not choose an installation directory, copy binaries, or decide packaging
/// layout; SCM provisioning wraps an executable that is already present.
/// </summary>
public sealed record HostServiceInstallOptions(
    string ExecutablePath,
    string? Arguments = null,
    HostServiceStartMode StartMode = HostServiceStartMode.Manual,
    string? ActivationGroupName = null);

/// <summary>
/// Full PRIVILEGED administrative lifecycle for the machine-wide Host service (SS2, SS11).
/// Deliberately separate from the ordinary-client <c>IHostActivation</c> seam, which exposes only
/// bounded query/start - this interface is for installer/administrative tooling and requires
/// actual Administrator privilege.
/// </summary>
public interface IHostServiceLifecycle
{
    Task<HostServiceStatus> QueryStatusAsync(CancellationToken ct = default);

    Task InstallAsync(HostServiceInstallOptions options, CancellationToken ct = default);

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes the SERVICE REGISTRATION only. It never deletes the authoritative Host data root or
    /// database - "uninstall service registration" is not "delete Manager state".
    /// </summary>
    Task UninstallAsync(CancellationToken ct = default);
}
