namespace PalworldServerManager.Client.Platform.Contracts;

/// <summary>
/// CONTRACT ONLY in #41 (SS2c, OWNER-002).
///
/// #41 fixes the shape and the OS-ACL'd artifact location; it deliberately does NOT implement
/// reading or consuming an OwnerBootstrapSecret. Consumption is the bootstrap ceremony, which
/// belongs to #42 together with the authenticated local channel it must travel over.
/// </summary>
public interface IOwnerBootstrapHandoffReader
{
    /// <summary>
    /// Reads the current OS user's own bootstrap handoff artifact, or null when absent.
    /// Implementations must never read another user's artifact.
    /// </summary>
    Task<string?> TryReadOwnerBootstrapSecretAsync(CancellationToken ct = default);
}
