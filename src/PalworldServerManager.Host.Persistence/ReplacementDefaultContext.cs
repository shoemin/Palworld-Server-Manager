using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class GrantPolicyRepository
{
    // Created only by the Owner replacement writer, for root issuance in its transaction.
    // This policy is never returned or used for ordinary authorization/remote proof.
    private sealed record ApprovedReplacementContext(LocalPrincipalMutationActor Owner,Guid Peer,Guid Replacement,
        long Incarnation,string PeerFingerprint,string LocalFingerprint,DateTimeOffset ApprovedUtc);

    private AuthorizationPolicy ReplacementRecipientPolicy(SqliteConnection c,SqliteTransaction tx,
        AuthorizationSnapshot snapshot,ApprovedReplacementContext context)
    {
        RequireLocal(c,tx,context.Owner,snapshot);
        if(!snapshot.Policy.IsOwner(ActorRef.LocalPrincipal(context.Owner.LocalPrincipalId))||RevocationCredential(c,tx).Fingerprint!=context.LocalFingerprint)
            throw new UnauthorizedAccessException("Current replacement Owner required.");
        using var check=Command(c,tx,"""
            SELECT COUNT(*) FROM TrustedManagers t JOIN PeerRelationshipIncarnations i ON i.PeerHostId=t.PeerHostId
            JOIN PeerReplacementCompletions m ON m.PeerHostId=t.PeerHostId
            JOIN PendingCredentialReplacements r ON r.ReplacementId=m.ReplacementId AND r.PeerHostId=t.PeerHostId
            WHERE t.PeerHostId=$peer AND t.State='Active' AND t.CurrentTrustedPublicKeyFingerprint=$fp
                AND t.PendingTrustedPublicKeyFingerprint IS NULL AND i.Incarnation=$inc
                AND m.ReplacementId=$replacement AND m.ApprovalIncarnation=$inc AND m.CurrentIncarnation=$inc
                AND m.ApprovedPeerFingerprint=$fp AND m.LocalFingerprintAtApproval=$local AND m.ApprovedUtc=$now
                AND m.ConfirmedUtc IS NULL AND m.InvalidatedUtc IS NULL
                AND r.ProposedKeyFingerprint=$fp AND r.ApprovedByOwnerLocalPrincipalId=$owner AND r.ApprovedUtc=$now AND r.InvalidatedUtc IS NULL;
            """,("$peer",Id(context.Peer)),("$fp",context.PeerFingerprint),("$inc",context.Incarnation),
            ("$replacement",Id(context.Replacement)),("$local",context.LocalFingerprint),("$now",Stamp(context.ApprovedUtc)),("$owner",Id(context.Owner.LocalPrincipalId)));
        if(Convert.ToInt32(check.ExecuteScalar())!=1)throw new UnauthorizedAccessException("Exact newly approved replacement recipient required.");
        var locals=new List<Guid>();var peers=new List<Guid>();
        using(var cmd=Command(c,tx,"SELECT LocalPrincipalId FROM LocalPrincipals WHERE State='Active';"))
        {using var reader=cmd.ExecuteReader();while(reader.Read())locals.Add(ParseId(reader.GetString(0)));}
        using(var cmd=Command(c,tx,"SELECT PeerHostId FROM TrustedManagers WHERE State='Active' AND (PeerRecoveryRequired=0 OR PeerHostId=$peer);",("$peer",Id(context.Peer))))
        {using var reader=cmd.ExecuteReader();while(reader.Read())peers.Add(ParseId(reader.GetString(0)));}
        // The local Owner has approved this Active recipient. Its independent recovery
        // flag stays set and normal Read().Policy still denies all use of these roots.
        return new(hostId,context.Owner.LocalPrincipalId,locals,peers,snapshot.HostGrants,snapshot.ServerGrants);
    }
}
