namespace PalworldServerManager.Client.Platform.Contracts;

/// <summary>
/// CLIENT-SIDE ONLY interactive shell integration (SS11, SS11a, PLATFORM-002).
///
/// This interface lives in the client-side tree, which Host has no reference to - so a Host-side
/// caller is a COMPILE ERROR rather than something a reviewer has to catch. There is deliberately
/// no Host-side shell interface at all.
///
/// It opens an already-validated LOCAL directory. It never executes arbitrary commands, never
/// resolves a remote ServerRef, never opens a remote path, and does not implement SS11a's
/// Host-side path-authorization RPC (that is Host-side, and belongs to a later slice).
/// </summary>
public interface IClientShellIntegration
{
    Task OpenLocalFolderAsync(string localDirectoryPath, CancellationToken ct = default);
}

/// <summary>Process-launch seam so self-tests never actually spawn Explorer.</summary>
public interface IShellProcessLauncher
{
    void LaunchFolder(string localDirectoryPath);
}
