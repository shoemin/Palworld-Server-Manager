namespace PalworldServerManager.Contracts;

public sealed record HostId
{
    public Guid Value { get; }
    public HostId(Guid value) { if (value == Guid.Empty) throw new ArgumentException("HostId cannot be empty."); Value = value; }
    public static HostId Parse(string value) => new(ParseIdentifier(value));
    internal static Guid ParseIdentifier(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) throw new ArgumentException("A nonempty canonical UUID is required.");
        return id;
    }
}
public sealed record ServerRef
{
    public HostId AuthoritativeHostId { get; }
    public Guid ServerProfileId { get; }
    public ServerRef(HostId authoritativeHostId, Guid serverProfileId)
    {
        AuthoritativeHostId = authoritativeHostId ?? throw new ArgumentNullException(nameof(authoritativeHostId));
        if (serverProfileId == Guid.Empty) throw new ArgumentException("ServerProfileId cannot be empty.");
        ServerProfileId = serverProfileId;
    }
    public Wire.ServerRef ToWire() => new() { AuthoritativeHostId = AuthoritativeHostId.Value.ToString("D"), ServerProfileId = ServerProfileId.ToString("D") };
    public static ServerRef FromWire(Wire.ServerRef value)
    { ArgumentNullException.ThrowIfNull(value); return new(HostId.Parse(value.AuthoritativeHostId), HostId.ParseIdentifier(value.ServerProfileId)); }
}

// Syntactic boundary validation only. A valid grant DTO conveys no authority by itself.
public static class GrantWireValidation
{
    public static bool IsKnown(Wire.HostCapability value) => value is
        Wire.HostCapability.CreateServer or Wire.HostCapability.ManageHostSettings or
        Wire.HostCapability.ManageTrustedManagers or Wire.HostCapability.ManagePermissions or Wire.HostCapability.ManageHostUpdates;
    public static bool IsKnown(Wire.ServerCapability value) => value is
        Wire.ServerCapability.ViewServer or Wire.ServerCapability.StartStopRestart or Wire.ServerCapability.EditSettings or
        Wire.ServerCapability.ManageBackups or Wire.ServerCapability.TransferExport or Wire.ServerCapability.DeleteServer or Wire.ServerCapability.ManageServerSharing;
    public static bool IsValid(Wire.HostCapabilityGrant? grant) => grant is not null && IsKnown(grant.Capability) &&
        ValidId(grant.TargetHostId) && ValidCommon(grant.GrantId, grant.GranteeActor, grant.GrantedByActor, grant.HasDerivedFromGrantId ? grant.DerivedFromGrantId : null, grant.GrantedUtc);
    public static bool IsValid(Wire.ServerCapabilityGrant? grant) => grant is not null && IsKnown(grant.Capability) && grant.Server is not null &&
        ValidId(grant.Server.AuthoritativeHostId) && ValidId(grant.Server.ServerProfileId) &&
        ValidCommon(grant.GrantId, grant.GranteeActor, grant.GrantedByActor, grant.HasDerivedFromGrantId ? grant.DerivedFromGrantId : null, grant.GrantedUtc);
    private static bool ValidCommon(string id, Wire.ActorRef grantee, Wire.ActorRef grantor, string? parent, string utc)
        => ValidId(id) && ValidActor(grantee) && ValidActor(grantor) && (parent is null || ValidId(parent)) &&
            DateTimeOffset.TryParseExact(utc, "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time) && time.Offset == TimeSpan.Zero;
    private static bool ValidId(string? id) => Guid.TryParseExact(id, "D", out var value) && value != Guid.Empty;
    private static bool ValidActor(Wire.ActorRef? actor) => actor?.ActorCase switch
    { Wire.ActorRef.ActorOneofCase.LocalPrincipalId => ValidId(actor.LocalPrincipalId), Wire.ActorRef.ActorOneofCase.RemoteHostId => ValidId(actor.RemoteHostId), _ => false };
}
