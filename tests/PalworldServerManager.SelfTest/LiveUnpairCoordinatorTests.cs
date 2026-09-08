using System.Security.Authentication;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static partial class LiveUnpairConnectionTests
{
    private static GrantPolicyRepository Grants(Pair f)=>new(f.A.State.Database,f.A.State.HostId,f.A.State.Time);
    private static LocalPrincipalMutationActor Owner(Pair f)=>new(f.A.State.HostId,f.A.State.OwnerId,"native-owner","fixture-public");
    private static long Incarnation(Pair f)=>HostDatabase.QueryScalarLong(f.A.State.Writer,$"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{f.B.State.HostId:D}';");
    private static PeerUnpairCommit LocalRevoke(Pair f,PeerUnpairCoordinator? coordinator)
    {
        var repo=Grants(f);using var call=new LocalPermissionCall(repo,Owner(f),default,coordinator);
        return call.RevokePeerWithNotification(repo.Read().Revision,f.B.State.HostId,Incarnation(f));
    }
    private static void Reject<T>(Action work)where T:Exception
    {try{work();}catch(T){return;}throw new Exception("Expected coordinator refusal: "+typeof(T).Name);}
    public static async Task LocalCommitReturnsBeforeHeldNotification()
    {
        await using var f=new Pair();await f.Start();var tracked=new TrackingFactory(f.A,true);
        await using var coordinator=new PeerUnpairCoordinator(f.A.State.HostId,f.Client(tracked).WithConnection<PeerUnpairNotification>);
        Check(await coordinator.Prepare(f.B.State.HostId,f.B.Address));
        var commit=LocalRevoke(f,coordinator);
        // If the command waited for the three-second delivery timeout this would already be complete.
        Check(commit.Revocation.Changed&&!commit.Notification.IsCompleted&&f.A.State.Repository.Read(f.B.State.HostId)!.State=="Revoked");
        await tracked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!commit.Notification.IsCompleted&&f.B.State.Repository.Read(f.A.State.HostId)!.State=="Revoked");
        await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(await commit.Notification==PeerUnpairNotification.Unconfirmed&&tracked.Disposed==1);
        Check(await coordinator.Notify(commit.Revocation)==PeerUnpairNotification.NotAttempted);
    }
    public static async Task MissingAndPreparingConnectionsNeverDelayCommit()
    {
        await using(var f=new Pair())
        {
            await f.Start();var invoked=0;
            await using var coordinator=new PeerUnpairCoordinator(f.A.State.HostId,(peer,address,work,ct)=>
            {Interlocked.Increment(ref invoked);throw new Exception("Notify attempted connection creation.");});
            var result=LocalRevoke(f,coordinator);
            Check(result.Notification.IsCompletedSuccessfully&&await result.Notification==PeerUnpairNotification.NotAttempted&&invoked==0);
            Check(f.B.State.Repository.Read(f.A.State.HostId)!.State=="Active");
        }
        await using(var f=new Pair())
        {
            await f.Start();var entered=Signal();var release=Signal();
            await using var coordinator=new PeerUnpairCoordinator(f.A.State.HostId,async(peer,address,work,ct)=>
            {
                entered.TrySetResult();await release.Task.WaitAsync(ct);
                // Explicit adapter: a retired preparation must refuse before using this absent connection.
                return await work(null!,ct);
            });
            var preparing=coordinator.Prepare(f.B.State.HostId,f.B.Address);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result=LocalRevoke(f,coordinator);
            Check(!preparing.IsCompleted&&result.Notification.IsCompletedSuccessfully&&await result.Notification==PeerUnpairNotification.NotAttempted);
            release.TrySetResult();Check(!await preparing.WaitAsync(TimeSpan.FromSeconds(5)));
            Check(f.B.State.Repository.Read(f.A.State.HostId)!.State=="Active");
        }
    }
    public static async Task DeniedStaleCanceledCallsLeaveReadyConnectionUnused()
    {
        await using var f=new Pair();await f.Start();
        var user=Guid.NewGuid();f.A.State.Execute($"INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc) VALUES ('{user:D}','native-user','user-public',0,'Active','{f.A.State.Time.Now:O}');");
        await using var coordinator=new PeerUnpairCoordinator(f.A.State.HostId,f.Client().WithConnection<PeerUnpairNotification>);
        Check(await coordinator.Prepare(f.B.State.HostId,f.B.Address));var repo=Grants(f);var revision=repo.Read().Revision;var inc=Incarnation(f);
        using(var denied=new LocalPermissionCall(repo,new(f.A.State.HostId,user,"native-user","user-public"),default,coordinator))
            Reject<UnauthorizedAccessException>(()=>denied.RevokePeerWithNotification(revision,f.B.State.HostId,inc));
        using(var stale=new LocalPermissionCall(repo,Owner(f),default,coordinator))
            Reject<StaleAuthorizationRevisionException>(()=>stale.RevokePeerWithNotification(revision-1,f.B.State.HostId,inc));
        using(var bad=new LocalPermissionCall(repo,Owner(f) with{PublicVerificationKey="wrong"},default,coordinator))
            Reject<AuthenticationException>(()=>bad.RevokePeerWithNotification(revision,f.B.State.HostId,inc));
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        using(var stopped=new LocalPermissionCall(repo,Owner(f),canceled.Token,coordinator))
            Reject<OperationCanceledException>(()=>stopped.RevokePeerWithNotification(revision,f.B.State.HostId,inc));
        var closed=new LocalPermissionCall(repo,Owner(f),default,coordinator);closed.Dispose();
        Reject<ObjectDisposedException>(()=>closed.RevokePeerWithNotification(revision,f.B.State.HostId,inc));
        Check(f.A.State.Repository.Read(f.B.State.HostId)!.State=="Active"&&f.B.State.Count("PeerUnpairReceipts")==0);
        var valid=LocalRevoke(f,coordinator);Check(await valid.Notification.WaitAsync(TimeSpan.FromSeconds(5))==PeerUnpairNotification.Confirmed);
    }
    public static async Task RemoteFacadeUsesSameNotificationWithoutEcho()
    {
        await using var f=new Pair();await f.Start();var repo=Grants(f);
        repo.IssueHost(Owner(f),repo.Read().Revision,Guid.NewGuid(),ActorRef.RemoteManager(f.B.State.HostId),
            HostCapability.ManageTrustedManagers,f.A.State.HostId,new(false,false),null);
        await using var coordinator=new PeerUnpairCoordinator(f.A.State.HostId,f.Client().WithConnection<PeerUnpairNotification>);
        Check(await coordinator.Prepare(f.B.State.HostId,f.B.Address));
        // Fixed actual-key fixture evidence at the internal facade, not an incoming RPC claim.
        var actor=new PeerGrantMutationActor(f.A.State.HostId,f.B.State.HostId,f.B.Pin,f.A.Pin,Incarnation(f));
        using var call=new PeerPermissionCall(repo,actor,default,coordinator);
        var result=call.RevokePeerWithNotification(repo.Read().Revision,f.B.State.HostId,actor.Incarnation);
        Check(await result.Notification.WaitAsync(TimeSpan.FromSeconds(5))==PeerUnpairNotification.Confirmed);
        Check(HostDatabase.QueryScalarLong(f.A.State.Writer,$"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked' AND ActorPeerHostId='{f.B.State.HostId:D}';")==1);
        Check(HostDatabase.QueryScalarLong(f.B.State.Writer,$"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked' AND ActorPeerHostId='{f.A.State.HostId:D}';")==1);
        Check(f.A.State.Count("PeerUnpairReceipts")==0&&f.B.State.Count("PeerUnpairReceipts")==1);
    }
    public static async Task CoordinatorRejectsCrossHostWiring()
    {
        await using var f=new Pair();await f.Start();
        await using var foreign=new PeerUnpairCoordinator(f.B.State.HostId,(_,_,_,_)=>Task.FromResult(PeerUnpairNotification.NotAttempted));
        Reject<ArgumentException>(()=>new LocalPermissionCall(Grants(f),Owner(f),default,foreign));
        Reject<ArgumentException>(()=>new PeerPermissionCall(Grants(f),new(f.A.State.HostId,f.B.State.HostId,f.B.Pin,f.A.Pin,Incarnation(f)),default,foreign));
        Reject<ArgumentException>(()=>f.A.Runtime.ConfigureUnpairNotifications(foreign));
        Reject<ArgumentException>(()=>new LocalSecurityRpcRuntime(f.A.State.Database,f.A.State.HostId,new LocalEnrollmentTests.Store(new byte[32]),_=>"native-owner",_=>{}){UnpairNotifications=foreign});
        Check(f.A.Runtime.UnpairNotifications is null&&f.A.State.Repository.Read(f.B.State.HostId)!.State=="Active");
    }
}
