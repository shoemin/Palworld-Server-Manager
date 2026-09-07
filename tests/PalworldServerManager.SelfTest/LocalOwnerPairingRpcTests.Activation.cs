using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.SelfTest;

internal static partial class LocalOwnerPairingRpcTests
{
    private static Task<LocalHandshakeReply> NegotiateActivation(LocalSecurityRpcTests.Client client,bool pairing=false)
    {
        var hello=new Handshake {Protocol=new(){Major=1,Minor=9},ProductVersion="display"};
        hello.Capabilities.Add(FeatureCapability.LocalPrincipalSecurity);hello.Capabilities.Add(FeatureCapability.LocalOwnerPeerActivation);
        if(pairing)hello.Capabilities.Add(FeatureCapability.LocalOwnerPairing);
        return client.Call<Handshake,LocalHandshakeReply>("Negotiate",hello);
    }
    private static Task<LocalActivatePeerReply> Activate(LocalSecurityRpcTests.Client client,Guid peer,Uri address)
        =>client.Call<LocalActivatePeerRequest,LocalActivatePeerReply>("ActivatePeer",new(){PeerHostId=peer.ToString("D"),ReachableHost=address.Host,PeerPort=(uint)address.Port});
    public static async Task ActivationBoundaries()
    {
        await using(var local=new LocalSecurityRpcTests.Fixture(true))
        {
            await local.Start();using var client=local.Connect();var reply=await NegotiateActivation(client);
            Check(!reply.Handshake.Capabilities.Contains(FeatureCapability.LocalOwnerPeerActivation));
            await Refused(Activate(client,Guid.NewGuid(),new("https://127.0.0.1:1")),StatusCode.FailedPrecondition);
        }
        await using var f=new Fixture();await f.Start(new RefusingFactory());
        using(var pairingOnly=await f.Authorized())await Refused(Activate(pairingOnly,Guid.NewGuid(),new("https://127.0.0.1:1")),StatusCode.FailedPrecondition);
        using var owner=f.Client();await NegotiateActivation(owner);
        await Refused(Activate(owner,Guid.NewGuid(),new("https://127.0.0.1:1")),StatusCode.Unauthenticated);
        await owner.Authenticate(f.State.State.HostId,f.Principal,f.Key);
        await Refused(owner.Call<LocalActivatePeerRequest,LocalActivatePeerReply>("ActivatePeer",new(){PeerHostId="bad",ReachableHost="127.0.0.1",PeerPort=1}),StatusCode.InvalidArgument);
        await Refused(owner.Call<LocalActivatePeerRequest,LocalActivatePeerReply>("ActivatePeer",new(){PeerHostId=Guid.NewGuid().ToString("D"),ReachableHost="127.0.0.1",PeerPort=65536}),StatusCode.InvalidArgument);
        await Refused(Activate(owner,Guid.NewGuid(),f.Generation.Endpoints!.Value.Peer),StatusCode.Unavailable);
        f.State.State.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=1;");
        await Refused(Activate(owner,Guid.NewGuid(),new("https://127.0.0.1:1")),StatusCode.Unauthenticated);
        await using var user=new Fixture(false);await user.Start(new RefusingFactory());using var clientUser=user.Client();await NegotiateActivation(clientUser);
        await clientUser.Authenticate(user.State.State.HostId,user.Principal,user.Key);
        await Refused(Activate(clientUser,Guid.NewGuid(),new("https://127.0.0.1:1")),StatusCode.Unauthenticated);
        Check(f.State.State.Count("ActivationRpcEffects")==0 && user.State.State.Count("ActivationRpcEffects")==0);
    }
    internal static async Task NativeActivation(IPairingKeyExchangeFactory provider)
    {
        await using var a=new Fixture();await using var b=new Fixture();
        var replacement=new WindowsLocalPrincipalCryptography().Generate();
        try
        {
            var remoteHook=new LocalOwnerActivationTests.Hook((_,_)=>a.State.State.Execute($"UPDATE LocalPrincipals SET PublicVerificationKey='{Convert.ToBase64String(replacement.PublicKey)}' WHERE IsOwner=1;"));
            await a.Start(provider);await b.Start(provider,remoteHook);
            using var localA=a.Client();using var localB=b.Client();await NegotiateActivation(localA,true);await NegotiateActivation(localB,true);
            await localA.Authenticate(a.State.State.HostId,a.Principal,a.Key);await localB.Authenticate(b.State.State.HostId,b.Principal,b.Key);
            var invitation=await Create(localB);await Pair(localA,b.Generation.Endpoints!.Value.Pairing,invitation.Code);
            await Refused(Activate(localA,b.State.State.HostId,b.Generation.Endpoints.Value.Peer),StatusCode.Unauthenticated);
            Check(a.State.State.Repository.Read(b.State.State.HostId)!.State=="PeerBound" && b.State.State.Repository.Read(a.State.State.HostId)!.State=="Active");
            Check(a.State.State.Count("ActivationRpcEffects")==0 && b.State.State.Count("ActivationRpcEffects")==1);
            using var retry=a.Client();await NegotiateActivation(retry);await retry.Authenticate(a.State.State.HostId,a.Principal,replacement);
            Check((await Activate(retry,b.State.State.HostId,b.Generation.Endpoints.Value.Peer)).Result==PeerActivationResult.Activated);
            Check((await Activate(retry,b.State.State.HostId,b.Generation.Endpoints.Value.Peer)).Result==PeerActivationResult.AlreadyActive);
            Check(a.State.State.Repository.Read(b.State.State.HostId)!.State=="Active" && b.State.State.Repository.Read(a.State.State.HostId)!.State=="Active");
            Check(a.State.State.Count("ActivationRpcEffects")==1 && b.State.State.Count("ActivationRpcEffects")==1);
            Check(a.State.State.Count("HostCapabilityGrants")==0 && a.State.State.Count("ServerCapabilityGrants")==0
                && b.State.State.Count("HostCapabilityGrants")==0 && b.State.State.Count("ServerCapabilityGrants")==0);
            Console.WriteLine("PASS actual local Owner activation: remote Active/local PeerBound after Owner change, reauthenticated pinned retry, one hook effect per Host and no real grants.");
        }
        finally {CryptographicOperations.ZeroMemory(replacement.PrivateKey);}
    }
}
