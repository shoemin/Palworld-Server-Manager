using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using PalworldServerManager.Core.Infrastructure;

namespace PalworldServerManager.Lan;

public sealed class PeerDiscoveryService : IAsyncDisposable
{
    private readonly Guid _instanceId;
    private readonly LanStateStore _state;
    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<Guid, LanPeer> _peers = new();
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;
    private Task? _sendLoop;
    private Task? _receiveLoop;

    public PeerDiscoveryService(Guid instanceId, LanStateStore state, IAppLogger logger)
    {
        _instanceId = instanceId;
        _state = state;
        _logger = logger;
    }

    public event EventHandler? PeersChanged;

    public IReadOnlyList<LanPeer> GetPeers()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-12);
        foreach (var pair in _peers)
            if (pair.Value.LastSeenUtc < cutoff) _peers.TryRemove(pair.Key, out _);

        return _peers.Values
            .OrderBy(x => x.MachineName, StringComparer.OrdinalIgnoreCase)
            .Select(x =>
            {
                x.IsPaired = _state.IsRemotePaired(x.InstanceId);
                return x;
            })
            .ToList();
    }

    public Task StartAsync(int apiPort, int discoveryPort, CancellationToken cancellationToken = default)
    {
        if (_cts is not null) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
        _udp.EnableBroadcast = true;

        _sendLoop = Task.Run(() => SendLoopAsync(apiPort, discoveryPort, _cts.Token));
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _logger.Info($"LAN discovery started. UDP port={discoveryPort} instance={_instanceId}");
        return Task.CompletedTask;
    }

    private async Task SendLoopAsync(int apiPort, int discoveryPort, CancellationToken cancellationToken)
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.3.0";
        var advertisement = new LanAdvertisement
        {
            InstanceId = _instanceId,
            MachineName = Environment.MachineName,
            ApiPort = apiPort,
            ManagerVersion = version
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(advertisement);
        var endpoint = new IPEndPoint(IPAddress.Broadcast, discoveryPort);

        while (!cancellationToken.IsCancellationRequested)
        {
            try { await _udp!.SendAsync(bytes, endpoint, cancellationToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.Warning("LAN discovery broadcast failed: " + ex.Message); }

            try { await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var received = await _udp!.ReceiveAsync(cancellationToken);
                var ad = JsonSerializer.Deserialize<LanAdvertisement>(received.Buffer);
                if (ad is null
                    || ad.Protocol != LanProtocol.ProtocolName
                    || ad.ProtocolVersion != LanProtocol.ProtocolVersion
                    || ad.InstanceId == _instanceId)
                    continue;

                _peers[ad.InstanceId] = new LanPeer
                {
                    InstanceId = ad.InstanceId,
                    MachineName = ad.MachineName,
                    Address = received.RemoteEndPoint.Address.ToString(),
                    ApiPort = ad.ApiPort,
                    ManagerVersion = ad.ManagerVersion,
                    LastSeenUtc = DateTime.UtcNow,
                    IsPaired = _state.IsRemotePaired(ad.InstanceId)
                };
                PeersChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.Warning("LAN discovery receive failed: " + ex.Message); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        _udp?.Dispose();
        try
        {
            if (_sendLoop is not null) await _sendLoop;
            if (_receiveLoop is not null) await _receiveLoop;
        }
        catch { }
        _cts.Dispose();
        _cts = null;
        _udp = null;
    }
}
