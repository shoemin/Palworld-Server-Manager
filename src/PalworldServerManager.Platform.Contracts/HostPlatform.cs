namespace PalworldServerManager.Platform.Contracts;

public enum HostServiceState { Stopped, StartPending, Running, StopPending, Other }

// Privileged provisioning only. A supplied executable is already installed; this seam never
// copies binaries, chooses packaging layout, or deletes authoritative Manager state.
public interface IHostServiceLifecycle
{
    Task InstallAsync(string executablePath, CancellationToken ct = default);
    Task UninstallAsync(CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<HostServiceState> GetStateAsync(CancellationToken ct = default);
}

public interface IBootStartPlatform
{
    Task<bool> IsEnabledAsync(CancellationToken ct = default);
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);
}

public interface IHostDataRootPlatform
{
    string GetHostDataRoot();
}
