using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PalworldServerManager.Core.Infrastructure;

namespace PalworldServerManager.Lan;

internal sealed class LanStateDocument
{
    public Guid InstanceId { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; }
    public int ApiPort { get; set; } = LanProtocol.DefaultApiPort;
    public int DiscoveryPort { get; set; } = LanProtocol.DefaultDiscoveryPort;
    public List<TrustedInboundPeer> TrustedInbound { get; set; } = [];
    public List<RemoteCredential> RemoteCredentials { get; set; } = [];
}

internal sealed class TrustedInboundPeer
{
    public Guid InstanceId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime PairedUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class RemoteCredential
{
    public Guid InstanceId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public DateTime PairedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LanStateStore
{
    private readonly object _sync = new();
    private readonly string _file;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private LanStateDocument _state;

    public LanStateStore(AppPaths paths)
    {
        Directory.CreateDirectory(paths.LanRoot);
        _file = Path.Combine(paths.LanRoot, "lan-state.json");
        _state = Load();
    }

    public Guid InstanceId { get { lock (_sync) return _state.InstanceId; } }
    public bool Enabled { get { lock (_sync) return _state.Enabled; } }
    public int ApiPort { get { lock (_sync) return _state.ApiPort; } }
    public int DiscoveryPort { get { lock (_sync) return _state.DiscoveryPort; } }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            _state.Enabled = enabled;
            SaveLocked();
        }
    }

    public string AddInboundTrust(Guid peerId, string machineName)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = HashToken(token);

        lock (_sync)
        {
            _state.TrustedInbound.RemoveAll(x => x.InstanceId == peerId);
            _state.TrustedInbound.Add(new TrustedInboundPeer
            {
                InstanceId = peerId,
                MachineName = machineName,
                TokenHash = hash,
                PairedUtc = DateTime.UtcNow
            });
            SaveLocked();
        }

        return token;
    }

    public bool IsAuthorizedToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var candidate = Convert.FromHexString(HashToken(token));
        lock (_sync)
        {
            foreach (var trusted in _state.TrustedInbound)
            {
                try
                {
                    var stored = Convert.FromHexString(trusted.TokenHash);
                    if (stored.Length == candidate.Length && CryptographicOperations.FixedTimeEquals(stored, candidate))
                        return true;
                }
                catch { }
            }
        }
        return false;
    }

    public void SaveRemoteCredential(Guid peerId, string machineName, string token)
    {
        lock (_sync)
        {
            _state.RemoteCredentials.RemoveAll(x => x.InstanceId == peerId);
            _state.RemoteCredentials.Add(new RemoteCredential
            {
                InstanceId = peerId,
                MachineName = machineName,
                Token = token,
                PairedUtc = DateTime.UtcNow
            });
            SaveLocked();
        }
    }

    public string? GetRemoteToken(Guid peerId)
    {
        lock (_sync) return _state.RemoteCredentials.FirstOrDefault(x => x.InstanceId == peerId)?.Token;
    }

    public bool IsRemotePaired(Guid peerId) => !string.IsNullOrWhiteSpace(GetRemoteToken(peerId));

    public void RemovePeerTrust(Guid peerId)
    {
        lock (_sync)
        {
            _state.RemoteCredentials.RemoveAll(x => x.InstanceId == peerId);
            _state.TrustedInbound.RemoveAll(x => x.InstanceId == peerId);
            SaveLocked();
        }
    }

    private LanStateDocument Load()
    {
        try
        {
            if (!File.Exists(_file))
            {
                var created = new LanStateDocument();
                File.WriteAllText(_file, JsonSerializer.Serialize(created, _json));
                return created;
            }

            return JsonSerializer.Deserialize<LanStateDocument>(File.ReadAllText(_file), _json)
                ?? new LanStateDocument();
        }
        catch
        {
            return new LanStateDocument();
        }
    }

    private void SaveLocked()
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, _json));
        File.Move(temp, _file, true);
    }

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
