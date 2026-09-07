using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using Authority=PalworldServerManager.Core.Authorization;

namespace PalworldServerManager.Host.Persistence;

// This public message is NOT proof by itself. The Host must receive it over completed
// pinned mutual TLS, with both actual connection fingerprints supplied separately.
public sealed record PeerActivationAcknowledgement(Guid FromHostId, Guid RecordedHostId, string RecordedFingerprint);
public enum PeerActivationDisposition { Activated = 1, AlreadyActive = 2 }
// Trusted caller provenance, never a request-body claim or an authentication substitute.
public sealed record PeerActivationContext(Guid AuthoritativeHostId, Guid PeerHostId, DateTimeOffset ActivatedUtc,
    Authority.ActorRef InitiatingActor);

// Trusted Host composition only. #45 must apply configured defaults through canonical
// issuance using this SAME transaction. No network calls, nested commit or external side
// effects: a rollback must undo every effect. There is deliberately no default/no-op hook.
public interface IPeerActivationHook
{
    // The per-call guard runs AFTER the enclosing trust audit, still before commit. It must
    // validate these exact effects without committing or retaining state across activations.
    Action Apply(SqliteConnection connection, SqliteTransaction transaction, PeerActivationContext activation);
}

public sealed partial class PeerTrustRepository
{
    private PeerTrustRecord RequireActivationPeer(SqliteConnection c, SqliteTransaction tx, Guid peer,
        string actualPeerFingerprint, string actualLocalFingerprint, DateTimeOffset now)
    {
        if (peer == hostId || RequireHost(c, tx) != actualLocalFingerprint) throw ActivationRefused();
        var trust = Read(c, tx, peer);
        if (trust is null || trust.RecoveryRequired || trust.State is not ("PeerBound" or "Active") ||
            (trust.CurrentFingerprint != actualPeerFingerprint && trust.PendingFingerprint != actualPeerFingerprint))
            throw ActivationRefused();
        if (trust.State == "PeerBound" && (trust.LocalBoundFingerprint != actualLocalFingerprint ||
            trust.ExpiresUtc is null || trust.ExpiresUtc <= now)) throw ActivationRefused();
        return trust;
    }

    // Read committed durable identity only; sending this message does NOT activate locally.
    // Use the actual completed TLS connection's remote/local public fingerprints, never
    // request parameters or a connection-authentication snapshot from an earlier RPC.
    public PeerActivationAcknowledgement PrepareActivationAcknowledgement(Guid peer,
        string actualPeerFingerprint, string actualLocalFingerprint)
        => PrepareActivationCore(peer, actualPeerFingerprint, actualLocalFingerprint, null);
    public PeerActivationAcknowledgement PrepareOwnerActivationAcknowledgement(LocalPrincipalMutationActor owner, Guid peer,
        string actualPeerFingerprint, string actualLocalFingerprint)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return PrepareActivationCore(peer, actualPeerFingerprint, actualLocalFingerprint, owner);
    }
    private PeerActivationAcknowledgement PrepareActivationCore(Guid peer, string actualPeerFingerprint,
        string actualLocalFingerprint, LocalPrincipalMutationActor? owner)
    {
        Id(peer); Fingerprint(actualPeerFingerprint); Fingerprint(actualLocalFingerprint);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        RequireActivationPeer(c, tx, peer, actualPeerFingerprint, actualLocalFingerprint, time.GetUtcNow());
        if (owner is not null) RequirePairingOwner(c, tx, owner);
        return new(hostId, peer, actualPeerFingerprint);
    }

    public PeerActivationDisposition AcceptActivationAcknowledgement(Guid peer,
        string actualPeerFingerprint, string actualLocalFingerprint,
        PeerActivationAcknowledgement acknowledgement, IPeerActivationHook hook)
        => AcceptActivationCore(peer, actualPeerFingerprint, actualLocalFingerprint, acknowledgement, hook, null, CancellationToken.None);
    public PeerActivationDisposition AcceptOwnerActivationAcknowledgement(LocalPrincipalMutationActor owner, Guid peer,
        string actualPeerFingerprint, string actualLocalFingerprint, PeerActivationAcknowledgement acknowledgement,
        IPeerActivationHook hook, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return AcceptActivationCore(peer, actualPeerFingerprint, actualLocalFingerprint, acknowledgement, hook, owner, ct);
    }
    private PeerActivationDisposition AcceptActivationCore(Guid peer, string actualPeerFingerprint, string actualLocalFingerprint,
        PeerActivationAcknowledgement acknowledgement, IPeerActivationHook hook, LocalPrincipalMutationActor? owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(acknowledgement); ArgumentNullException.ThrowIfNull(hook);
        Id(peer); Fingerprint(actualPeerFingerprint); Fingerprint(actualLocalFingerprint);
        if (acknowledgement.FromHostId != peer || acknowledgement.RecordedHostId != hostId ||
            acknowledgement.RecordedFingerprint != actualLocalFingerprint) throw ActivationRefused();
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        var now = time.GetUtcNow();
        var trust = RequireActivationPeer(c, tx, peer, actualPeerFingerprint, actualLocalFingerprint, now);
        if (owner is not null) RequirePairingOwner(c, tx, owner);
        ct.ThrowIfCancellationRequested();
        if (trust.State == "Active") return PeerActivationDisposition.AlreadyActive;

        Execute(c, tx, """
            UPDATE TrustedManagers SET State='Active',PairedUtc=$now WHERE PeerHostId=$peer AND State='PeerBound';
            """, ("$now", Stamp(now)), ("$peer", Id(peer)));
        var initiator=owner is null?Authority.ActorRef.RemoteManager(peer):Authority.ActorRef.LocalPrincipal(owner.LocalPrincipalId);
        var validateEffects = hook.Apply(c, tx, new(hostId, peer, now, initiator))
            ?? throw new InvalidOperationException("Activation effect validation is required.");
        Audit(c, tx, peer, "PeerActivated", now);
        validateEffects();
        // A slow hook, audit or final validator cannot extend the original pending window. The transaction
        // prevents other writers from changing trust while these effects are prepared.
        if (trust.ExpiresUtc <= time.GetUtcNow()) throw ActivationRefused();
        if (owner is not null) RequirePairingOwner(c, tx, owner);
        ct.ThrowIfCancellationRequested();
        tx.Commit();
        return PeerActivationDisposition.Activated;
    }
    private static InvalidOperationException ActivationRefused() => new("Peer activation refused.");
}
