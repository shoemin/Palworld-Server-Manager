using Microsoft.Data.Sqlite;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class PeerActivationTests
{
    private static readonly string Local = new('A', 64), Peer = new('B', 64), Other = new('C', 64);
    private static void Check(bool value) { if (!value) throw new Exception("Peer activation assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected activation refusal."); }
    // Records a transaction-local test effect. This is not a shipped grant/default provider.
    private sealed class Hook(bool fail = false, Action? afterWrite = null) : IPeerActivationHook
    {
        public void Apply(SqliteConnection c, SqliteTransaction tx, PeerActivationContext activation)
        {
            using var command = c.CreateCommand(); command.Transaction = tx;
            command.CommandText = "INSERT INTO ActivationTestEffects VALUES ($peer,$host,$now);";
            command.Parameters.AddWithValue("$peer", activation.PeerHostId.ToString("D"));
            command.Parameters.AddWithValue("$host", activation.AuthoritativeHostId.ToString("D"));
            command.Parameters.AddWithValue("$now", activation.ActivatedUtc.ToString("O")); command.ExecuteNonQuery();
            afterWrite?.Invoke();
            if (fail) throw new InvalidOperationException("Synthetic hook failure.");
        }
    }
    private static void Setup(PeerTrustTests.Fixture f, Guid peer, string peerFingerprint, string localFingerprint)
    {
        f.Execute("CREATE TABLE ActivationTestEffects (Peer TEXT PRIMARY KEY,Host TEXT NOT NULL,ActivatedUtc TEXT NOT NULL);");
        f.Repository.RecordVerifiedBinding(peer, peerFingerprint, localFingerprint);
    }
    private static PeerActivationAcknowledgement Ack(PeerTrustTests.Fixture f) => new(f.PeerId, f.HostId, Local);
    public static Task ReciprocalReopenAndLostReply()
    {
        using var a = new PeerTrustTests.Fixture(); using var b = new PeerTrustTests.Fixture();
        b.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{Peer}' WHERE CredentialRef='current';");
        Setup(a, b.HostId, Peer, Local);
        Reject<InvalidOperationException>(() => b.Repository.PrepareActivationAcknowledgement(a.HostId, Local, Peer));
        Check(a.Repository.Read(b.HostId)!.State == "PeerBound");
        Setup(b, a.HostId, Local, Peer);
        var ackA = a.Repository.PrepareActivationAcknowledgement(b.HostId, Peer, Local);
        var ackB = b.Repository.PrepareActivationAcknowledgement(a.HostId, Local, Peer);
        Check(a.Count("ActivationTestEffects") == 0 && b.Count("ActivationTestEffects") == 0);
        // Each property access opens a fresh repository/connection. The ephemeral PAKE code
        // no longer exists. This is a durable reopen oracle, not a process-crash/TLS claim.
        a.Time.Now += TimeSpan.FromMinutes(6); b.Time.Now += TimeSpan.FromMinutes(6);
        Check(a.Repository.AcceptActivationAcknowledgement(b.HostId, Peer, Local, ackB, new Hook()) == PeerActivationDisposition.Activated);
        Check(b.Repository.Read(a.HostId)!.State == "PeerBound"); // No distributed commit.
        Check(a.Repository.AcceptActivationAcknowledgement(b.HostId, Peer, Local, ackB, new Hook(true)) == PeerActivationDisposition.AlreadyActive);
        Check(b.Repository.AcceptActivationAcknowledgement(a.HostId, Local, Peer, ackA, new Hook()) == PeerActivationDisposition.Activated);
        Check(a.Repository.PrepareActivationAcknowledgement(b.HostId, Peer, Local) == ackA);
        Check(a.Count("ActivationTestEffects") == 1 && b.Count("ActivationTestEffects") == 1);
        Check(a.Count("HostCapabilityGrants") == 0 && b.Count("ServerCapabilityGrants") == 0);
        Check(HostDatabase.QueryScalarLong(a.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerActivated';") == 1);
        a.Execute($"""
            INSERT INTO HostCapabilityGrants (GrantId,TargetHostId,Capability,GranteeActorKind,GranteePeerHostId,
                GrantedByActorKind,GrantedByLocalPrincipalId,CanDelegate,CanDelegateOnwardDelegation,CreatedUtc)
            VALUES ('retained','{a.HostId:D}','ViewHost','RemoteManager','{b.HostId:D}','LocalPrincipal','{a.OwnerId:D}',0,0,'retained');
            """);
        a.Time.Now += TimeSpan.FromHours(1);
        Check(a.Repository.AcceptActivationAcknowledgement(b.HostId, Peer, Local, ackB, new Hook(true)) == PeerActivationDisposition.AlreadyActive);
        Check(HostDatabase.QueryScalarLong(a.Writer, "SELECT COUNT(*) FROM HostCapabilityGrants WHERE GrantId='retained' AND CreatedUtc='retained' AND InvalidatedUtc IS NULL;") == 1);
        return Task.CompletedTask;
    }
    public static Task ConcurrentAndRollback()
    {
        using var f = new PeerTrustTests.Fixture(); Setup(f, f.PeerId, Peer, Local);
        Reject<InvalidOperationException>(() => f.Repository.AcceptActivationAcknowledgement(f.PeerId, Peer, Local, Ack(f), new Hook(true)));
        Check(f.Repository.Read(f.PeerId)!.State == "PeerBound" && f.Count("ActivationTestEffects") == 0);
        f.Execute("CREATE TRIGGER FailActivationAudit BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='PeerActivated' BEGIN SELECT RAISE(ABORT,'fixture audit unavailable'); END;");
        Reject<SqliteException>(() => f.Repository.AcceptActivationAcknowledgement(f.PeerId, Peer, Local, Ack(f), new Hook()));
        Check(f.Repository.Read(f.PeerId)!.State == "PeerBound" && f.Count("ActivationTestEffects") == 0);
        f.Execute("DROP TRIGGER FailActivationAudit;");
        var results = new PeerActivationDisposition[8];
        Parallel.For(0, 8, i => results[i] = f.Repository.AcceptActivationAcknowledgement(f.PeerId, Peer, Local, Ack(f), new Hook()));
        Check(results.Count(r => r == PeerActivationDisposition.Activated) == 1);
        Check(results.Count(r => r == PeerActivationDisposition.AlreadyActive) == 7);
        Check(f.Count("ActivationTestEffects") == 1);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerActivated' AND ActorKind='RemoteManager';") == 1);
        var summary = HostDatabase.QueryScalarText(f.Writer, "SELECT Summary FROM AuditEvents WHERE EventKind='PeerActivated';")!;
        Check(!summary.Contains(Local) && !summary.Contains(Peer));
        return Task.CompletedTask;
    }
    public static Task IdentityExpiryAndRecovery()
    {
        using var f = new PeerTrustTests.Fixture(); Setup(f, f.PeerId, Peer, Local);
        void Denied(PeerActivationAcknowledgement ack, string peer = "", string local = "") =>
            Reject<InvalidOperationException>(() => f.Repository.AcceptActivationAcknowledgement(f.PeerId,
                peer == "" ? Peer : peer, local == "" ? Local : local, ack, new Hook()));
        Denied(Ack(f) with { FromHostId = Guid.NewGuid() });
        Denied(Ack(f) with { RecordedHostId = Guid.NewGuid() });
        Denied(Ack(f) with { RecordedFingerprint = Other });
        Denied(Ack(f), Other); Denied(Ack(f) with { RecordedFingerprint = Other }, local: Other);
        Reject<ArgumentNullException>(() => f.Repository.AcceptActivationAcknowledgement(f.PeerId, Peer, Local, Ack(f), null!));
        // An unapproved replacement candidate never authenticates the activation channel.
        f.Repository.RecordVerifiedBinding(f.PeerId, Other, Local); Denied(Ack(f), Other);
        f.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{f.PeerId:D}';");
        Denied(Ack(f)); Reject<InvalidOperationException>(() => f.Repository.PrepareActivationAcknowledgement(f.PeerId, Peer, Local));
        f.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=0 WHERE PeerHostId='{f.PeerId:D}';");
        f.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{Other}' WHERE CredentialRef='current';");
        Denied(Ack(f)); Denied(Ack(f) with { RecordedFingerprint = Other }, local: Other);
        f.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{Local}' WHERE CredentialRef='current';");
        f.Time.Now += TimeSpan.FromMinutes(30);
        Denied(Ack(f)); Reject<InvalidOperationException>(() => f.Repository.PrepareActivationAcknowledgement(f.PeerId, Peer, Local));
        Check(f.Repository.ExpirePending() == 1); Denied(Ack(f));
        Check(f.Repository.Read(f.PeerId)!.State == "Revoked" && f.Count("ActivationTestEffects") == 0);
        return Task.CompletedTask;
    }
    public static Task DeadlineAndMissingProof()
    {
        using var f = new PeerTrustTests.Fixture(); Setup(f, f.PeerId, Peer, Local);
        f.Execute("UPDATE TrustedManagerPairings SET LocalBoundPublicKeyFingerprint=NULL;");
        Reject<InvalidOperationException>(() => f.Repository.PrepareActivationAcknowledgement(f.PeerId, Peer, Local));
        Reject<InvalidOperationException>(() => f.Repository.AcceptActivationAcknowledgement(f.PeerId, Peer, Local, Ack(f), new Hook()));
        f.Execute($"UPDATE TrustedManagerPairings SET LocalBoundPublicKeyFingerprint='{Local}';");
        f.Time.Now += TimeSpan.FromMinutes(29);
        Reject<InvalidOperationException>(() => f.Repository.AcceptActivationAcknowledgement(f.PeerId, Peer, Local, Ack(f),
            new Hook(afterWrite: () => f.Time.Now += TimeSpan.FromMinutes(1))));
        Check(f.Repository.Read(f.PeerId)!.State == "PeerBound" && f.Count("ActivationTestEffects") == 0);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerActivated';") == 0);
        Check(f.Repository.ExpirePending() == 1);
        return Task.CompletedTask;
    }
}
