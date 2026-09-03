namespace PalworldServerManager.Platform.Contracts;

/// <summary>
/// Host boot-start policy (SS2). Maps directly onto the Windows service start type and nothing
/// else. Deliberately NOT conflated with the Avalonia client's own per-user login-start
/// (IClientLoginStartPlatform), which is a separate mechanism with no shared state.
/// </summary>
public interface IBootStartPlatform
{
    Task<bool> IsBootStartEnabledAsync(CancellationToken ct = default);

    /// <summary>enabled -> automatic service start; disabled -> demand/manual start.</summary>
    Task SetBootStartEnabledAsync(bool enabled, CancellationToken ct = default);
}
