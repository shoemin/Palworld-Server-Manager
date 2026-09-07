using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Host.Persistence;

public enum PeerRotationResolution { Unchanged = 1, Renewed = 2, Cleared = 3 }

public sealed partial class PeerTrustRepository
{
    private readonly object rotationQueryScope = new();
    private static readonly TimeSpan RotationQueryLifetime = TimeSpan.FromSeconds(20);
    private void RequireFreshRotationQuery(RotationStatusQuery query)
    {
        var elapsed = time.GetElapsedTime(query.Started);
        if (elapsed < TimeSpan.Zero || elapsed >= RotationQueryLifetime) throw RotationRefused();
    }

    // Host-memory-only single-use intent. Never restored from client/wire data after restart.
    public sealed class RotationStatusQuery
    {
        public RoutineRotationStatusRequest Request { get; }
        internal object Scope { get; }
        internal DateTimeOffset OriginalDeadline { get; }
        internal long Started { get; }
        internal string LocalFingerprint { get; }
        internal LocalPrincipalMutationActor? Owner { get; }
        private int consumed;
        internal RotationStatusQuery(object scope, RoutineRotationStatusRequest request, DateTimeOffset deadline,
            long started, LocalPrincipalMutationActor? owner, string localFingerprint)
        { Scope = scope; Request = request; OriginalDeadline = deadline; Started = started; Owner = owner; LocalFingerprint = localFingerprint; }
        internal bool Consume() => Interlocked.Exchange(ref consumed, 1) == 0;
    }
    private void RequireRotationOwner(SqliteConnection c, SqliteTransaction tx, LocalPrincipalMutationActor owner)
    {
        if (owner.HostId != hostId || owner.LocalPrincipalId == Guid.Empty) throw RotationRefused();
        using var command = Command(c, tx, """
            SELECT COUNT(*) FROM LocalPrincipals WHERE LocalPrincipalId=$id AND OsPrincipalRef=$os
                AND PublicVerificationKey=$key AND State='Active' AND IsOwner=1;
            """, ("$id", Id(owner.LocalPrincipalId)), ("$os", owner.OsPrincipalRef), ("$key", owner.PublicVerificationKey));
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw RotationRefused();
    }
    // Non-null owner means explicit approval to renew this exact retained rotation, obtained
    // through actual local authentication. Opportunistic queries pass null and cannot renew.
    public RotationStatusQuery BeginPeerRotationStatusQuery(Guid peer, LocalPrincipalMutationActor? owner = null)
    {
        Id(peer); using var c = Open(); using var tx = c.BeginTransaction(deferred: true); var local = RequireHost(c, tx);
        var row = Read(c, tx, peer) ?? throw RotationRefused();
        var trust = RequireObservedActivePeer(c, tx, peer, row.CurrentFingerprint ?? throw RotationRefused());
        if (trust.PendingFingerprint is null) throw RotationRefused();
        if (owner is not null) RequireRotationOwner(c, tx, owner);
        return new(rotationQueryScope, new(Guid.NewGuid(), peer, trust.PendingRotationId!.Value,
            trust.CurrentFingerprint!, trust.PendingFingerprint), trust.PendingRotationExpiresUtc!.Value, time.GetTimestamp(), owner, local);
    }
    // Called only after this Host initiated a fresh query and the transport bound the exact
    // reply to negotiated peer UUID and completed mutual TLS. Reply content is never proof.
    public PeerRotationResolution CompletePeerRotationStatusQuery(RotationStatusQuery query, RoutineRotationStatusReply reply, string actualFingerprint, string actualLocalFingerprint)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(reply);
        if (!ReferenceEquals(query.Scope, rotationQueryScope) || !query.Consume()) throw RotationRefused();
        if (reply.Request != query.Request || !Enum.IsDefined(reply.State)) throw RotationRefused();
        Fingerprint(actualFingerprint);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        if (actualLocalFingerprint != query.LocalFingerprint || RequireHost(c, tx) != actualLocalFingerprint) throw RotationRefused();
        // This completion handles Old-only status. Actual New presentation is handled by the
        // ordinary observation path before RPC and makes this captured pending state stale.
        var trust = RequireObservedActivePeer(c, tx, query.Request.HostId, actualFingerprint);
        if (actualFingerprint != query.Request.OldFingerprint || trust.CurrentFingerprint != query.Request.OldFingerprint ||
            trust.PendingFingerprint != query.Request.NewFingerprint || trust.PendingRotationId != query.Request.RotationId ||
            trust.PendingRotationExpiresUtc != query.OriginalDeadline) throw RotationRefused();
        RequireFreshRotationQuery(query);
        var now = time.GetUtcNow();
        if (reply.State is RoutineRotationLiveState.Aborted or RoutineRotationLiveState.Unknown)
        {
            Execute(c, tx, """
                UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint=NULL,PendingRotationId=NULL,
                    PendingRotationExpiresUtc=NULL,PendingReconfirmationRequired=0 WHERE PeerHostId=$peer;
                """, ("$peer", Id(trust.PeerHostId)));
            Audit(c, tx, trust.PeerHostId, "PeerRotationAbandonmentConfirmed", now);
            RequireFreshRotationQuery(query); tx.Commit(); return PeerRotationResolution.Cleared;
        }
        if (reply.State is RoutineRotationLiveState.Staging or RoutineRotationLiveState.ReadyForCutover && query.Owner is { } owner &&
            (trust.PendingReconfirmationRequired || trust.PendingRotationExpiresUtc <= now))
        {
            RequireRotationOwner(c, tx, owner);
            Execute(c, tx, "UPDATE TrustedManagers SET PendingRotationExpiresUtc=$expires,PendingReconfirmationRequired=0 WHERE PeerHostId=$peer;",
                ("$expires", Stamp(now + RotationStagingLifetime)), ("$peer", Id(trust.PeerHostId)));
            Execute(c, tx, """
                INSERT INTO AuditEvents (AuditEventId,OccurredUtc,EventKind,ActorKind,ActorLocalPrincipalId,AffectedHostId,Summary)
                    VALUES ($id,$now,'PeerRotationReconfirmed','LocalPrincipal',$owner,$host,$summary);
                """, ("$id", Id(Guid.NewGuid())), ("$now", Stamp(now)), ("$owner", Id(owner.LocalPrincipalId)),
                ("$host", Id(hostId)), ("$summary", $"Peer rotation reconfirmed: peer {trust.PeerHostId:D}, rotation {query.Request.RotationId:D}."));
            RequireFreshRotationQuery(query); tx.Commit(); return PeerRotationResolution.Renewed;
        }
        MarkRotationLapsed(c, tx, trust, now); RequireFreshRotationQuery(query); tx.Commit(); return PeerRotationResolution.Unchanged;
    }
}
