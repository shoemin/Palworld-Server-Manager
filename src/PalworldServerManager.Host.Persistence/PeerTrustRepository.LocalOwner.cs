using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class PeerTrustRepository
{
    // Trusted Host primitive: the actor must come from the authenticated local connection.
    // This read is only an early gate; outbound binding repeats it in its final writer transaction.
    public void AuthorizePairingOwner(LocalPrincipalMutationActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        RequireHost(c, tx); RequirePairingOwner(c, tx, actor);
    }
    public PeerBindingResult RecordOwnerVerifiedBinding(LocalPrincipalMutationActor actor, Guid peer,
        string peerFingerprint, string verifiedLocalFingerprint)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return RecordBindingCore(peer, peerFingerprint, verifiedLocalFingerprint, actor);
    }
    private void RequirePairingOwner(SqliteConnection c, SqliteTransaction tx, LocalPrincipalMutationActor actor)
    {
        static AuthenticationException Denied() => new("Active local Owner authorization is required.");
        if (actor.HostId != hostId || actor.LocalPrincipalId == Guid.Empty || string.IsNullOrWhiteSpace(actor.OsPrincipalRef)
            || actor.OsPrincipalRef.Length > 256 || string.IsNullOrEmpty(actor.PublicVerificationKey)) throw Denied();
        using var command = Command(c, tx, """
            SELECT COUNT(*) FROM LocalPrincipals WHERE LocalPrincipalId=$id AND OsPrincipalRef=$native
                AND PublicVerificationKey=$key AND State='Active' AND IsOwner=1;
            """, ("$id", Id(actor.LocalPrincipalId)), ("$native", actor.OsPrincipalRef), ("$key", actor.PublicVerificationKey));
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw Denied();
    }
}
