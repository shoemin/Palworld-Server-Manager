using System.Security.Authentication;
using PalworldServerManager.Contracts;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

internal sealed record LocalPeerRecoveryContact(PeerRecoveryCompletionExchange Sent,PeerRecoveryPullExchange Received);

// Fixed trusted actions. No SQL transaction or synchronous PermissionCall spans network work.
internal sealed class LocalPeerTrustNetworkActions(HostDatabase database,Guid host,TimeProvider? time=null)
{
    private readonly GrantPolicyRepository grants=new(database,host,time);
    private readonly PeerTrustRepository peers=new(database,host,time);
    private void RequireTarget(HostNetworkGeneration generation,Guid peer,HostReachableAddress address)
    {
        ArgumentNullException.ThrowIfNull(generation);ArgumentNullException.ThrowIfNull(address);
        if(generation.HostId!=host)throw new AuthenticationException("Network generation belongs to another Host.");
        if(peer==Guid.Empty||peer==host)throw new ArgumentException("Remote peer required.");
    }
    internal Task<bool> Prepare(HostNetworkGeneration generation,LocalPrincipalConnectionAuthentication connection,
        Guid peer,HostReachableAddress address,CancellationToken ct)
    {
        RequireTarget(generation,peer,address);
        return generation.RunAsync(async token=>
        {
            var actor=connection.GetCurrentPrincipal().MutationActor;
            void Authorize()
            {
                if(connection.GetCurrentPrincipal().MutationActor!=actor)throw new AuthenticationException();
                grants.RequireLocalHostCapability(actor,HostCapability.ManageTrustedManagers,host,token);
            }
            Authorize();token.ThrowIfCancellationRequested();
            using var preparation=CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                var ready=await (generation.UnpairNotifications??throw new InvalidOperationException()).Prepare(peer,address.HttpsAddress,preparation.Token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();Authorize();return ready;
            }
            catch
            {
                // Cancel this admission outside SQL. Actual cleanup remains generation-owned.
                await preparation.CancelAsync().ConfigureAwait(false);throw;
            }
        },ct);
    }
    internal Task<LocalPeerRecoveryContact> CompleteRecovery(HostNetworkGeneration generation,LocalPrincipalConnectionAuthentication connection,
        Guid peer,HostReachableAddress address,CancellationToken ct)
    {
        RequireTarget(generation,peer,address);
        return generation.RunAsync(async token=>
        {
            var actor=connection.GetCurrentPrincipal().MutationActor;
            void Owner()
            {
                if(connection.GetCurrentPrincipal().MutationActor!=actor)throw new AuthenticationException();
                peers.AuthorizePairingOwner(actor);token.ThrowIfCancellationRequested();
            }
            Owner();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var sent=await generation.ConfirmRecoveryAsync(peer,address.HttpsAddress,deadline.Token).ConfigureAwait(false);
            Owner();deadline.Token.ThrowIfCancellationRequested();
            var received=await generation.PullRecoveryAsync(peer,address.HttpsAddress,deadline.Token).ConfigureAwait(false);
            Owner();deadline.Token.ThrowIfCancellationRequested();return new LocalPeerRecoveryContact(sent,received);
        },ct);
    }
}
