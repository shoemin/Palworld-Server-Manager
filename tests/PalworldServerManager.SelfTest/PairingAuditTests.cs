using Microsoft.Data.Sqlite;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class PairingAuditTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Pairing audit assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected pairing audit refusal."); }
    public static Task IdempotenceAndPrivacy()
    {
        using var f = new PeerTrustTests.Fixture(); var id = Guid.NewGuid();
        f.Repository.RecordPairingTerminal(id, PairingTerminalOutcome.Expired, f.Time.Now);
        Parallel.For(0, 8, _ => f.Repository.RecordPairingTerminal(id, PairingTerminalOutcome.Failed, f.Time.Now.AddMinutes(1)));
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PairingAttemptExpired';") == 1);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE ActorKind IS NOT NULL OR ActorPeerHostId IS NOT NULL OR ActorLocalPrincipalId IS NOT NULL;") == 0);
        Check(HostDatabase.QueryScalarText(f.Writer, "SELECT Summary FROM AuditEvents;") == $"PairingAttemptExpired: attempt {id:D}.");
        Check(HostDatabase.QueryScalarText(f.Writer, "SELECT OccurredUtc FROM AuditEvents;") == f.Time.Now.ToString("O"));
        Reject<ArgumentException>(() => f.Repository.RecordPairingTerminal(Guid.NewGuid(), (PairingTerminalOutcome)999, f.Time.Now));
        Reject<ArgumentException>(() => f.Repository.RecordPairingTerminal(Guid.Empty, PairingTerminalOutcome.Failed, f.Time.Now));
        f.Execute($"UPDATE AuditEvents SET EventKind='UnrelatedEvent' WHERE AuditEventId='{id:D}';");
        Reject<InvalidDataException>(() => f.Repository.RecordPairingTerminal(id, PairingTerminalOutcome.Failed, f.Time.Now));
        f.Execute("CREATE TRIGGER FailAudit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT,'fixture audit unavailable'); END;");
        Reject<SqliteException>(() => f.Repository.RecordPairingTerminal(Guid.NewGuid(), PairingTerminalOutcome.Failed, f.Time.Now));
        Check(f.Count("AuditEvents") == 1 && f.Count("TrustedManagers") == 0);
        return Task.CompletedTask;
    }
    public static Task RetryAndPendingCleanup()
    {
        using var f = new PeerTrustTests.Fixture();
        f.Repository.RecordVerifiedBinding(f.PeerId, new('B', 64), new('A', 64)); f.Time.Now += TimeSpan.FromMinutes(30);
        using var journal = new PairingAuditJournal(f.Repository, f.Time);
        Check(f.Repository.Read(f.PeerId)!.State == "Revoked");
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerBoundExpired' AND ActorKind IS NULL;") == 1);
        var id = Guid.NewGuid(); var occurred = f.Time.Now;
        f.Execute("CREATE TRIGGER FailAudit BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='PairingAttemptFailed' BEGIN SELECT RAISE(ABORT,'fixture audit unavailable'); END;");
        journal.Record(id, PairingTerminalOutcome.Failed); Check(journal.Faulted && journal.PendingCount == 1);
        Reject<InvalidOperationException>(journal.RequireHealthy);
        journal.Record(id, PairingTerminalOutcome.Expired); Check(journal.PendingCount == 1); // First observed outcome is retained.
        f.Time.Now += TimeSpan.FromMinutes(1); f.Execute("DROP TRIGGER FailAudit;"); journal.Maintain();
        Check(!journal.Faulted && journal.PendingCount == 0); journal.RequireHealthy();
        Check(HostDatabase.QueryScalarText(f.Writer, $"SELECT OccurredUtc FROM AuditEvents WHERE AuditEventId='{id:D}';") == occurred.ToString("O"));
        journal.Record(id, PairingTerminalOutcome.Failed);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PairingAttemptFailed';") == 1);
        var secondPeer = Guid.NewGuid(); f.Repository.RecordVerifiedBinding(secondPeer, new('C', 64), new('A', 64));
        f.Execute("CREATE TRIGGER FailExpiry BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='PeerBoundExpired' BEGIN SELECT RAISE(ABORT,'fixture expiry unavailable'); END;");
        f.Time.Now += TimeSpan.FromMinutes(30); journal.Maintain();
        Check(journal.Faulted && f.Repository.Read(secondPeer)!.State == "PeerBound" && !f.Repository.RecognizesTransportFingerprint(new('C', 64)));
        f.Execute("DROP TRIGGER FailExpiry;"); journal.Maintain();
        Check(!journal.Faulted && f.Repository.Read(secondPeer)!.State == "Revoked");
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerBoundExpired';") == 2);
        return Task.CompletedTask;
    }
    public static Task CapacityAndShutdownFailure()
    {
        using var f = new PeerTrustTests.Fixture(); var journal = new PairingAuditJournal(f.Repository, f.Time);
        f.Execute("CREATE TRIGGER FailAudit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT,'fixture audit unavailable'); END;");
        for (var i = 0; i < 64; i++) journal.Record(Guid.NewGuid(), PairingTerminalOutcome.Failed);
        Reject<InvalidOperationException>(() => journal.Record(Guid.NewGuid(), PairingTerminalOutcome.Failed));
        Check(journal.Faulted && journal.PendingCount == 64);
        f.Execute("DROP TRIGGER FailAudit;"); journal.Maintain();
        Check(journal.Faulted && journal.PendingCount == 0 && f.Count("AuditEvents") == 64);
        Reject<InvalidOperationException>(journal.Dispose); journal.Dispose();
        Reject<ObjectDisposedException>(journal.RequireHealthy);
        return Task.CompletedTask;
    }
}
