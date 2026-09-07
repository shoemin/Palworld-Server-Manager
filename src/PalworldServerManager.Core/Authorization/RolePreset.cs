namespace PalworldServerManager.Core.Authorization;

// Proposed data only: no grantor, Owner claim or previously authorized snapshot.
public abstract record GrantRequest
{
    public Guid GrantId { get; }
    public ActorRef Grantee { get; }
    public DelegationRights Rights { get; }
    public Guid? SourceGrantId { get; }
    private protected GrantRequest(Guid id,ActorRef grantee,DelegationRights rights,Guid? source)
    {
        if(id==Guid.Empty||source==Guid.Empty||source==id)throw new ArgumentException("Invalid proposed grant identity.");
        GrantId=id;Grantee=grantee??throw new ArgumentNullException(nameof(grantee));
        Rights=rights??throw new ArgumentNullException(nameof(rights));SourceGrantId=source;
    }
}
public sealed record HostGrantRequest:GrantRequest
{
    public HostCapability Capability { get; }
    public Guid TargetHostId { get; }
    public HostGrantRequest(Guid id,ActorRef grantee,HostCapability capability,Guid targetHostId,DelegationRights rights,Guid? source=null)
        :base(id,grantee,rights,source)
    {
        if(!Enum.IsDefined(capability)||targetHostId==Guid.Empty)throw new ArgumentException("Invalid proposed Host capability.");
        Capability=capability;TargetHostId=targetHostId;
    }
}
public sealed record ServerGrantRequest:GrantRequest
{
    public ServerCapability Capability { get; }
    public ServerRef Target { get; }
    public ServerGrantRequest(Guid id,ActorRef grantee,ServerCapability capability,ServerRef target,DelegationRights rights,Guid? source=null)
        :base(id,grantee,rights,source)
    {
        if(!Enum.IsDefined(capability))throw new ArgumentException("Invalid proposed server capability.");
        Capability=capability;Target=target??throw new ArgumentNullException(nameof(target));
    }
}
// UI recipe/label is presentation only. Explicit entries, never the name, determine the
// proposed effects; each entry still needs canonical authorization by its actual applier.
public sealed class RolePreset
{
    public string Label { get; }
    public IReadOnlyList<HostGrantRequest> Hosts { get; }
    public IReadOnlyList<ServerGrantRequest> Servers { get; }
    public RolePreset(string label,IEnumerable<HostGrantRequest> hosts,IEnumerable<ServerGrantRequest> servers)
    {
        if(string.IsNullOrWhiteSpace(label)||label.Length>120||label.Any(char.IsControl))throw new ArgumentException("Invalid preset label.");
        ArgumentNullException.ThrowIfNull(hosts);ArgumentNullException.ThrowIfNull(servers);
        var hs=hosts.ToArray();var ss=servers.ToArray();
        if(hs.Any(x=>x is null)||ss.Any(x=>x is null)||hs.Select(x=>x.GrantId).Distinct().Count()!=hs.Length||
            ss.Select(x=>x.GrantId).Distinct().Count()!=ss.Length)throw new ArgumentException("Duplicate or invalid preset grant identity.");
        Label=label.Trim();Hosts=Array.AsReadOnly(hs);Servers=Array.AsReadOnly(ss);
    }
}
public sealed record PresetExpansion(IReadOnlyList<HostCapabilityGrant> Hosts,IReadOnlyList<ServerCapabilityGrant> Servers);
