namespace PalworldServerManager.Core.Authorization;

public sealed record HostDefaultGrant
{
    public HostCapability Capability { get; }
    public DelegationRights Rights { get; }
    public HostDefaultGrant(HostCapability capability,DelegationRights rights)
    {
        if(!Enum.IsDefined(capability))throw new ArgumentException("Unknown default Host capability.");
        Capability=capability;Rights=rights??throw new ArgumentNullException(nameof(rights));
    }
}
public sealed record ServerDefaultGrant
{
    public ServerCapability Capability { get; }
    public ServerRef Target { get; }
    public DelegationRights Rights { get; }
    public ServerDefaultGrant(ServerCapability capability,ServerRef target,DelegationRights rights)
    {
        if(!Enum.IsDefined(capability))throw new ArgumentException("Unknown default server capability.");
        Capability=capability;Target=target??throw new ArgumentNullException(nameof(target));
        Rights=rights??throw new ArgumentNullException(nameof(rights));
    }
}
// Configuration is data, never authority. Only a fresh structural-Owner transaction may
// configure it; Active application must issue every entry through the canonical root rule.
public sealed class DefaultGrantTemplate
{
    public IReadOnlyList<HostDefaultGrant> Hosts { get; }
    public IReadOnlyList<ServerDefaultGrant> Servers { get; }
    public static DefaultGrantTemplate Factory { get; }=new([],[]);
    public DefaultGrantTemplate(IEnumerable<HostDefaultGrant> hosts,IEnumerable<ServerDefaultGrant> servers)
    {
        ArgumentNullException.ThrowIfNull(hosts);ArgumentNullException.ThrowIfNull(servers);
        var hs=hosts.ToArray();var ss=servers.ToArray();
        if(hs.Any(x=>x is null)||ss.Any(x=>x is null)||hs.Select(x=>x.Capability).Distinct().Count()!=hs.Length||
            ss.Select(x=>(x.Target,x.Capability)).Distinct().Count()!=ss.Length)throw new ArgumentException("Duplicate or invalid default entry.");
        Hosts=Array.AsReadOnly(hs);Servers=Array.AsReadOnly(ss);
    }
}
