using System.Buffers.Binary;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static partial class WindowsPeerProcessFixture
{
    internal sealed record UnpairProcessState(string State,long Incarnation,long Receipts,long LocalAudits,long PeerAudits,long Activations);
    internal static UnpairProcessState UnpairState(FixtureHost host)
    {
        host.RequireFixture();
        using var c=host.Database.OpenConnection();using var tx=c.BeginTransaction(deferred:true);
        long Count(string sql)
        {
            using var q=c.CreateCommand();q.Transaction=tx;q.CommandText=sql;
            q.Parameters.AddWithValue("$peer",host.Config.Peer.ToString("D"));q.Parameters.AddWithValue("$owner",host.Config.Owner.ToString("D"));
            return (long)q.ExecuteScalar()!;
        }
        Check(Count("SELECT COUNT(*) FROM HostCapabilityGrants;")==0&&Count("SELECT COUNT(*) FROM ServerCapabilityGrants;")==0&&
            Count("SELECT COUNT(*) FROM LocalPrincipals WHERE IsOwner=1 AND State='Active';")==1&&
            Count("SELECT COUNT(*) FROM LocalPrincipals WHERE LocalPrincipalId=$owner AND IsOwner=1 AND State='Active';")==1,
            "Unpair changed fixture grants or Owner.");
        using var state=c.CreateCommand();state.Transaction=tx;state.CommandText="SELECT State FROM TrustedManagers WHERE PeerHostId=$peer;";
        state.Parameters.AddWithValue("$peer",host.Config.Peer.ToString("D"));
        return new((string)state.ExecuteScalar()!,Count("SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId=$peer;"),
            Count("SELECT COUNT(*) FROM PeerUnpairReceipts WHERE PeerHostId=$peer;"),
            Count("SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked' AND ActorLocalPrincipalId=$owner;"),
            Count("SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerTrustRevoked' AND ActorPeerHostId=$peer;"),
            Count("SELECT COUNT(*) FROM PeerProcessFixtureActivation;"));
    }
    // Real native credential/generation/listeners; the initial verified binding is an explicit
    // fixture input, not a PAKE claim. No fault injection exists in the installed Host.
    internal static async Task<HostNetworkGeneration> UnpairGeneration(FixtureHost host,Action<WebApplication>? configure,CancellationToken ct)
    {
        var certificate=await host.Cache.LoadAsync(host.State.Read().CurrentReference!,ct);
        var generation=new HostNetworkGeneration(certificate);
        try
        {
            var runtime=new PeerSecurityRpcRuntime(host.Database,host.Config.Host,new Activation());
            byte[] publicKey;using(var key=certificate.GetECDsaPublicKey()!)publicKey=key.ExportSubjectPublicKeyInfo();
            var pairing=new PeerPairingRpcRuntime(host.Database,host.Config.Host,publicKey,new RefusingPairing(),(_,_)=>{});
            generation.SetPeerWork(runtime,pairing,new WindowsPeerHttpTransportFactory(certificate));
            var peer=WindowsHostComposition.BuildPeerApplication(runtime,certificate,new(IPAddress.Loopback,0),generation.BindConnection);generation.AddListener(peer);
            var pair=WindowsHostComposition.BuildPairingApplication(pairing,certificate,new(IPAddress.Loopback,0),generation.BindConnection);generation.AddListener(pair);
            generation.ConfigurePeerEndpoints(()=>(new(peer.Urls.Single()),new(pair.Urls.Single())));
            configure?.Invoke(peer);await generation.StartAsync(ct);return generation;
        }
        catch{await generation.DisposeAsync();throw;}
    }
    private static async Task<int> RunUnpairChild(FixtureHost host,CancellationToken ct)
    {
        var config=host.Config;
        Check(config.NativePath is null&&config.NativeHash is null&&!config.PauseBeforePeerBound&&!config.PauseResponderBeforePeerBound,"Ambiguous unpair fixture mode.");
        var identity=await host.Credential(ct);var instance=Guid.NewGuid();using var output=new SemaphoreSlim(1,1);var armed=0;
        HostNetworkGeneration? active=null;
        async Task Send(string kind)
        {
            var state=UnpairState(host);var trust=host.Peers.Read(config.Peer)!;
            var report=new Report(kind,Environment.ProcessId,instance,config.Host,identity.Pin,identity.Key,active!.Endpoints!.Value.Peer,
                trust.CurrentFingerprint,trust.PendingRotationId,trust.State,trust.ExpiresUtc,PairingAddress:active.Endpoints.Value.Pairing,Unpair:state);
            await output.WaitAsync(ct);
            try{await Console.Out.WriteLineAsync(JsonSerializer.Serialize(report).AsMemory(),ct);await Console.Out.FlushAsync(ct);}
            finally{output.Release();}
        }
        await using var generation=await UnpairGeneration(host,peer=>peer.Use(async(context,next)=>
        {
            if(config.UnpairFault==4||!context.Request.Path.Value!.EndsWith("/ReceiveUnpair",StringComparison.Ordinal)||Interlocked.Exchange(ref armed,1)!=0)
            {await next(context);return;}
            if(config.UnpairFault is 1 or 3)
            {
                await Send("unpair-barrier");await Task.Delay(Timeout.Infinite,ct);return;
            }
            // Commit the actual receiving RPC, withholding its bounded response until killed.
            var original=context.Response.Body;
            using var buffer=new MemoryStream(new byte[PeerSecurityRpcService.MaximumMessageBytes],0,PeerSecurityRpcService.MaximumMessageBytes,true,true);
            buffer.SetLength(0);context.Response.Body=buffer;
            try
            {
                await next(context);await context.Response.BodyWriter.FlushAsync(ct);
                Check(buffer.Length is >5 and <=PeerSecurityRpcService.MaximumMessageBytes,"Missing bounded unpair response.");
                await Send("unpair-barrier");await Task.Delay(Timeout.Infinite,ct);
            }
            finally{context.Response.Body=original;}
        }),ct);
        active=generation;await Send("ready");using var input=Console.OpenStandardInput();
        while(true)
        {
            var tag=new byte[1];if(await input.ReadAsync(tag,ct)==0)break;
            Check(tag[0] is 7 or 8,"Unpair child accepts only fixed public state/refusal probes.");
            var size=new byte[2];await input.ReadExactlyAsync(size,ct);var length=BinaryPrimitives.ReadUInt16BigEndian(size);
            Check(length is >0 and <=512,"Invalid unpair control length.");var bytes=new byte[length];await input.ReadExactlyAsync(bytes,ct);
            var address=new Uri(new UTF8Encoding(false,true).GetString(bytes));RequireLoopback(address);
            if(tag[0]==7){Check(address==generation.Endpoints!.Value.Peer,"Unpair state probe targets another endpoint.");await Send("unpair-state");continue;}
            var refused=false;
            try{await generation.ActivateAsync(config.Peer,address,ct);}
            catch(Exception ex)when(ex is AuthenticationException or RpcException or HttpRequestException){refused=true;}
            Check(refused&&!ct.IsCancellationRequested,"Revoked trust reconnected or refusal used fixture timeout.");await Send("unpair-refused");
        }
        await generation.StopAsync();return 0;
    }
}
