using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed record RoutineRotationCompletionAssessment(RoutineRotationPreparation Rotation,IReadOnlyList<Guid> UnresolvedPeers)
{
    public bool Ready => UnresolvedPeers.Count==0;
}

public sealed partial class HostCredentialStateRepository
{
    private (HostCredentialSnapshot Snapshot,HostRotationMetadata Rotation) RequireCompletionScope(
        SqliteConnection c,SqliteTransaction tx,LocalPrincipalMutationActor owner,Guid rotationId,string actualLocalFingerprint)
    {
        if(rotationId==Guid.Empty || !HostTrustPlanning.Fingerprint(actualLocalFingerprint))throw RoutineDenied();
        var snapshot=RequireRoutineOwner(c,tx,owner);
        var rotation=snapshot.Rotations.SingleOrDefault(r=>r.RotationId==rotationId)??throw RoutineDenied();
        if(rotation.State is not (HostCredentialRotationState.CutOver or HostCredentialRotationState.Completed) ||
            rotation.NewReference is null || snapshot.CurrentReference!=rotation.NewReference ||
            snapshot.Credentials.Single(c=>c.Reference==rotation.NewReference).PublicKeyFingerprint!=actualLocalFingerprint)throw RoutineDenied();
        return(snapshot,rotation);
    }
    private IReadOnlyList<Guid> UnresolvedCompletionPeers(SqliteConnection c,SqliteTransaction tx,Guid rotationId,string actualLocalFingerprint)
    {
        using var command=Command(c,tx,"""
            SELECT t.PeerHostId,t.State,t.PeerRecoveryRequired,t.CurrentTrustedPublicKeyFingerprint,i.Incarnation,
                EXISTS(SELECT 1 FROM HostRotationPromotionEvidence e WHERE e.RotationId=$rotation
                    AND e.PeerHostId=t.PeerHostId AND e.Incarnation=i.Incarnation)
                OR EXISTS(SELECT 1 FROM HostRotationCurrentCredentialEvidence e WHERE e.RotationId=$rotation
                    AND e.PeerHostId=t.PeerHostId AND e.Incarnation=i.Incarnation)
                OR EXISTS(SELECT 1 FROM PeerLocalBindingEvidence e WHERE e.PeerHostId=t.PeerHostId
                    AND e.Incarnation=i.Incarnation AND e.LocalFingerprint=$local)
            FROM TrustedManagers t LEFT JOIN PeerRelationshipIncarnations i ON i.PeerHostId=t.PeerHostId
            WHERE t.State<>'Revoked' ORDER BY t.PeerHostId;
            """,("$rotation",rotationId.ToString("D")),("$local",actualLocalFingerprint));
        using var reader=command.ExecuteReader();var unresolved=new List<Guid>();
        while(reader.Read())
        {
            if(!Guid.TryParseExact(reader.GetString(0),"D",out var peer) || peer==Guid.Empty || peer==_hostId)
                throw new InvalidDataException("Invalid retirement peer identity.");
            if(reader.GetString(1)!="Active" || reader.GetInt32(2)!=0 || reader.IsDBNull(3) ||
                !HostTrustPlanning.Fingerprint(reader.GetString(3)) || reader.IsDBNull(4) || reader.GetInt64(4)<=0 || reader.GetInt32(5)!=1)
                unresolved.Add(peer);
        }
        return unresolved.AsReadOnly();
    }
    public RoutineRotationCompletionAssessment InspectRoutineRotationCompletion(LocalPrincipalMutationActor owner,Guid rotationId,string actualLocalFingerprint)
    {
        using var c=Open();using var tx=c.BeginTransaction(deferred:true);
        var scope=RequireCompletionScope(c,tx,owner,rotationId,actualLocalFingerprint);
        return new(RotationResult(scope.Snapshot,scope.Rotation),scope.Rotation.State==HostCredentialRotationState.Completed
            ? Array.Empty<Guid>() : UnresolvedCompletionPeers(c,tx,rotationId,actualLocalFingerprint));
    }
    // Trusted Host persistence seam only: caller holds the machine lease and has successfully
    // drained EVERY network generation/background writer. Completion releases Old to startup
    // reconciliation; never invoke from an online incoming request or an unchecked snapshot.
    public RoutineRotationPreparation CommitRoutineRotationCompletionWhileQuiesced(LocalPrincipalMutationActor owner,Guid rotationId,
        string actualLocalFingerprint,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var scope=RequireCompletionScope(c,tx,owner,rotationId,actualLocalFingerprint);ct.ThrowIfCancellationRequested();
        if(scope.Rotation.State==HostCredentialRotationState.Completed)return RotationResult(scope.Snapshot,scope.Rotation);
        if(UnresolvedCompletionPeers(c,tx,rotationId,actualLocalFingerprint).Count!=0)throw RoutineDenied();
        var revision=PeerRevision(c,tx);var now=DateTimeOffset.UtcNow.ToString("O");
        Execute(c,tx,"UPDATE HostCredentialRotations SET State='Completed',CompletedUtc=$now WHERE RotationId=$rotation;",
            ("$now",now),("$rotation",rotationId.ToString("D")));
        RotationAudit(c,tx,owner,rotationId,"HostRoutineRotationCompleted",now);
        var final=RequireCompletionScope(c,tx,owner,rotationId,actualLocalFingerprint);
        if(final.Rotation!=scope.Rotation with {State=HostCredentialRotationState.Completed} || PeerRevision(c,tx)!=revision ||
            UnresolvedCompletionPeers(c,tx,rotationId,actualLocalFingerprint).Count!=0)throw RoutineDenied();
        ct.ThrowIfCancellationRequested();tx.Commit();return RotationResult(final.Snapshot,final.Rotation);
    }
}
