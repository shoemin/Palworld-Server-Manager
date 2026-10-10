using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public sealed record LocalPeerReplacementItem(Guid ReplacementId,Guid PeerHostId,string ExpectedState,
    string? ExpectedFingerprint,string ProposedFingerprint,DateTimeOffset VerifiedUtc,DateTimeOffset ExpiresUtc);
public sealed record LocalPeerReplacementPage(Guid HostId,long Revision,IReadOnlyList<LocalPeerReplacementItem> Candidates,Guid? NextAfterReplacement);

public sealed partial class GrantPolicyRepository
{
    // Public candidate observations for the current Owner. Approval independently checks
    // all current evidence, expiry, trust/incarnation, defaults and actual Owner again.
    public LocalPeerReplacementPage ReadLocalPeerReplacements(LocalPrincipalMutationActor owner,Guid? afterReplacement,int limit,CancellationToken ct=default)
    {
        if(limit is <1 or >16||afterReplacement==Guid.Empty)throw new ArgumentException("Invalid replacement page.");
        ct.ThrowIfCancellationRequested();var writer=LocalWriter(owner);
        using var c=Open();using var tx=c.BeginTransaction(deferred:false);
        var before=Read(c,tx);writer.Require(c,tx,before);
        AuthorizeOrAudit(c,tx,writer,before.Revision,new("ReadPeerReplacements",hostId,null,"OwnerReplacementPage"),()=>
        {
            if(!before.Policy.IsOwner(writer.Actual))throw new UnauthorizedAccessException("Current local Owner required.");
            return true;
        },ct);
        var candidates=new List<LocalPeerReplacementItem>();
        using(var q=Command(c,tx,"""
            SELECT ReplacementId,PeerHostId,ExpectedTrustState,ExpectedCurrentTrustedPublicKeyFingerprint,
                ProposedKeyFingerprint,VerifiedUtc,ExpiresUtc
            FROM PendingCredentialReplacements
            WHERE ApprovedUtc IS NULL AND InvalidatedUtc IS NULL AND ReplacementId>$after COLLATE BINARY
            ORDER BY ReplacementId COLLATE BINARY LIMIT $limit;
            """,("$after",afterReplacement?.ToString("D")??""),("$limit",limit+1)))
        {
            using var r=q.ExecuteReader();while(r.Read())
            {
                ct.ThrowIfCancellationRequested();var id=ParseId(r.GetString(0));var peer=ParseId(r.GetString(1));var state=r.GetString(2);
                var old=r.IsDBNull(3)?null:r.GetString(3);var proposed=r.GetString(4);
                var verified=ParseTime(r.GetString(5));var expires=ParseTime(r.GetString(6));
                if(peer==hostId||state is not ("PeerBound" or "Active" or "Revoked")||
                    (old is not null&&!HostTrustPlanning.Fingerprint(old))||!HostTrustPlanning.Fingerprint(proposed)||expires<=verified)throw Corrupt();
                candidates.Add(new(id,peer,state,old,proposed,verified,expires));
            }
        }
        Guid? next=candidates.Count>limit?candidates[limit-1].ReplacementId:null;
        if(candidates.Count>limit)candidates.RemoveAt(limit);
        var after=Read(c,tx);writer.Require(c,tx,after);RequireRevision(before.Revision,after.Revision);
        if(!after.Policy.IsOwner(writer.Actual))throw new UnauthorizedAccessException("Current local Owner required.");
        ct.ThrowIfCancellationRequested();tx.Commit();return new(hostId,after.Revision,candidates.ToArray(),next);
    }
}
