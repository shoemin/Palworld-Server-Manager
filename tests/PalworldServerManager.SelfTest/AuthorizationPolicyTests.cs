using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Host;
using Protocol=PalworldServerManager.Contracts;
using Wire=PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.SelfTest;

internal static class AuthorizationPolicyTests
{
    private static readonly DateTimeOffset Now=new(2026,9,7,0,0,0,TimeSpan.Zero);
    private static readonly DelegationRights Use=new(false,false), Delegate=new(true,false), Onward=new(true,true);
    private static void Check(bool value){if(!value)throw new Exception("Authorization foundation assertion failed.");}
    private static void Reject<T>(Action action)where T:Exception
    {try{action();}catch(T){return;}throw new Exception("Expected authorization refusal: "+typeof(T).Name);}
    private static bool Issued(Func<CapabilityGrant> action)
    {try{action();return true;}catch(UnauthorizedAccessException){return false;}}
    private sealed class Rig(Guid? host=null)
    {
        internal readonly Guid Host=host??Guid.NewGuid(),Peer=Guid.NewGuid();
        internal readonly ActorRef Owner=ActorRef.LocalPrincipal(Guid.NewGuid()),A=ActorRef.LocalPrincipal(Guid.NewGuid()),B=ActorRef.LocalPrincipal(Guid.NewGuid());
        internal AuthorizationPolicy Policy(IEnumerable<HostCapabilityGrant>? hs=null,IEnumerable<ServerCapabilityGrant>? ss=null,
            IEnumerable<Guid>? locals=null,IEnumerable<Guid>? peers=null)
            =>new(Host,Owner.Id,locals??[Owner.Id,A.Id,B.Id],peers??[Peer],hs??[],ss??[]);
        internal HostCapabilityGrant Root(ActorRef grantee,HostCapability cap=HostCapability.CreateServer,Guid? target=null,DelegationRights? rights=null,Guid? id=null)
            =>Policy().IssueHost(Owner,id??Guid.NewGuid(),grantee,cap,target??Host,rights??Onward,null,Now);
        internal ServerCapabilityGrant RootServer(ActorRef grantee,ServerRef target,ServerCapability cap=ServerCapability.ViewServer,DelegationRights? rights=null,Guid? id=null)
            =>Policy().IssueServer(Owner,id??Guid.NewGuid(),grantee,cap,target,rights??Onward,null,Now);
    }
    public static Task ModelsAndProtocolMapping()
    {
        var r=new Rig();var profile=Guid.NewGuid();var a=new ServerRef(r.Host,profile);var b=new ServerRef(r.Peer,profile);
        Check(a!=b && new HashSet<ServerRef>{a,b}.Count==2);
        Check(AuthorizationContractMapping.ToDomain(AuthorizationContractMapping.ToProtocol(a))==a);
        Check(AuthorizationContractMapping.ToProtocol(a)==new Protocol.ServerRef(new Protocol.HostId(r.Host),profile));
        Check(typeof(HostCapabilityGrant).GetProperties().All(p=>p.PropertyType!=typeof(ServerRef)));
        foreach(var c in Enum.GetValues<HostCapability>())Check(AuthorizationContractMapping.ToDomain((Wire.HostCapability)(int)c)==c);
        foreach(var c in Enum.GetValues<ServerCapability>())Check(AuthorizationContractMapping.ToDomain((Wire.ServerCapability)(int)c)==c);
        Check(Enum.GetValues<HostCapability>().Length==5 && Enum.GetValues<ServerCapability>().Length==7);
        foreach(var unknown in new[]{-1,0,99,int.MaxValue})
        {
            Reject<ArgumentException>(()=>AuthorizationContractMapping.ToDomain((Wire.HostCapability)unknown));
            Reject<ArgumentException>(()=>AuthorizationContractMapping.ToDomain((Wire.ServerCapability)unknown));
            Check(!r.Policy().CanUseHost(r.Owner,(HostCapability)unknown,r.Host));
            Check(!r.Policy().CanUseServer(r.Owner,(ServerCapability)unknown,a));
            Reject<ArgumentException>(()=>r.Root(r.A,(HostCapability)unknown));
        }
        Reject<ArgumentException>(()=>new ServerRef(Guid.Empty,profile));Reject<ArgumentException>(()=>new ServerRef(r.Host,Guid.Empty));
        Reject<ArgumentException>(()=>new ActorRef((ActorKind)0,r.Host));Reject<ArgumentException>(()=>ActorRef.LocalPrincipal(Guid.Empty));
        Reject<ArgumentException>(()=>new DelegationRights(false,true));
        Check(ActorRef.LocalPrincipal(r.Peer)!=ActorRef.RemoteManager(r.Peer));
        return Task.CompletedTask;
    }
    public static Task ExhaustiveDelegationRightsAndTypedScope()
    {
        var r=new Rig();DelegationRights[] rights=[Use,Delegate,Onward];
        bool[,] expected={{false,false,false},{true,false,false},{true,true,true}};
        var target=new ServerRef(r.Host,Guid.NewGuid());
        foreach(var capability in Enum.GetValues<HostCapability>())
        for(var i=0;i<3;i++)for(var j=0;j<3;j++)
        {
            var parent=r.Root(r.A,capability,rights:rights[i]);var policy=r.Policy([parent]);
            Check(Issued(()=>policy.IssueHost(r.A,Guid.NewGuid(),r.B,capability,r.Host,rights[j],parent.GrantId,Now))==expected[i,j]);
            Check(!Issued(()=>policy.IssueHost(r.A,Guid.NewGuid(),r.B,capability,r.Peer,Use,parent.GrantId,Now)));
            Check(!Issued(()=>policy.IssueHost(r.B,Guid.NewGuid(),r.A,capability,r.Host,Use,parent.GrantId,Now)));
        }
        foreach(var capability in Enum.GetValues<ServerCapability>())
        for(var i=0;i<3;i++)for(var j=0;j<3;j++)
        {
            var parent=r.RootServer(r.A,target,capability,rights[i]);var policy=r.Policy(ss:[parent]);
            Check(Issued(()=>policy.IssueServer(r.A,Guid.NewGuid(),r.B,capability,target,rights[j],parent.GrantId,Now))==expected[i,j]);
            Check(!Issued(()=>policy.IssueServer(r.A,Guid.NewGuid(),r.B,capability,new(r.Peer,target.ServerProfileId),Use,parent.GrantId,Now)));
            Check(!Issued(()=>policy.IssueServer(r.A,Guid.NewGuid(),r.B,capability,new(r.Host,Guid.NewGuid()),Use,parent.GrantId,Now)));
        }
        var narrow=r.Root(r.A,HostCapability.CreateServer,rights:Delegate);var broad=r.Root(r.A,HostCapability.ManageHostUpdates);
        var both=r.Policy([narrow,broad]);
        Check(!Issued(()=>both.IssueHost(r.A,Guid.NewGuid(),r.B,HostCapability.CreateServer,r.Host,Onward,narrow.GrantId,Now)));
        Check(!Issued(()=>both.IssueHost(r.A,Guid.NewGuid(),r.B,HostCapability.CreateServer,r.Host,Use,broad.GrantId,Now)));
        Check(!Issued(()=>both.IssueServer(r.A,Guid.NewGuid(),r.B,ServerCapability.ViewServer,target,Use,narrow.GrantId,Now)));
        return Task.CompletedTask;
    }
    public static Task ForestValidityAndExactSubtrees()
    {
        var r=new Rig();var root=r.Root(r.A);var sibling=r.Root(r.A,HostCapability.ManageHostSettings);
        var child=r.Policy([root,sibling]).IssueHost(r.A,Guid.NewGuid(),r.B,root.Capability,r.Host,Onward,root.GrantId,Now);
        var peer=ActorRef.RemoteManager(r.Peer);
        var grand=r.Policy([root,sibling,child]).IssueHost(r.B,Guid.NewGuid(),peer,root.Capability,r.Host,Use,child.GrantId,Now);
        var other=r.Policy([root,sibling]).IssueHost(r.A,Guid.NewGuid(),r.B,sibling.Capability,r.Host,Use,sibling.GrantId,Now);
        var server=r.RootServer(r.A,new(r.Host,Guid.NewGuid()),id:root.GrantId);
        var sChild=r.Policy(ss:[server]).IssueServer(r.A,Guid.NewGuid(),r.B,server.Capability,server.Target,Use,server.GrantId,Now);
        var policy=r.Policy([root,sibling,child,grand,other],[server,sChild]);
        Check(policy.HostSubtree(root.GrantId).ToHashSet().SetEquals([root.GrantId,child.GrantId,grand.GrantId]));
        Check(policy.ServerSubtree(root.GrantId).ToHashSet().SetEquals([server.GrantId,sChild.GrantId]));
        Check(policy.CanUseHost(peer,root.Capability,r.Host));Check(!policy.CanUseHost(peer,root.Capability,r.Peer));
        Check(!policy.CanUseServer(r.B,server.Capability,new(r.Peer,server.Target.ServerProfileId)));
        var invalid=new HostCapabilityGrant(root.GrantId,root.GranteeActor,root.Capability,root.TargetHostId,root.Rights,root.GrantedByActor,null,Now,Now);
        var revoked=r.Policy([invalid,sibling,child,grand,other]);
        Check(!revoked.CanUseHost(peer,root.Capability,r.Host) && !revoked.CanUseHost(r.B,root.Capability,r.Host));
        Check(revoked.CanUseHost(r.B,sibling.Capability,r.Host) && revoked.IsOwner(r.Owner));
        Check(!r.Policy([root,child,grand],locals:[r.Owner.Id,r.B.Id]).CanUseHost(peer,root.Capability,r.Host));
        return Task.CompletedTask;
    }
    public static Task MalformedLineagesAndStructuralOwner()
    {
        var r=new Rig();var id1=Guid.NewGuid();var id2=Guid.NewGuid();
        var cycle1=new HostCapabilityGrant(id1,r.A,HostCapability.CreateServer,r.Host,Onward,r.B,id2,Now);
        var cycle2=new HostCapabilityGrant(id2,r.B,HostCapability.CreateServer,r.Host,Onward,r.A,id1,Now);
        Check(!r.Policy([cycle1,cycle2]).CanUseHost(r.A,HostCapability.CreateServer,r.Host));
        Check(r.Policy([cycle1,cycle2]).HostSubtree(id1).Count==2);
        Check(!r.Policy([cycle1]).CanUseHost(r.A,HostCapability.CreateServer,r.Host));
        var fakeRoot=new HostCapabilityGrant(Guid.NewGuid(),r.B,HostCapability.CreateServer,r.Host,Use,r.A,null,Now);
        Check(!r.Policy([fakeRoot]).CanUseHost(r.B,HostCapability.CreateServer,r.Host));
        var permissions=r.Root(r.A,HostCapability.ManagePermissions,rights:Use);var policy=r.Policy([permissions]);
        Check(policy.CanUseHost(r.A,HostCapability.ManagePermissions,r.Host));
        Check(!Issued(()=>policy.IssueHost(r.A,Guid.NewGuid(),r.B,HostCapability.ManageHostUpdates,r.Host,Use,null,Now)));
        Check(!Issued(()=>policy.IssueHost(r.A,Guid.NewGuid(),r.B,HostCapability.ManageHostUpdates,r.Host,Use,permissions.GrantId,Now)));
        Check(policy.CanUseHost(r.Owner,HostCapability.ManageHostUpdates,r.Peer));
        var root=policy.IssueHost(r.Owner,Guid.NewGuid(),r.B,HostCapability.ManageHostUpdates,r.Peer,Onward,null,Now);
        Check(root.DerivedFromGrantId is null && root.GrantedByActor==r.Owner && root.TargetHostId==r.Peer);
        var uninitialized=new AuthorizationPolicy(r.Host,null,[r.Owner.Id,r.A.Id],[r.Peer],[root],[]);
        Check(!uninitialized.CanUseHost(r.Owner,HostCapability.ManageHostUpdates,r.Host));
        Check(!Issued(()=>uninitialized.IssueHost(r.Owner,Guid.NewGuid(),r.A,HostCapability.CreateServer,r.Host,Use,null,Now)));
        Check(!Issued(()=>r.Policy(peers:[]).IssueHost(r.Owner,Guid.NewGuid(),ActorRef.RemoteManager(r.Peer),HostCapability.CreateServer,r.Host,Use,null,Now)));
        return Task.CompletedTask;
    }
    public static Task DualLocalAndRemoteCeilings()
    {
        var local=new Rig();var remote=new Rig(local.Peer);var machine=ActorRef.RemoteManager(local.Host);
        var target=new ServerRef(remote.Host,Guid.NewGuid());
        foreach(var localAllowed in new[]{false,true})foreach(var remoteAllowed in new[]{false,true})
        {
            var lHost=local.Root(local.A,HostCapability.CreateServer,remote.Host,Use);
            var lServer=local.RootServer(local.A,target,rights:Use);
            var remoteBase=remote.Policy(peers:[local.Host]);
            var rHost=remoteBase.IssueHost(remote.Owner,Guid.NewGuid(),machine,HostCapability.CreateServer,remote.Host,Use,null,Now);
            var rServer=remoteBase.IssueServer(remote.Owner,Guid.NewGuid(),machine,ServerCapability.ViewServer,target,Use,null,Now);
            var lp=local.Policy(localAllowed?[lHost]:[],localAllowed?[lServer]:[]);
            var rp=remote.Policy(remoteAllowed?[rHost]:[],remoteAllowed?[rServer]:[],peers:[local.Host]);
            Check(RemoteAuthorization.CanUseHost(lp,local.A,rp,HostCapability.CreateServer)==(localAllowed && remoteAllowed));
            Check(RemoteAuthorization.CanUseServer(lp,local.A,rp,ServerCapability.ViewServer,target)==(localAllowed && remoteAllowed));
            Check(RemoteAuthorization.CanUseHost(lp,local.Owner,rp,HostCapability.CreateServer)==remoteAllowed);
            Check(!RemoteAuthorization.CanUseHost(local.Policy([lHost],peers:[]),local.A,rp,HostCapability.CreateServer));
            Check(!RemoteAuthorization.CanUseHost(lp,ActorRef.RemoteManager(local.Peer),rp,HostCapability.CreateServer));
            Check(!RemoteAuthorization.CanUseServer(lp,local.A,rp,ServerCapability.ViewServer,new(local.Host,target.ServerProfileId)));
            Check(!RemoteAuthorization.CanUseHost(lp,local.A,remote.Policy([rHost],[rServer],peers:[]),HostCapability.CreateServer));
        }
        return Task.CompletedTask;
    }
    public static Task SnapshotIsolationAndClosedInputs()
    {
        var r=new Rig();var root=r.Root(r.A);var grants=new List<HostCapabilityGrant>{root};var locals=new List<Guid>{r.Owner.Id,r.A.Id};
        var policy=r.Policy(grants,locals:locals);grants.Clear();locals.Clear();
        Check(policy.CanUseHost(r.A,root.Capability,r.Host));
        Reject<ArgumentException>(()=>r.Policy([root,root]));
        Check(!policy.CanUseHost(null,root.Capability,r.Host));Check(!policy.CanUseHost(r.Owner,root.Capability,Guid.Empty));
        Check(!policy.CanUseServer(r.Owner,ServerCapability.ViewServer,null));
        Check(!Issued(()=>policy.IssueHost(r.Owner,root.GrantId,r.A,root.Capability,r.Host,Use,null,Now)));
        Reject<ArgumentException>(()=>r.Policy(locals:[r.A.Id]));
        Check(policy.HostSubtree(Guid.NewGuid()).Count==0);
        Reject<NotSupportedException>(()=>((IList<Guid>)policy.HostSubtree(root.GrantId)).Clear());
        return Task.CompletedTask;
    }
}
