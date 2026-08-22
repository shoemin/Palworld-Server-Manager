using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Services;

namespace PalworldServerManager.Lan;

public sealed class LanCoordinator : IAsyncDisposable
{
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    public LanCoordinator(
        AppPaths paths,
        ProfileRegistry registry,
        DashboardService dashboard,
        ServerProcessService processes,
        IAppLogger logger,
        ICriticalOperationTracker? operations = null)
    {
        _logger = logger;
        State = new LanStateStore(paths);
        Pairing = new PairingService();
        Discovery = new PeerDiscoveryService(State.InstanceId, State, logger);
        Host = new ManagerLanHost(paths, registry, dashboard, processes, State, Pairing, logger, operations);
        Client = new ManagerLanClient(State, logger, operations);
    }

    public LanStateStore State { get; }
    public PairingService Pairing { get; }
    public PeerDiscoveryService Discovery { get; }
    public ManagerLanHost Host { get; }
    public ManagerLanClient Client { get; }

    public bool Enabled => State.Enabled;
    public bool Running => Host.IsRunning;

    public Task StartIfEnabledAsync(CancellationToken cancellationToken = default)
        => State.Enabled ? StartAsync(cancellationToken) : Task.CompletedTask;

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            State.SetEnabled(false);
            await StopAsync();
            return;
        }

        try
        {
            await StartAsync(cancellationToken);
            State.SetEnabled(true);
        }
        catch
        {
            State.SetEnabled(false);
            throw;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (Host.IsRunning) return;
            try
            {
                await Host.StartAsync(State.ApiPort, cancellationToken);
                await Discovery.StartAsync(State.ApiPort, State.DiscoveryPort, cancellationToken);
            }
            catch
            {
                await Discovery.DisposeAsync();
                await Host.DisposeAsync();
                throw;
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await Discovery.DisposeAsync();
            await Host.DisposeAsync();
            _logger.Info("Manager LAN services stopped.");
        }
        finally { _lifecycleGate.Release(); }
    }

    public IReadOnlyList<LanPeer> GetPeers() => Discovery.GetPeers();

    public void UnpairPeer(Guid peerId) => State.RemovePeerTrust(peerId);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleGate.Dispose();
    }
}
