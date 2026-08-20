using System.Text.Json.Serialization;

namespace PalworldServerManager.Core.Models;

public sealed class PalworldServerInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;
    [JsonPropertyName("servername")]
    public string ServerName { get; set; } = string.Empty;
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
    [JsonPropertyName("worldguid")]
    public string WorldGuid { get; set; } = string.Empty;
}

public sealed class PalworldServerMetrics
{
    [JsonPropertyName("serverfps")]
    public int ServerFps { get; set; }
    [JsonPropertyName("currentplayernum")]
    public int CurrentPlayerNum { get; set; }
    [JsonPropertyName("serverframetime")]
    public double ServerFrameTime { get; set; }
    [JsonPropertyName("maxplayernum")]
    public int MaxPlayerNum { get; set; }
    [JsonPropertyName("uptime")]
    public long UptimeSeconds { get; set; }
    [JsonPropertyName("basecampnum")]
    public int BaseCampNum { get; set; }
    [JsonPropertyName("days")]
    public int Days { get; set; }
}

public sealed class PalworldPlayer
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("accountName")]
    public string AccountName { get; set; } = string.Empty;
    [JsonPropertyName("playerId")]
    public string PlayerId { get; set; } = string.Empty;
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("ip")]
    public string IpAddress { get; set; } = string.Empty;
    [JsonPropertyName("ping")]
    public double Ping { get; set; }
    [JsonPropertyName("location_x")]
    public double LocationX { get; set; }
    [JsonPropertyName("location_y")]
    public double LocationY { get; set; }
    [JsonPropertyName("level")]
    public int Level { get; set; }
    [JsonPropertyName("building_count")]
    public int BuildingCount { get; set; }
}

public sealed class PalworldPlayersResponse
{
    [JsonPropertyName("players")]
    public List<PalworldPlayer> Players { get; set; } = [];
}

public sealed class DashboardSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class DashboardSnapshot
{
    public Guid ProfileId { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public string SourceMachine { get; set; } = Environment.MachineName;
    public string ManagerStatus { get; set; } = "Stopped";
    public bool IsRunning { get; set; }
    public bool RestConfigured { get; set; }
    public bool RestAvailable { get; set; }
    public string? RestError { get; set; }
    public int GamePort { get; set; }
    public int RestPort { get; set; }
    public DateTime? LastBackupUtc { get; set; }
    public PalworldServerInfo? Info { get; set; }
    public PalworldServerMetrics? Metrics { get; set; }
    public List<PalworldPlayer> Players { get; set; } = [];
    public List<DashboardSetting> Settings { get; set; } = [];
}
