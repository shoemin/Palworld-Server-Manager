namespace PalworldServerManager.Client.Platform.Contracts;

/// <summary>
/// Per-user "start this CLIENT at sign-in" mechanism. Entirely separate from Host boot-start
/// (IBootStartPlatform, Host-side) - different scope, different mechanism, no shared state.
///
/// The launch command is supplied BY THE CALLER. #41 does not resolve a packaged executable path:
/// Velopack installs into versioned directories, so persisting a resolved path here would break
/// after the next update. Packaging owns what the stable command is.
/// </summary>
public interface IClientLoginStartPlatform
{
    Task<bool> IsLoginStartEnabledAsync(CancellationToken ct = default);

    Task SetLoginStartAsync(bool enabled, string launchCommand, CancellationToken ct = default);
}
