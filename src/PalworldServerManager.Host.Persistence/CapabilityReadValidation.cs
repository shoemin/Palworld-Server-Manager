using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class GrantPolicyRepository
{
    // Trusted Host-owned transaction only. Read-only current validation, never a cached permit,
    // nested writer, denial-audit commit or replacement for initial authorization before IO.
    public void ValidateLocalServerCapability(SqliteConnection c, SqliteTransaction tx, LocalPrincipalMutationActor actor,
        ServerCapability capability, ServerRef target, CancellationToken ct = default)
        => ValidateServerCapability(c, tx, LocalWriter(actor), capability, target, ct);
    public void ValidateRemoteServerCapability(SqliteConnection c, SqliteTransaction tx, PeerGrantMutationActor actor,
        ServerCapability capability, ServerRef target, CancellationToken ct = default)
        => ValidateServerCapability(c, tx, PeerWriter(actor), capability, target, ct);

    private void ValidateServerCapability(SqliteConnection c, SqliteTransaction tx, GrantWriter actor,
        ServerCapability capability, ServerRef target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(capability)) throw new ArgumentException("Known server capability required.");
        database.RequireOwnedTransaction(c, tx); ct.ThrowIfCancellationRequested();
        var snapshot = Read(c, tx); actor.Require(c, tx, snapshot);
        if (!TargetAllowed(actor, target.AuthoritativeHostId, snapshot.Policy) || !snapshot.Policy.CanUseServer(actor.Actual, capability, target))
            throw new UnauthorizedAccessException("Capability use refused.");
        ct.ThrowIfCancellationRequested();
    }
    private bool TargetAllowed(GrantWriter actor, Guid target, AuthorizationPolicy policy)
        => actor.Actual.Kind == ActorKind.RemoteManager ? target == hostId :
            target == hostId || policy.IsActive(ActorRef.RemoteManager(target));
}
