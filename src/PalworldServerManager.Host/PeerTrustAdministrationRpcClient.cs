using System.Net;
using System.Security.Authentication;
using Google.Protobuf;
using Grpc.Net.Client;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

// Fixed Host-only route. The trusted local action supplies its current-user check;
// the receiving Host separately enforces this machine's ceiling.
internal sealed class PeerTrustAdministrationRpcClient(PeerSecurityRpcRuntime runtime,IPeerHttpTransportFactory transport)
{
    internal Task<PeerTrustPageReply> Read(Guid peer,Uri address,Guid? after,int limit,Action<CancellationToken> authorize,CancellationToken ct)
    {
        if(limit is <1 or >16||after==Guid.Empty)throw new ArgumentException();
        var request=new PeerTrustPageRequest{AuthoritativeHostId=peer.ToString("D"),AfterPeerHostId=after?.ToString("D")??"",Limit=(uint)limit};
        return Send(peer,address,(client,token)=>client.ReadManagedPeerTrustAsync(request,cancellationToken:token).ResponseAsync,
            reply=>ValidatePage(reply,request),authorize,ct);
    }
    internal Task<PeerRevokeTrustReply> Revoke(Guid peer,Uri address,Guid target,long revision,long incarnation,Action<CancellationToken> authorize,CancellationToken ct)
    {
        if(target==Guid.Empty||target==peer||revision<0||incarnation<=0)throw new ArgumentException();
        var request=new PeerRevokeTrustRequest{AuthoritativeHostId=peer.ToString("D"),PeerHostId=target.ToString("D"),ExpectedAuthorizationRevision=revision,ExpectedIncarnation=incarnation};
        return Send(peer,address,(client,token)=>client.RevokeManagedPeerTrustAsync(request,cancellationToken:token).ResponseAsync,
            reply=>ValidateRevocation(reply,request),authorize,ct);
    }
    private async Task<T> Send<T>(Guid peer,Uri address,Func<PeerSecurityProtocol.PeerSecurityProtocolClient,CancellationToken,Task<T>> send,
        Action<T> validate,Action<CancellationToken> authorize,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authorize);ArgumentNullException.ThrowIfNull(address);
        if(peer==Guid.Empty||peer==runtime.HostId||!address.IsAbsoluteUri||address.Scheme!="https"||address.UserInfo.Length!=0||
            address.AbsolutePath!="/"||address.Query.Length!=0||address.Fragment.Length!=0)throw new ArgumentException();
        ct.ThrowIfCancellationRequested();authorize(ct);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        PeerGrantMutationActor? original=null;
        using var connection=transport.Create(pin=>runtime.Authentication.AdmitHandshake(peer,pin,PeerTrafficPurpose.OrdinaryManagement),actual=>
        {
            var incarnation=runtime.Repository.ReadAuthenticatedRelationshipIncarnation(peer,actual.PeerFingerprint,actual.LocalFingerprint);
            var proof=new PeerGrantMutationActor(runtime.HostId,peer,actual.PeerFingerprint,actual.LocalFingerprint,incarnation);
            if(Interlocked.CompareExchange(ref original,proof,null) is not null)throw new AuthenticationException("Fresh peer connection required.");
            runtime.Authentication.AuthenticateOrdinary(proof,deadline.Token);
        });
        using var channel=GrpcChannel.ForAddress(address,new GrpcChannelOptions{HttpHandler=connection.Handler,HttpVersion=HttpVersion.Version20,
            HttpVersionPolicy=HttpVersionPolicy.RequestVersionExact,MaxReceiveMessageSize=PeerSecurityRpcService.MaximumMessageBytes,MaxSendMessageSize=PeerSecurityRpcService.MaximumMessageBytes});
        var client=new PeerSecurityProtocol.PeerSecurityProtocolClient(channel);var hello=PeerSecurityRpcRuntime.Hello(runtime.HostId);
        var reply=await client.NegotiateAsync(hello,cancellationToken:deadline.Token).ResponseAsync.ConfigureAwait(false);
        if(reply.Host is null||Canonical(reply.Host.HostId)!=peer||reply.Handshake is null||reply.Handshake.Capabilities.Count>64||reply.Handshake.ProductVersion.Length>256)
            throw new AuthenticationException("Peer negotiation refused.");
        var protocol=NegotiatedProtocol.Negotiate(hello.Handshake,reply.Handshake);protocol.Require(FeatureCapability.PeerTrustActivation);protocol.Require(FeatureCapability.PeerTrustAdministration);
        void RequireProof()
        {
            deadline.Token.ThrowIfCancellationRequested();var proof=original??throw new AuthenticationException();var actual=connection.Identity;
            if(actual.PeerFingerprint!=proof.PeerFingerprint||actual.LocalFingerprint!=proof.LocalFingerprint)throw new AuthenticationException();
            runtime.Authentication.AuthenticateOrdinary(proof,deadline.Token);
        }
        RequireProof();authorize(deadline.Token);deadline.Token.ThrowIfCancellationRequested();
        var result=await send(client,deadline.Token).ConfigureAwait(false);
        RequireProof();authorize(deadline.Token);validate(result);deadline.Token.ThrowIfCancellationRequested();return result;
    }
    private static Guid Canonical(string value)
        =>Guid.TryParseExact(value,"D",out var id)&&id!=Guid.Empty&&id.ToString("D")==value?id:throw new AuthenticationException("Invalid peer reply.");
    internal static void ValidatePage(PeerTrustPageReply reply,PeerTrustPageRequest request)
    {
        if(reply is null||reply.CalculateSize()>PeerSecurityRpcService.MaximumMessageBytes||reply.AuthoritativeHostId!=request.AuthoritativeHostId||
            reply.AuthorizationRevision<0||request.Limit is <1 or >16||reply.Peers.Count>request.Limit)throw new AuthenticationException("Invalid peer page.");
        var host=Canonical(reply.AuthoritativeHostId);var previous=request.AfterPeerHostId;
        foreach(var row in reply.Peers)
        {
            var peer=Canonical(row.PeerHostId);
            if(peer==host||StringComparer.Ordinal.Compare(previous,row.PeerHostId)>=0||row.Incarnation<=0||
                row.State is not(PeerManagedTrustState.PeerBound or PeerManagedTrustState.Active or PeerManagedTrustState.Revoked)||
                !PublicPin(row.CurrentFingerprint)||!PublicPin(row.PendingFingerprint))throw new AuthenticationException("Invalid peer row.");
            previous=row.PeerHostId;
        }
        if(reply.NextAfterPeerHostId.Length!=0&&(reply.Peers.Count!=request.Limit||reply.NextAfterPeerHostId!=previous))throw new AuthenticationException("Invalid peer continuation.");
    }
    private static bool PublicPin(string value)=>value.Length==0||(value.Length==64&&value.All(c=>c is >= '0' and <= '9' or >= 'A' and <= 'F'));
    internal static void ValidateRevocation(PeerRevokeTrustReply reply,PeerRevokeTrustRequest request)
    {
        if(reply is null||reply.CalculateSize()>PeerSecurityRpcService.MaximumMessageBytes||reply.AuthoritativeHostId!=request.AuthoritativeHostId||reply.PeerHostId!=request.PeerHostId||
            reply.AuthorizationRevision<request.ExpectedAuthorizationRevision||reply.Incarnation<request.ExpectedIncarnation||
            (!reply.Changed&&(reply.AuthorizationRevision!=request.ExpectedAuthorizationRevision||reply.Incarnation!=request.ExpectedIncarnation)))
            throw new AuthenticationException("Invalid peer revocation reply.");
        _=Canonical(reply.AuthoritativeHostId);_=Canonical(reply.PeerHostId);
    }
}
