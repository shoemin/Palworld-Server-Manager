using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

// One trusted owner per authoritative Host, outside all replaceable listener generations.
// The enclosing composition owns the machine lease until this owner has drained.
internal sealed class HostOperationLifetime : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly HostDatabase database;
    private readonly Guid hostId;
    private readonly HostOperationExecutor[] registrations;
    private readonly TimeProvider? time;
    private readonly HostCredentialStateRepository identity;
    private HostOperationRuntime? runtime;
    private Task? shutdown;
    private bool failed;

    public HostOperationLifetime(HostDatabase database, Guid hostId, IEnumerable<HostOperationExecutor> registrations,
        TimeProvider? time = null)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.hostId = hostId; this.time = time; identity = new(database, hostId);
        ArgumentNullException.ThrowIfNull(registrations);
        this.registrations = registrations.ToArray();
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        if (this.registrations.Any(e => e is null || !kinds.Add(e.Definition.Kind)))
            throw new ArgumentException("Unique trusted operation registrations required.");
    }

    // Bootstrap-only startup is valid. Future ordinary operation dispatch must obtain
    // GetReady(), which rechecks initialization before admitting any work.
    public bool InitializeIfReady()
    {
        lock (gate)
        {
            if (shutdown is not null) throw new ObjectDisposedException(nameof(HostOperationLifetime));
            if (failed) throw new InvalidOperationException("Host operation startup requires recovery inspection.");
            if (runtime is not null) return true;
            try
            {
                if (!identity.Read().Initialized) return false;
                runtime = new(database, hostId, registrations, time);
                runtime.ApplyStartupRecovery();
                return true;
            }
            catch { failed = true; throw; } // retain an allocated runtime for guaranteed drain
        }
    }

    public HostOperationRuntime GetReady()
    {
        lock (gate)
            return InitializeIfReady() ? runtime! : throw new InvalidOperationException("Complete Host Owner initialization before operation access.");
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            shutdown ??= runtime?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            return new(shutdown);
        }
    }
}
