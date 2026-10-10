using System.Security.Authentication;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using HostCapability=PalworldServerManager.Core.Authorization.HostCapability;

namespace PalworldServerManager.Host;

internal sealed partial class LocalPeerTrustNetworkActions
{
    private Task<T> Remote<T>(HostNetworkGeneration generation,LocalPrincipalConnectionAuthentication connection,Guid peer,HostReachableAddress address,
        Func<Action<CancellationToken>,CancellationToken,Task<T>> work,CancellationToken ct)
    {
        RequireTarget(generation,peer,address);
        return generation.RunAsync(async token=>
        {
            var actor=connection.GetCurrentPrincipal().MutationActor;
            void Authorize(CancellationToken cancellation)
            {
                if(connection.GetCurrentPrincipal().MutationActor!=actor)throw new AuthenticationException();
                grants.RequireLocalHostCapability(actor,HostCapability.ManageTrustedManagers,peer,cancellation);
            }
            Authorize(token);return await work(Authorize,token).ConfigureAwait(false);
        },ct);
    }
    internal Task<PeerTrustPageReply> ReadRemote(HostNetworkGeneration generation,LocalPrincipalConnectionAuthentication connection,Guid peer,HostReachableAddress address,
        Guid? after,int limit,CancellationToken ct)
        =>Remote(generation,connection,peer,address,(authorize,token)=>generation.ReadManagedPeerTrustAsync(peer,address.HttpsAddress,after,limit,authorize,token),ct);
    internal Task<PeerRevokeTrustReply> RevokeRemote(HostNetworkGeneration generation,LocalPrincipalConnectionAuthentication connection,Guid peer,HostReachableAddress address,
        Guid target,long revision,long incarnation,CancellationToken ct)
        =>Remote(generation,connection,peer,address,(authorize,token)=>generation.RevokeManagedPeerTrustAsync(peer,address.HttpsAddress,target,revision,incarnation,authorize,token),ct);
}
