using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Host;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static partial class WindowsPeerProcessFixture
{
    internal sealed record RecoveryProcessState(Guid Approval,bool Required,bool Confirmed,long Receipts,
        Guid? ReceiptApproval,long ReceivedAudits,long ConfirmationAudits);
    internal static RecoveryProcessState RecoveryState(FixtureHost host)
    {
        host.RequireFixture();host.RequireNoGrants(0);
        using var c=host.Database.OpenConnection();using var tx=c.BeginTransaction(deferred:true);
        using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="SELECT m.ReplacementId,t.PeerRecoveryRequired,m.ConfirmedUtc FROM TrustedManagers t JOIN PeerReplacementCompletions m ON m.PeerHostId=t.PeerHostId WHERE t.PeerHostId=$peer AND m.InvalidatedUtc IS NULL;";
        q.Parameters.AddWithValue("$peer",host.Config.Peer.ToString("D"));
        using var reader=q.ExecuteReader();Check(reader.Read(),"Missing original recovery marker.");
        var approval=Guid.Parse(reader.GetString(0));var required=reader.GetBoolean(1);var confirmed=!reader.IsDBNull(2);reader.Close();
        long Count(string sql)
        {using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;cmd.Parameters.AddWithValue("$peer",host.Config.Peer.ToString("D"));return (long)cmd.ExecuteScalar()!;}
        using var receipt=c.CreateCommand();receipt.Transaction=tx;receipt.CommandText="SELECT ApprovalId FROM PeerRecoveryCompletionReceipts WHERE PeerHostId=$peer;";
        receipt.Parameters.AddWithValue("$peer",host.Config.Peer.ToString("D"));var id=receipt.ExecuteScalar() as string;
        return new(approval,required,confirmed,Count("SELECT COUNT(*) FROM PeerRecoveryCompletionReceipts WHERE PeerHostId=$peer;"),id is null?null:Guid.Parse(id),
            Count("SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerRecoveryCompletionReceived' AND ActorPeerHostId=$peer;"),
            Count("SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerRecoveryCompletionConfirmed' AND ActorPeerHostId=$peer;"));
    }
    private static async Task<int> RunRecoveryChild(FixtureHost host,CancellationToken ct)
    {
        var config=host.Config;
        Check(config.NativePath is null&&config.NativeHash is null&&!config.PauseBeforePeerBound&&!config.PauseResponderBeforePeerBound,"Ambiguous recovery fixture mode.");
        var identity=await host.Credential(ct);var certificate=await host.Cache.LoadAsync(host.State.Read().CurrentReference!,ct);
        await using var generation=new HostNetworkGeneration(certificate);
        var runtime=new PeerSecurityRpcRuntime(host.Database,config.Host,new Activation());
        byte[] publicKey;using(var key=certificate.GetECDsaPublicKey()!)publicKey=key.ExportSubjectPublicKeyInfo();
        var pairing=new PeerPairingRpcRuntime(host.Database,config.Host,publicKey,new RefusingPairing(),(_,_)=>{});
        generation.SetPeerWork(runtime,pairing,new WindowsPeerHttpTransportFactory(certificate));
        var peer=WindowsHostComposition.BuildPeerApplication(runtime,certificate,new(IPAddress.Loopback,0),generation.BindConnection);generation.AddListener(peer);
        var pairingApp=WindowsHostComposition.BuildPairingApplication(pairing,certificate,new(IPAddress.Loopback,0),generation.BindConnection);generation.AddListener(pairingApp);
        generation.ConfigurePeerEndpoints(()=>(new(peer.Urls.Single()),new(pairingApp.Urls.Single())));
        var instance=Guid.NewGuid();using var output=new SemaphoreSlim(1,1);var armed=0;
        async Task Send(string kind)
        {
            var state=RecoveryState(host);var trust=host.Peers.Read(config.Peer)!;
            var report=new Report(kind,Environment.ProcessId,instance,config.Host,identity.Pin,identity.Key,generation.Endpoints!.Value.Peer,
                trust.CurrentFingerprint,trust.PendingRotationId,trust.State,trust.ExpiresUtc,PairingAddress:generation.Endpoints.Value.Pairing,Recovery:state);
            await output.WaitAsync(ct);
            try{await Console.Out.WriteLineAsync(JsonSerializer.Serialize(report).AsMemory(),ct);await Console.Out.FlushAsync(ct);}
            finally{output.Release();}
        }
        peer.Use(async(context,next)=>
        {
            var method=config.RecoveryFault is 1 or 2 or 5?"/ReceiveRecoveryCompletion":"/ConfirmRecoveryCompletionOffer";
            if(config.RecoveryFault==6||!context.Request.Path.Value!.EndsWith(method,StringComparison.Ordinal)||Interlocked.Exchange(ref armed,1)!=0)
            {await next(context);return;}
            if(config.RecoveryFault is 1 or 3 or 5)
            {
                await Send("recovery-barrier");
                await Task.Delay(Timeout.Infinite,ct); // Actual child-side hang; intentionally outlives a client disconnect.
                return;
            }
            // The real RPC commits, but its bytes never reach the peer before termination.
            // The parent also proves its real ResponseAsync is pending at this barrier.
            var original=context.Response.Body;
            using var buffer=new MemoryStream(new byte[PeerSecurityRpcService.MaximumMessageBytes],0,PeerSecurityRpcService.MaximumMessageBytes,true,true);
            buffer.SetLength(0);context.Response.Body=buffer;
            try
            {
                await next(context);await context.Response.BodyWriter.FlushAsync(ct);
                Check(buffer.Length is >5 and <=PeerSecurityRpcService.MaximumMessageBytes,"Expected a bounded withheld RPC response.");
                await Send("recovery-barrier");await Task.Delay(Timeout.Infinite,ct);
            }
            finally{context.Response.Body=original;}
        });
        await generation.StartAsync(ct);await Send("ready");
        using var input=Console.OpenStandardInput();
        while(true)
        {
            var tag=new byte[1];if(await input.ReadAsync(tag,ct)==0)break;
            Check(tag[0]==6,"Recovery child accepts only its fixed public state probe.");
            var size=new byte[2];await input.ReadExactlyAsync(size,ct);var length=BinaryPrimitives.ReadUInt16BigEndian(size);
            Check(length is >0 and <=512,"Invalid recovery control length.");var bytes=new byte[length];await input.ReadExactlyAsync(bytes,ct);
            var address=new Uri(new UTF8Encoding(false,true).GetString(bytes));RequireLoopback(address);
            Check(address==generation.Endpoints!.Value.Peer,"Recovery control targets another endpoint.");await Send("recovery-state");
        }
        await generation.StopAsync();return 0;
    }
}
