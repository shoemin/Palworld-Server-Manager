using System.Net;
using System.Security.Authentication;
using Grpc.Net.Client;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

internal enum PeerUnpairDelivery { Confirmed=1, Unconfirmed=2 }

// Preparation is separate from the revoke command. The trusted callback owns the entire
// retained connection lifetime; a captured connection becomes unusable after it returns.
internal sealed class PeerUnpairConnectionFactory(PeerSecurityRpcRuntime runtime,IPeerHttpTransportFactory transport)
{
    internal async Task<T> WithConnection<T>(Guid peer,Uri address,Func<PeerUnpairConnection,CancellationToken,Task<T>> work,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(work);ArgumentNullException.ThrowIfNull(address);
        if(peer==Guid.Empty||peer==runtime.HostId||!address.IsAbsoluteUri||address.Scheme!="https"||
            address.UserInfo.Length!=0||address.AbsolutePath!="/"||address.Query.Length!=0||address.Fragment.Length!=0)
            throw new ArgumentException("A peer HTTPS address is required.");
        ct.ThrowIfCancellationRequested();
        using var connection=transport.Create(pin=>runtime.Authentication.AdmitHandshake(peer,pin,PeerTrafficPurpose.OrdinaryManagement),
            actual=>runtime.Authentication.Authenticate(peer,actual.PeerFingerprint,PeerTrafficPurpose.OrdinaryManagement));
        using var channel=GrpcChannel.ForAddress(address,new GrpcChannelOptions
        {
            HttpHandler=connection.Handler,HttpVersion=HttpVersion.Version20,HttpVersionPolicy=HttpVersionPolicy.RequestVersionExact,
            MaxReceiveMessageSize=PeerSecurityRpcService.MaximumMessageBytes,MaxSendMessageSize=PeerSecurityRpcService.MaximumMessageBytes
        });
        var client=new PeerSecurityProtocol.PeerSecurityProtocolClient(channel);var hello=PeerSecurityRpcRuntime.Hello(runtime.HostId);
        PeerGrantMutationActor proof;
        using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var reply=await client.NegotiateAsync(hello,cancellationToken:timeout.Token).ResponseAsync.ConfigureAwait(false);
            if(reply.Host is null||PeerSecurityRpcService.Id(reply.Host.HostId)!=peer)throw new AuthenticationException("Peer identity refused.");
            NegotiatedProtocol.Negotiate(hello.Handshake,reply.Handshake).Require(FeatureCapability.PeerUnpair);
            var actual=connection.Identity;
            proof=new(runtime.HostId,peer,actual.PeerFingerprint,actual.LocalFingerprint,
                runtime.Repository.ReadAuthenticatedRelationshipIncarnation(peer,actual.PeerFingerprint,actual.LocalFingerprint));
            runtime.Authentication.AuthenticateOrdinary(proof,timeout.Token);
        }
        await using var held=new PeerUnpairConnection(runtime,client,channel,proof,ct);
        return await work(held,ct).ConfigureAwait(false);
    }
}

internal sealed class PeerUnpairConnection : IAsyncDisposable
{
    private readonly PeerSecurityRpcRuntime runtime;
    private readonly PeerSecurityProtocol.PeerSecurityProtocolClient client;
    private readonly GrpcChannel channel;
    private readonly PeerGrantMutationActor proof;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly CancellationTokenSource stopping;
    private readonly CancellationToken lifetime;
    private readonly TaskCompletionSource closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposing,attempted;
    internal PeerUnpairConnection(PeerSecurityRpcRuntime runtime,PeerSecurityProtocol.PeerSecurityProtocolClient client,
        GrpcChannel channel,PeerGrantMutationActor proof,CancellationToken ct)
    {
        this.runtime=runtime;this.client=client;this.channel=channel;this.proof=proof;
        stopping=CancellationTokenSource.CreateLinkedTokenSource(ct);lifetime=stopping.Token;
    }
    internal async Task<PeerUnpairDelivery> Send(PeerTrustRevocationResult revoked)
    {
        if(Volatile.Read(ref disposing)!=0)return PeerUnpairDelivery.Unconfirmed;
        try{await gate.WaitAsync(lifetime).ConfigureAwait(false);}
        catch(OperationCanceledException){return PeerUnpairDelivery.Unconfirmed;}
        try
        {
            if(Volatile.Read(ref disposing)!=0||Interlocked.Exchange(ref attempted,1)!=0)return PeerUnpairDelivery.Unconfirmed;
            runtime.RequireCommittedUnpair(proof,revoked,lifetime);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime);timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var reply=await client.ReceiveUnpairAsync(new(){ReceivingHostId=proof.PeerHostId.ToString("D")},
                cancellationToken:timeout.Token).ResponseAsync.ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            PeerUnpairWire.ValidateReply(reply,proof.PeerHostId,proof.HostId);
            runtime.RequireCommittedUnpair(proof,revoked,timeout.Token);
            return PeerUnpairDelivery.Confirmed;
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){return PeerUnpairDelivery.Unconfirmed;}
        finally{gate.Release();}
    }
    public ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposing,1)==0)_=Close();
        return new(closed.Task);
    }
    private async Task Close()
    {
        Exception? failure=null;
        try
        {
            try{await stopping.CancelAsync().ConfigureAwait(false);}catch(Exception ex){failure=ex;}
            try{channel.Dispose();}catch(Exception ex){failure??=ex;} // Abort HTTP before draining, even if cancellation callbacks fail.
            await gate.WaitAsync().ConfigureAwait(false);gate.Release();
            stopping.Dispose();
            if(failure is null)closed.TrySetResult();else closed.TrySetException(failure);
        }
        catch(Exception ex){closed.TrySetException(ex);}
    }
}
