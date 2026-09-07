using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class LocalOwnerActivationTests
{
    private static readonly string Local=new('A',64),Peer=new('B',64);
    private static void Check(bool value) { if(!value) throw new Exception("Owner activation assertion failed."); }
    private static void Denied<T>(Action action) where T:Exception
    { try { action(); } catch(T) { return; } throw new Exception("Expected Owner activation refusal: "+typeof(T).Name); }
    internal sealed class Hook(Action<SqliteConnection,SqliteTransaction>? after=null):IPeerActivationHook
    {
        public void Apply(SqliteConnection c,SqliteTransaction tx,PeerActivationContext activation)
        {
            using var command=c.CreateCommand();command.Transaction=tx;command.CommandText="INSERT INTO ActivationRpcEffects VALUES ($peer);";
            command.Parameters.AddWithValue("$peer",activation.PeerHostId.ToString("D"));command.ExecuteNonQuery();after?.Invoke(c,tx);
        }
    }
    private static void Setup(PeerTrustTests.Fixture f)
    { f.Execute("CREATE TABLE ActivationRpcEffects (Peer TEXT PRIMARY KEY);");f.Repository.RecordVerifiedBinding(f.PeerId,Peer,Local); }
    private static PeerActivationAcknowledgement Ack(PeerTrustTests.Fixture f)=>new(f.PeerId,f.HostId,Local);
    public static Task ExactOwnerAndIdempotentActivation()
    {
        using var f=new PeerTrustTests.Fixture();Setup(f);var owner=LocalOwnerPairingTests.Actor(f);
        foreach(var bad in new[] {owner with {HostId=Guid.NewGuid()},owner with {LocalPrincipalId=Guid.NewGuid()},
            owner with {OsPrincipalRef="other-native"},owner with {PublicVerificationKey="changed"}})
        {
            Denied<AuthenticationException>(()=>f.Repository.PrepareOwnerActivationAcknowledgement(bad,f.PeerId,Peer,Local));
            Denied<AuthenticationException>(()=>f.Repository.AcceptOwnerActivationAcknowledgement(bad,f.PeerId,Peer,Local,Ack(f),new Hook()));
        }
        Denied<ArgumentNullException>(()=>f.Repository.PrepareOwnerActivationAcknowledgement(null!,f.PeerId,Peer,Local));
        Denied<ArgumentNullException>(()=>f.Repository.AcceptOwnerActivationAcknowledgement(null!,f.PeerId,Peer,Local,Ack(f),new Hook()));
        Denied<InvalidOperationException>(()=>f.Repository.PrepareOwnerActivationAcknowledgement(owner,f.PeerId,new('C',64),Local));
        Check(f.Repository.PrepareOwnerActivationAcknowledgement(owner,f.PeerId,Peer,Local).RecordedHostId==f.PeerId);
        Check(f.Repository.AcceptOwnerActivationAcknowledgement(owner,f.PeerId,Peer,Local,Ack(f),new Hook())==PeerActivationDisposition.Activated);
        Check(f.Repository.AcceptOwnerActivationAcknowledgement(owner,f.PeerId,Peer,Local,Ack(f),new Hook())==PeerActivationDisposition.AlreadyActive);
        Check(f.Count("ActivationRpcEffects")==1 && f.Count("HostCapabilityGrants")==0 && f.Count("ServerCapabilityGrants")==0);
        f.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");
        Denied<AuthenticationException>(()=>f.Repository.AcceptOwnerActivationAcknowledgement(owner,f.PeerId,Peer,Local,Ack(f),new Hook()));
        Check(f.Count("ActivationRpcEffects")==1);
        return Task.CompletedTask;
    }
    public static Task HookAndCancellationRollback()
    {
        foreach(var mode in new[] {0,1,2,3})
        {
            using var f=new PeerTrustTests.Fixture();Setup(f);using var cancellation=new CancellationTokenSource();var owner=LocalOwnerPairingTests.Actor(f);
            var hook=new Hook((c,tx)=>
            {
                if(mode==0) {using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;";cmd.ExecuteNonQuery();}
                if(mode==1)cancellation.Cancel();
                if(mode==2)throw new IOException("fixture hook failure");
                if(mode==3)f.Time.Now+=TimeSpan.FromMinutes(31);
            });
            void Activate()=>f.Repository.AcceptOwnerActivationAcknowledgement(owner,f.PeerId,Peer,Local,Ack(f),hook,cancellation.Token);
            if(mode==0)Denied<AuthenticationException>(Activate);
            if(mode==1)Denied<OperationCanceledException>(Activate);
            if(mode==2)Denied<IOException>(Activate);
            if(mode==3)Denied<InvalidOperationException>(Activate);
            Check(f.Repository.Read(f.PeerId)!.State=="PeerBound" && f.Count("ActivationRpcEffects")==0);
            Check(HostDatabase.QueryScalarText(f.Writer,"SELECT PublicVerificationKey FROM LocalPrincipals WHERE IsOwner=1;")=="fixture-public");
            Check(HostDatabase.QueryScalarLong(f.Writer,"SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerActivated';")==0);
            Check(f.Count("HostCapabilityGrants")==0 && f.Count("ServerCapabilityGrants")==0);
        }
        return Task.CompletedTask;
    }
    public static async Task FinalWriterFreshness()
    {
        foreach(var cancel in new[] {false,true})
        {
            using var f=new PeerTrustTests.Fixture();Setup(f);using var cancellation=new CancellationTokenSource();var owner=LocalOwnerPairingTests.Actor(f);
            f.Repository.AuthorizePairingOwner(owner);
            using var tx=f.Writer.BeginTransaction();using var cmd=f.Writer.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText=cancel?"UPDATE LocalPrincipals SET DisplayName='queued' WHERE IsOwner=1;":"UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;";cmd.ExecuteNonQuery();
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work=Task.Run(()=>{entered.SetResult();return f.Repository.AcceptOwnerActivationAcknowledgement(owner,f.PeerId,Peer,Local,Ack(f),new Hook(),cancellation.Token);});
            try {await entered.Task;await Task.Delay(100);Check(!work.IsCompleted);if(cancel)cancellation.Cancel();} finally {tx.Commit();}
            try {await work.WaitAsync(TimeSpan.FromSeconds(10));throw new Exception("Queued stale activation succeeded.");}
            catch(AuthenticationException) when(!cancel) { }
            catch(OperationCanceledException) when(cancel) { }
            Check(f.Repository.Read(f.PeerId)!.State=="PeerBound" && f.Count("ActivationRpcEffects")==0);
        }
    }
}
