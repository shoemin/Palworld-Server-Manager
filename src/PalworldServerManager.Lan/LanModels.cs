namespace PalworldServerManager.Lan;

public sealed class LanPeer
{
    public Guid InstanceId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int ApiPort { get; set; }
    public string ManagerVersion { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
    public bool IsPaired { get; set; }

    public string BaseUri => $"http://{Address}:{ApiPort}";
    public string DisplayName => $"{MachineName} ({Address})";
    public override string ToString() => DisplayName;
}

public sealed class LanAdvertisement
{
    public string Protocol { get; set; } = LanProtocol.ProtocolName;
    public int ProtocolVersion { get; set; } = LanProtocol.ProtocolVersion;
    public Guid InstanceId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public int ApiPort { get; set; }
    public string ManagerVersion { get; set; } = string.Empty;
}

public sealed class PairRequest
{
    public Guid InstanceId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string ReciprocalToken { get; set; } = string.Empty;
}

public sealed class PairResponse
{
    public Guid InstanceId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}

public sealed class RemoteServerSummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int GamePort { get; set; }
    public int RestApiPort { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class TransferOfferRequest
{
    public Guid SourceInstanceId { get; set; }
    public string SourceMachine { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class TransferOfferResponse
{
    public Guid OfferId { get; set; }
    public string Status { get; set; } = LanTransferStatus.Pending;
}

public sealed class LanTransferOffer
{
    public Guid OfferId { get; set; }
    public Guid SourceInstanceId { get; set; }
    public string SourceMachine { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string Status { get; set; } = LanTransferStatus.Pending;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? ReceivedPath { get; set; }
    public string? Error { get; set; }
}

public sealed class TransferStatusResponse
{
    public Guid OfferId { get; set; }
    public string Status { get; set; } = LanTransferStatus.Pending;
    public string? Error { get; set; }
}

public static class LanTransferStatus
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
    public const string Receiving = "Receiving";
    public const string Received = "Received";
    public const string Failed = "Failed";
}

public static class LanProtocol
{
    public const string ProtocolName = "PalworldServerManager";
    public const int ProtocolVersion = 1;
    public const int DefaultApiPort = 8215;
    public const int DefaultDiscoveryPort = 8216;
}
