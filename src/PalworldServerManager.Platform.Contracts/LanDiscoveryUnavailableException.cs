namespace PalworldServerManager.Platform.Contracts;

// A known platform availability failure, surfaced by an owned discovery operation after cleanup.
// A future owner may retry later; this does not promise recovery or permit a routing fallback.
public sealed class LanDiscoveryUnavailableException(Exception innerException)
    : IOException("LAN discovery is currently unavailable.", innerException ?? throw new ArgumentNullException(nameof(innerException)));
