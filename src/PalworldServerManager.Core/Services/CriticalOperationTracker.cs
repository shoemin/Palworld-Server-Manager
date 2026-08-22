namespace PalworldServerManager.Core.Services;

public enum CriticalOperationKind
{
    ServerStart,
    ServerSafeStop,
    ServerForceStop,
    SteamCmdProvision,
    Backup,
    Restore,
    LegacyImport,
    PackageExport,
    PackageImport,
    SettingsWrite,
    LanTransferSend,
    LanTransferReceive
}

/// <summary>
/// Tracks Manager-owned operations that a Manager self-update apply/restart must never
/// interrupt. Operations register a scoped lease for their duration; releasing happens via
/// Dispose so an exception mid-operation cannot leave a permanent "busy" flag.
///
/// A running Palworld server is deliberately NOT represented here - "server is running" alone
/// must never block an update apply. Only active transitions/operations do.
/// </summary>
public interface ICriticalOperationTracker
{
    /// <summary>Begins a critical operation. Throws if a Manager update apply has already committed to restarting.</summary>
    IDisposable Begin(CriticalOperationKind kind, string? detail = null);

    bool IsBusy { get; }
    IReadOnlyList<string> ActiveOperations { get; }

    /// <summary>
    /// Atomically checks that no critical operation is currently active and, if so, blocks every
    /// subsequent <see cref="Begin"/> call from succeeding until the shutdown is committed or
    /// canceled. Returns false with a reason if something is active right now.
    /// </summary>
    bool TryBeginShutdown(out string? blockReason);

    /// <summary>The update apply committed and the process is exiting; nothing further to coordinate locally.</summary>
    void CommitShutdown();

    /// <summary>Rolls back a shutdown gate that was acquired via <see cref="TryBeginShutdown"/> but not committed, so normal operations can resume.</summary>
    void CancelShutdown();
}

public sealed class CriticalOperationTracker : ICriticalOperationTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, (CriticalOperationKind Kind, string? Detail)> _active = [];
    private bool _shuttingDown;

    public bool IsBusy
    {
        get { lock (_sync) return _active.Count > 0; }
    }

    public IReadOnlyList<string> ActiveOperations
    {
        get
        {
            lock (_sync)
                return _active.Values.Select(Describe).ToList();
        }
    }

    public IDisposable Begin(CriticalOperationKind kind, string? detail = null)
    {
        var id = Guid.NewGuid();
        lock (_sync)
        {
            if (_shuttingDown)
                throw new InvalidOperationException("Palworld Server Manager is applying an update and restarting. Try this again once the Manager has restarted.");
            _active[id] = (kind, detail);
        }
        return new Lease(this, id);
    }

    public bool TryBeginShutdown(out string? blockReason)
    {
        lock (_sync)
        {
            if (_shuttingDown)
            {
                blockReason = "An update apply is already in progress.";
                return false;
            }
            if (_active.Count > 0)
            {
                blockReason = "In progress: " + string.Join(", ", _active.Values.Select(Describe));
                return false;
            }
            _shuttingDown = true;
            blockReason = null;
            return true;
        }
    }

    public void CommitShutdown() { }

    public void CancelShutdown()
    {
        lock (_sync) _shuttingDown = false;
    }

    private static string Describe((CriticalOperationKind Kind, string? Detail) op)
        => op.Detail is null ? op.Kind.ToString() : $"{op.Kind} ({op.Detail})";

    private void End(Guid id)
    {
        lock (_sync) _active.Remove(id);
    }

    private sealed class Lease(CriticalOperationTracker owner, Guid id) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.End(id);
        }
    }
}
