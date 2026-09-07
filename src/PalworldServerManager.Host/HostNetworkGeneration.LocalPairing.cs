using System.Runtime.ExceptionServices;
using PalworldServerManager.Contracts;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

internal sealed partial class HostNetworkGeneration
{
    private LocalPrincipalMutationActor PairingOwner(LocalPrincipalConnectionAuthentication connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var actor = connection.GetCurrentPrincipal().MutationActor;
        Required(pairing).Repository.AuthorizePairingOwner(actor); return actor;
    }
    internal Task<IReadOnlyList<UnverifiedHostEndpoint>> DiscoverForOwnerAsync(LocalPrincipalConnectionAuthentication connection, CancellationToken ct = default)
        => RunAsync(token =>
        {
            PairingOwner(connection); token.ThrowIfCancellationRequested();
            return Task.FromResult(Required(discovery).Snapshot());
        }, ct);
    internal Task<PairingInvitation> CreateInvitationForOwnerAsync(LocalPrincipalConnectionAuthentication connection, CancellationToken ct = default)
        => CreateInvitationForOwnerAsync(connection, invitation => invitation, ct);
    // Trusted synchronous response construction stays inside the same ownership/cleanup scope.
    internal Task<T> CreateInvitationForOwnerAsync<T>(LocalPrincipalConnectionAuthentication connection,
        Func<PairingInvitation, T> deliver, CancellationToken ct = default)
        => RunAsync(token =>
        {
            ArgumentNullException.ThrowIfNull(deliver);
            var actor = PairingOwner(connection); token.ThrowIfCancellationRequested();
            PairingInvitation? invitation = null;
            try
            {
                // No database transaction spans Sweep/audit callbacks or code creation.
                invitation = Required(pairing).CreateInvitation();
                token.ThrowIfCancellationRequested(); Required(pairing).Repository.AuthorizePairingOwner(actor);
                return Task.FromResult(deliver(invitation));
            }
            catch (Exception primary)
            {
                var failures = new List<Exception> { primary };
                if (invitation is not null)
                {
                    try { Required(pairing).CancelInvitation(invitation.Id); } catch (Exception ex) { failures.Add(ex); }
                    try { invitation.Dispose(); } catch (Exception ex) { failures.Add(ex); }
                }
                ExceptionDispatchInfo.Capture(failures.Count == 1 ? primary : new AggregateException(failures)).Throw(); throw;
            }
        }, ct);
    internal Task CancelInvitationForOwnerAsync(LocalPrincipalConnectionAuthentication connection, Guid invitation, CancellationToken ct = default)
        => RunAsync(token =>
        {
            PairingOwner(connection); token.ThrowIfCancellationRequested();
            Required(pairing).CancelInvitation(invitation); return Task.CompletedTask;
        }, ct);
    internal Task<PeerPairingCompletion> PairForOwnerAsync(LocalPrincipalConnectionAuthentication connection, HostReachableAddress address,
        RedactedSecret code, CancellationToken ct = default)
        => RunAsync(token =>
        {
            var actor = PairingOwner(connection); ArgumentNullException.ThrowIfNull(address); token.ThrowIfCancellationRequested();
            return Required(pairingClient).PairForOwnerAsync(address.HttpsAddress, code, actor, token);
        }, ct);
}
