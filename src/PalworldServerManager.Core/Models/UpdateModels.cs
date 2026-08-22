namespace PalworldServerManager.Core.Models;

/// <summary>How this running copy relates to Velopack's install management, and therefore whether it can self-update at all.</summary>
public enum UpdateExecutionMode
{
    /// <summary>Installed via the Velopack Setup.exe; full check/download support.</summary>
    Installed,
    /// <summary>A Velopack-managed portable package; must never try to replace its own running executable.</summary>
    Portable,
    /// <summary>A developer build (bin/Debug, bin/Release) or any other copy Velopack does not manage.</summary>
    Development
}

public enum UpdateChannel
{
    Stable,
    Prerelease
}

public enum UpdateState
{
    Idle,
    Checking,
    UpdateAvailable,
    Downloading,
    ReadyToInstall,
    Failed,
    /// <summary>Committed to restarting: the runtime handoff has been written and the external updater is being launched. This is a terminal in-process state - a successful apply ends with the process exiting, not with a further transition.</summary>
    Applying
}

/// <summary>Manager-owned description of an available release. Never exposes raw Velopack types to callers.</summary>
public sealed class ReleaseInfo
{
    public required string Version { get; init; }
    public string? ReleaseNotes { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset? ReleaseDate { get; init; }

    /// <summary>Opaque backend-specific handle (a Velopack UpdateInfo, for the real backend) needed to actually perform the download. Callers must treat this as a black box.</summary>
    public object? BackendToken { get; init; }
}

public sealed record UpdateCheckResult(bool UpdateAvailable, ReleaseInfo? Release);

/// <summary>Immutable snapshot of update state for UI binding/polling.</summary>
public sealed record UpdateStatus(
    UpdateState State,
    UpdateExecutionMode ExecutionMode,
    UpdateChannel Channel,
    string CurrentVersion,
    DateTime? LastCheckedUtc,
    ReleaseInfo? AvailableRelease,
    int DownloadPercent,
    string? ErrorMessage);
