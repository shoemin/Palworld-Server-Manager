using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed record RoutineRotationPreparation(Guid RotationId, string OldReference, string NewReference,
    HostCredentialRotationState State, bool PublicMetadataReady);

public sealed partial class HostCredentialStateRepository
{
    // Trusted local Host entry: actor is the result of actual local authentication,
    // rechecked here against current Owner/native identity/key inside the write transaction.
    private HostCredentialSnapshot RequireRoutineOwner(SqliteConnection c, SqliteTransaction tx, LocalPrincipalMutationActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var snapshot = Read(c, tx);
        if (!snapshot.Initialized || actor.HostId != _hostId || actor.LocalPrincipalId == Guid.Empty) throw RoutineDenied();
        using var command = Command(c, tx, """
            SELECT COUNT(*) FROM LocalPrincipals WHERE LocalPrincipalId=$id AND OsPrincipalRef=$os
                AND PublicVerificationKey=$key AND State='Active' AND IsOwner=1;
            """, ("$id", actor.LocalPrincipalId.ToString("D")), ("$os", actor.OsPrincipalRef), ("$key", actor.PublicVerificationKey));
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw RoutineDenied();
        HostTrustPlanning.Build(snapshot); return snapshot;
    }
    private static AuthenticationException RoutineDenied() => new("Routine Host credential rotation refused.");
    private static RoutineRotationPreparation RotationResult(HostCredentialSnapshot snapshot, HostRotationMetadata rotation)
    {
        if (rotation.OldReference is null || rotation.NewReference is null) throw new InvalidDataException("Rotation credential reference unavailable.");
        var material = snapshot.Credentials.SingleOrDefault(c => c.Reference == rotation.NewReference);
        return new(rotation.RotationId, rotation.OldReference, rotation.NewReference, rotation.State,
            material is { Retired: false } && HostTrustPlanning.Fingerprint(material.PublicKeyFingerprint));
    }
    public RoutineRotationPreparation PrepareRoutineRotation(LocalPrincipalMutationActor owner, Guid requestId)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("Rotation request identity required.");
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false); var snapshot = RequireRoutineOwner(c, tx, owner);
        // Completed/aborted request IDs are never rebound to a new credential pair.
        if (snapshot.Rotations.SingleOrDefault(r => r.RotationId == requestId) is { } replay) return RotationResult(snapshot, replay);
        if (snapshot.Rotations.SingleOrDefault(r => r.State is not (HostCredentialRotationState.Completed or HostCredentialRotationState.Aborted)) is { } active)
            return RotationResult(snapshot, active);
        var current = snapshot.CurrentReference ?? throw RoutineDenied();
        var next = "host-rotation-" + requestId.ToString("N"); var now = DateTimeOffset.UtcNow.ToString("O");
        Execute(c, tx, """
            INSERT INTO SecureCredentialReferences (CredentialRef,Purpose,CreatedUtc) VALUES ($ref,$purpose,$now);
            INSERT INTO HostCredentialRotations (RotationId,OldCredentialRef,NewCredentialRef,State,StartedUtc)
                VALUES ($id,$old,$ref,'Prepared',$now);
            """, ("$id", requestId.ToString("D")), ("$old", current), ("$ref", next), ("$purpose", TlsPurpose), ("$now", now));
        RotationAudit(c, tx, owner, requestId, "HostRoutineRotationPrepared", now);
        tx.Commit(); return new(requestId, current, next, HostCredentialRotationState.Prepared, false);
    }
    // Material generation/validation belongs to the existing secure-store platform flow.
    // This method only makes ready public metadata eligible for local descriptor/peer staging.
    public RoutineRotationPreparation BeginRoutineRotationStaging(LocalPrincipalMutationActor owner, Guid rotationId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false); var snapshot = RequireRoutineOwner(c, tx, owner);
        var rotation = snapshot.Rotations.SingleOrDefault(r => r.RotationId == rotationId) ?? throw RoutineDenied();
        if (rotation.State == HostCredentialRotationState.Staging) return RotationResult(snapshot, rotation);
        if (rotation.State != HostCredentialRotationState.Prepared || snapshot.CurrentReference != rotation.OldReference || rotation.NewReference is null) throw RoutineDenied();
        var next = Ready(c, tx, rotation.NewReference);
        var current = snapshot.Credentials.Single(c => c.Reference == snapshot.CurrentReference).PublicKeyFingerprint;
        if (next == current) throw RoutineDenied();
        Execute(c, tx, "UPDATE HostCredentialRotations SET State='Staging' WHERE RotationId=$id;", ("$id", rotationId.ToString("D")));
        RotationAudit(c, tx, owner, rotationId, "HostRoutineRotationStaging", DateTimeOffset.UtcNow.ToString("O"));
        tx.Commit(); return new(rotationId, rotation.OldReference!, rotation.NewReference, HostCredentialRotationState.Staging, true);
    }
    public RoutineRotationPreparation AbortRoutineRotation(LocalPrincipalMutationActor owner, Guid rotationId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false); var snapshot = RequireRoutineOwner(c, tx, owner);
        var rotation = snapshot.Rotations.SingleOrDefault(r => r.RotationId == rotationId) ?? throw RoutineDenied();
        if (rotation.State == HostCredentialRotationState.Aborted) return RotationResult(snapshot, rotation);
        if (rotation.State is not (HostCredentialRotationState.Prepared or HostCredentialRotationState.Staging or HostCredentialRotationState.ReadyForCutover) ||
            snapshot.CurrentReference != rotation.OldReference) throw RoutineDenied();
        Execute(c, tx, "UPDATE HostCredentialRotations SET State='Aborted' WHERE RotationId=$id;", ("$id", rotationId.ToString("D")));
        RotationAudit(c, tx, owner, rotationId, "HostRoutineRotationAborted", DateTimeOffset.UtcNow.ToString("O"));
        tx.Commit(); return RotationResult(snapshot, rotation with { State = HostCredentialRotationState.Aborted });
    }
    private void RotationAudit(SqliteConnection c, SqliteTransaction tx, LocalPrincipalMutationActor owner, Guid rotationId, string kind, string now)
        => Execute(c, tx, """
            INSERT INTO AuditEvents (AuditEventId,OccurredUtc,EventKind,ActorKind,ActorLocalPrincipalId,AffectedHostId,Summary)
                VALUES ($id,$now,$kind,'LocalPrincipal',$owner,$host,$summary);
            """, ("$id", Guid.NewGuid().ToString("D")), ("$now", now), ("$kind", kind),
            ("$owner", owner.LocalPrincipalId.ToString("D")), ("$host", _hostId.ToString("D")), ("$summary", $"{kind}: rotation {rotationId:D}."));
}
