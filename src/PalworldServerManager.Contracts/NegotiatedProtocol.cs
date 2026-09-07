using PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Contracts;

public sealed class ProtocolCompatibilityException(uint localMajor, uint remoteMajor)
    : Exception($"Incompatible protocol majors: local {localMajor}, remote {remoteMajor}.")
{
    public uint LocalMajor { get; } = localMajor;
    public uint RemoteMajor { get; } = remoteMajor;
}

// A connection must earn this immutable result before dispatch, for local and remote alike.
// It is a protocol gate only; it never replaces authentication or authorization.
public sealed class NegotiatedProtocol
{
    private readonly HashSet<FeatureCapability> _features;
    public uint Major { get; }
    public uint Minor { get; }
    private NegotiatedProtocol(uint major, uint minor, HashSet<FeatureCapability> features)
    { Major = major; Minor = minor; _features = features; }
    public static NegotiatedProtocol Negotiate(Handshake local, Handshake remote)
    {
        ArgumentNullException.ThrowIfNull(local); ArgumentNullException.ThrowIfNull(remote);
        if (local.Protocol is null || remote.Protocol is null || local.Protocol.Major == 0 || remote.Protocol.Major == 0)
            throw new ArgumentException("Both peers must supply an explicit nonzero protocol major.");
        if (local.Protocol.Major != remote.Protocol.Major)
            throw new ProtocolCompatibilityException(local.Protocol.Major, remote.Protocol.Major);
        var features = local.Capabilities.Where(IsKnown).Intersect(remote.Capabilities.Where(IsKnown)).ToHashSet();
        return new(local.Protocol.Major, Math.Min(local.Protocol.Minor, remote.Protocol.Minor), features);
    }
    public bool Supports(FeatureCapability feature) => IsKnown(feature) && _features.Contains(feature);
    public void Require(FeatureCapability feature)
    { if (!Supports(feature)) throw new InvalidOperationException("The requested feature was not negotiated."); }
    public static bool IsKnown(FeatureCapability value) => value is FeatureCapability.ServerInventory or FeatureCapability.ServerIdentity or FeatureCapability.LocalPrincipalSecurity or FeatureCapability.PeerTrustActivation or FeatureCapability.PeerPairing or FeatureCapability.PeerRotationStatus or FeatureCapability.PeerRotationProposal or FeatureCapability.PeerRotationReceipt;
}
