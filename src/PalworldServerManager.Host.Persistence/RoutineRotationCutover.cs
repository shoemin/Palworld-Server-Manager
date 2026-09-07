using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class HostCredentialStateRepository
{
    private (HostCredentialSnapshot Snapshot, HostRotationMetadata Rotation) RequireCutoverScope(
        SqliteConnection c, SqliteTransaction tx, LocalPrincipalMutationActor owner, HostRotationProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var snapshot = RequireRoutineOwner(c, tx, owner);
        var rotation = snapshot.Rotations.SingleOrDefault(r => r.RotationId == proposal.RotationId) ?? throw RoutineDenied();
        if (proposal.HostId != _hostId || rotation.State is not (HostCredentialRotationState.Staging or HostCredentialRotationState.ReadyForCutover or HostCredentialRotationState.CutOver))
            throw RoutineDenied();
        var old = snapshot.Credentials.Single(c => c.Reference == rotation.OldReference).PublicKeyFingerprint!;
        var next = snapshot.Credentials.Single(c => c.Reference == rotation.NewReference).PublicKeyFingerprint!;
        if (ReadProposal(c, tx, proposal.RotationId, (old, next)) != proposal) throw RoutineDenied();
        return (snapshot, rotation);
    }
    public RoutineRotationPreparation InspectRoutineRotationCutover(LocalPrincipalMutationActor owner, HostRotationProposal proposal)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        var scope = RequireCutoverScope(c, tx, owner, proposal); return RotationResult(scope.Snapshot, scope.Rotation);
    }
    // Trusted Host-only mutation seam. Caller holds its machine lease, quiesces every
    // connection/mutation and has durably staged the local descriptor before invoking this.
    // The callback checks actual process-local evidence; no public snapshot/history is proof.
    public RoutineRotationPreparation CommitRoutineRotationCutover(LocalPrincipalMutationActor owner, HostRotationProposal proposal,
        Func<RoutineRotationPeerSet, bool> hasCurrentAcceptance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hasCurrentAcceptance); ct.ThrowIfCancellationRequested();
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        var scope = RequireCutoverScope(c, tx, owner, proposal); ct.ThrowIfCancellationRequested();
        if (scope.Rotation.State == HostCredentialRotationState.CutOver) return RotationResult(scope.Snapshot, scope.Rotation);
        var peers = ReadRoutineRotationPeerSet(c, tx, proposal.RotationId);
        if (peers.Peers.Any(p => p.State != "Active" || p.RecoveryRequired) || !hasCurrentAcceptance(peers)) throw RoutineDenied();
        var next = scope.Rotation.NewReference!;
        if (Ready(c, tx, next) != proposal.NewFingerprint) throw RoutineDenied();
        var now = DateTimeOffset.UtcNow.ToString("O");
        Execute(c, tx, """
            UPDATE HostIdentity SET CurrentCredentialRef=$new WHERE Id=1;
            UPDATE SecureCredentialReferences SET ActivatedUtc=$now WHERE CredentialRef=$new;
            UPDATE HostCredentialRotations SET State='CutOver' WHERE RotationId=$rotation;
            """, ("$new", next), ("$now", now), ("$rotation", proposal.RotationId.ToString("D")));
        RotationAudit(c, tx, owner, proposal.RotationId, "HostRoutineRotationCutOver", now);
        // Time spent waiting for the writer or audit is never free acceptance time.
        var final = RequireCutoverScope(c, tx, owner, proposal);
        if (PeerRevision(c, tx) != peers.Revision || !hasCurrentAcceptance(peers)) throw RoutineDenied();
        ct.ThrowIfCancellationRequested(); tx.Commit(); return RotationResult(final.Snapshot, final.Rotation);
    }
}
