namespace PalworldServerManager.Core.Authorization;

public enum HostCapability { CreateServer=1, ManageHostSettings=2, ManageTrustedManagers=3, ManagePermissions=4, ManageHostUpdates=5 }
public enum ServerCapability { ViewServer=1, StartStopRestart=2, EditSettings=3, ManageBackups=4, TransferExport=5, DeleteServer=6, ManageServerSharing=7 }
public enum ActorKind { LocalPrincipal=1, RemoteManager=2 }

public sealed record ActorRef
{
    public ActorKind Kind { get; }
    public Guid Id { get; }
    public ActorRef(ActorKind kind,Guid id)
    {
        if(!Enum.IsDefined(kind) || id==Guid.Empty)throw new ArgumentException("Invalid authority actor.");
        Kind=kind;Id=id;
    }
    public static ActorRef LocalPrincipal(Guid id)=>new(ActorKind.LocalPrincipal,id);
    public static ActorRef RemoteManager(Guid id)=>new(ActorKind.RemoteManager,id);
}
// The BCL-only domain tuple. Host maps it to Contracts.ServerRef; Core never references protobuf.
public sealed record ServerRef
{
    public Guid AuthoritativeHostId { get; }
    public Guid ServerProfileId { get; }
    public ServerRef(Guid authoritativeHostId,Guid serverProfileId)
    {
        if(authoritativeHostId==Guid.Empty || serverProfileId==Guid.Empty)throw new ArgumentException("A Host-qualified server is required.");
        AuthoritativeHostId=authoritativeHostId;ServerProfileId=serverProfileId;
    }
}
public sealed record DelegationRights
{
    public bool CanDelegate { get; }
    public bool CanDelegateOnwardDelegation { get; }
    public DelegationRights(bool canDelegate,bool canDelegateOnwardDelegation)
    {
        if(canDelegateOnwardDelegation && !canDelegate)throw new ArgumentException("Onward delegation requires delegation.");
        CanDelegate=canDelegate;CanDelegateOnwardDelegation=canDelegateOnwardDelegation;
    }
}
// Data describes a grant; construction is not proof of authorization. A trusted Host snapshot
// validates the complete lineage before use. The common base has no capability/scope bag.
public abstract record CapabilityGrant
{
    public Guid GrantId { get; }
    public ActorRef GranteeActor { get; }
    public ActorRef GrantedByActor { get; }
    public DelegationRights Rights { get; }
    public Guid? DerivedFromGrantId { get; }
    public DateTimeOffset GrantedUtc { get; }
    public DateTimeOffset? InvalidatedUtc { get; }
    private protected CapabilityGrant(Guid grantId,ActorRef grantee,ActorRef grantor,DelegationRights rights,
        Guid? parent,DateTimeOffset grantedUtc,DateTimeOffset? invalidatedUtc)
    {
        if(grantId==Guid.Empty || parent==Guid.Empty || parent==grantId || grantedUtc.Offset!=TimeSpan.Zero ||
            (invalidatedUtc is {} invalidated && invalidated.Offset!=TimeSpan.Zero))throw new ArgumentException("Invalid grant metadata.");
        GrantId=grantId;GranteeActor=grantee??throw new ArgumentNullException(nameof(grantee));
        GrantedByActor=grantor??throw new ArgumentNullException(nameof(grantor));Rights=rights??throw new ArgumentNullException(nameof(rights));
        DerivedFromGrantId=parent;GrantedUtc=grantedUtc;InvalidatedUtc=invalidatedUtc;
    }
}
public sealed record HostCapabilityGrant : CapabilityGrant
{
    public Guid TargetHostId { get; }
    public HostCapability Capability { get; }
    public HostCapabilityGrant(Guid id,ActorRef grantee,HostCapability capability,Guid targetHostId,DelegationRights rights,
        ActorRef grantor,Guid? parent,DateTimeOffset utc,DateTimeOffset? invalidatedUtc=null)
        :base(id,grantee,grantor,rights,parent,utc,invalidatedUtc)
    {
        if(!Enum.IsDefined(capability) || targetHostId==Guid.Empty)throw new ArgumentException("Invalid Host capability target.");
        Capability=capability;TargetHostId=targetHostId;
    }
}
public sealed record ServerCapabilityGrant : CapabilityGrant
{
    public ServerRef Target { get; }
    public ServerCapability Capability { get; }
    public ServerCapabilityGrant(Guid id,ActorRef grantee,ServerCapability capability,ServerRef target,DelegationRights rights,
        ActorRef grantor,Guid? parent,DateTimeOffset utc,DateTimeOffset? invalidatedUtc=null)
        :base(id,grantee,grantor,rights,parent,utc,invalidatedUtc)
    {
        if(!Enum.IsDefined(capability))throw new ArgumentException("Invalid server capability.");
        Capability=capability;Target=target??throw new ArgumentNullException(nameof(target));
    }
}
