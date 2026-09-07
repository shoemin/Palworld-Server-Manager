using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

// Public protocol data only; the trusted wire adapter must bind it to completed current-key TLS.
public sealed record HostRotationProposal(Guid HostId, Guid RotationId, long Sequence, string OldFingerprint, string NewFingerprint);

public sealed partial class HostCredentialStateRepository
{
    private static (string Old, string New) ProposalPins(HostCredentialSnapshot snapshot, Guid rotationId)
    {
        if (!snapshot.Initialized) throw RoutineDenied();
        HostTrustPlanning.Build(snapshot);
        var rotation = snapshot.Rotations.SingleOrDefault(r => r.RotationId == rotationId) ?? throw RoutineDenied();
        if (rotation.State is not (HostCredentialRotationState.Staging or HostCredentialRotationState.ReadyForCutover) ||
            rotation.OldReference != snapshot.CurrentReference || rotation.NewReference is null) throw RoutineDenied();
        var old = snapshot.Credentials.Single(c => c.Reference == rotation.OldReference).PublicKeyFingerprint!;
        var next = snapshot.Credentials.Single(c => c.Reference == rotation.NewReference).PublicKeyFingerprint!;
        return (old, next);
    }
    private HostRotationProposal? ReadProposal(SqliteConnection c, SqliteTransaction tx, Guid rotationId, (string Old, string New) pins)
    {
        using var command = Command(c, tx, "SELECT ProposalSequence,OldFingerprint,NewFingerprint FROM HostRotationProposals WHERE RotationId=$id;", ("$id", rotationId.ToString("D")));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        if (reader.GetInt64(0) <= 0 || reader.GetString(1) != pins.Old || reader.GetString(2) != pins.New)
            throw new InvalidDataException("Rotation proposal metadata changed.");
        return new(_hostId, rotationId, reader.GetInt64(0), pins.Old, pins.New);
    }
    public HostRotationProposal PrepareRoutineRotationProposal(LocalPrincipalMutationActor owner, Guid rotationId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        var snapshot = RequireRoutineOwner(c, tx, owner); var pins = ProposalPins(snapshot, rotationId);
        if (ReadProposal(c, tx, rotationId, pins) is { } replay) return replay;
        var now = DateTimeOffset.UtcNow.ToString("O");
        Execute(c, tx, "INSERT INTO HostRotationProposals (RotationId,OldFingerprint,NewFingerprint,PreparedUtc) VALUES ($id,$old,$new,$now);",
            ("$id", rotationId.ToString("D")), ("$old", pins.Old), ("$new", pins.New), ("$now", now));
        RotationAudit(c, tx, owner, rotationId, "HostRoutineRotationProposalPrepared", now);
        var proposal = ReadProposal(c, tx, rotationId, pins)!; tx.Commit(); return proposal;
    }
    // Trusted Host's non-mutating retransmission path for an already Owner-prepared proposal.
    public HostRotationProposal ReadRoutineRotationProposal(Guid rotationId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        var pins = ProposalPins(Read(c, tx), rotationId);
        return ReadProposal(c, tx, rotationId, pins) ?? throw new InvalidOperationException("Rotation proposal has not been prepared.");
    }
}
