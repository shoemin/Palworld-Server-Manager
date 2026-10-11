using System.Security.Authentication;
using PalworldServerManager.Contracts;
using PalworldServerManager.Host.Persistence;
using ServerRef = PalworldServerManager.Core.Authorization.ServerRef;
using ServerCapability = PalworldServerManager.Core.Authorization.ServerCapability;

namespace PalworldServerManager.Host;

internal sealed class LocalSettingsSnapshotNetworkActions(HostDatabase database, Guid host, TimeProvider? time = null)
{
    private readonly GrantPolicyRepository grants = new(database, host, time);
    internal Task<PalworldServerManager.Contracts.Wire.SettingsSnapshot> Read(HostNetworkGeneration generation,
        LocalPrincipalConnectionAuthentication connection, ServerRef server, HostReachableAddress address, CancellationToken ct)
    {
        if (generation.HostId != host) throw new AuthenticationException();
        if (server.AuthoritativeHostId == host) throw new ArgumentException("Remote server required.");
        return generation.RunAsync(async token =>
        {
            var actor = connection.GetCurrentPrincipal().MutationActor;
            void Authorize(CancellationToken cancellation)
            {
                if (connection.GetCurrentPrincipal().MutationActor != actor) throw new AuthenticationException();
                grants.RequireLocalServerCapability(actor, ServerCapability.ViewServer, server, cancellation);
            }
            Authorize(token);
            var wire = new PalworldServerManager.Contracts.Wire.ServerRef
                { AuthoritativeHostId = server.AuthoritativeHostId.ToString("D"), ServerProfileId = server.ServerProfileId.ToString("D") };
            return await generation.ReadSettingsSnapshotAsync(wire, address.HttpsAddress, Authorize, token).ConfigureAwait(false);
        }, ct);
    }
}
