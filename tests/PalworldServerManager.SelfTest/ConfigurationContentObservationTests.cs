using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Operations;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.SelfTest;

internal static class ConfigurationContentObservationTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Configuration content observation assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected observation refusal."); }
    private static ConfigurationRevisionRepository Repo(PeerTrustTests.Fixture f) => new(f.Database, f.HostId, f.Time);
    private static EditableResource Resource(PeerTrustTests.Fixture f, Guid? server = null, string kind = "ServerSettings")
        => new(kind, server is { } id ? new ServerTarget(new(f.HostId, id)) : new HostTarget(f.HostId));
    private static ConfigurationRevision Observe(PeerTrustTests.Fixture f, EditableResource resource, byte[] bytes)
        => Repo(f).ObserveContent(resource, bytes, () => { });
    private static string Snapshot(PeerTrustTests.Fixture f)
    {
        var rows = new List<object?[]>();
        foreach (var table in new[] { "ConfigurationRevisions", "ConfigurationContentObservations", "HostIdentity", "LocalPrincipals", "AuthorizationRevision", "AuditEvents" })
        {
            using var cmd = f.Writer.CreateCommand(); cmd.CommandText = "SELECT * FROM " + table + " ORDER BY 1,2;";
            using var reader = cmd.ExecuteReader(); rows.Add([table]);
            while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray());
        }
        return JsonSerializer.Serialize(rows);
    }
    public static Task DurableBytesAndQualifiedScopes()
    {
        using var f = new PeerTrustTests.Fixture(); using var other = new PeerTrustTests.Fixture(); var id = Guid.NewGuid();
        var resource = Resource(f, id); byte[] first = [0, 255, 128, 1];
        var initial = Observe(f, resource, first); Check(initial.Revision == 1 && initial.LastModifiedUtc == f.Time.Now);
        f.Time.Now += TimeSpan.FromHours(1);
        Check(Observe(f, resource, first) == initial); // independent connection/repository and unchanged timestamp
        Check(Observe(f, resource, [0, 255, 128, 2]).Revision == 2);
        Check(Observe(f, resource, first).Revision == 3); // observed A -> B -> A never resurrects old token
        foreach (var target in new[] { Resource(f), Resource(f, id, "OtherSettings"), Resource(f, Guid.NewGuid()) })
            Check(Observe(f, target, first).Revision == 1);
        Check(Observe(other, Resource(other, id), first).Revision == 1);
        Reject<ArgumentException>(() => Observe(f, Resource(other, id), first));
        Check(f.Count("ConfigurationContentObservations") == 4 && other.Count("ConfigurationContentObservations") == 1);
        var bounded = Resource(f, Guid.NewGuid()); Check(Observe(f, bounded, []).Revision == 1);
        Check(Observe(f, bounded, new byte[4194308]).Revision == 2);
        Check(f.Count("HostCapabilityGrants") == 0 && f.Count("ServerCapabilityGrants") == 0 && f.Count("AuditEvents") == 0);
        return Task.CompletedTask;
    }
    public static Task ConcurrentObservationsAndStaleWrites()
    {
        using var f = new PeerTrustTests.Fixture(); var resource = Resource(f, Guid.NewGuid()); var revisions = new long[8];
        Parallel.For(0, 8, i => revisions[i] = Observe(f, resource, [200]).Revision);
        Check(revisions.All(r => r == 1));
        Parallel.For(0, 8, i => revisions[i] = Observe(f, resource, [(byte)i]).Revision);
        Check(revisions.Order().SequenceEqual(Enumerable.Range(2, 8).Select(i => (long)i)));
        var currentBytes = new byte[] { 77 }; var observed = Observe(f, resource, currentBytes);
        var calls = 0;
        Reject<StaleWriteConflictException>(() => Repo(f).Write(resource, 1, (_, _) => { calls++; return () => { }; }));
        Check(calls == 0);
        var genericWrite = Repo(f).Write(resource, observed.Revision, (_, _) => () => { });
        Check(genericWrite.Revision == observed.Revision + 1);
        var reconciled = Observe(f, resource, currentBytes);
        Check(reconciled.Revision == genericWrite.Revision + 1 && Observe(f, resource, currentBytes) == reconciled);
        Check(f.Count("ConfigurationContentObservations") == 1);
        return Task.CompletedTask;
    }
    public static Task FinalProofCancellationAndFaultsRollBack()
    {
        foreach (var exists in new[] { false, true }) foreach (var unchanged in new[] { false, true }) foreach (var cancel in new[] { false, true })
        {
            using var f = new PeerTrustTests.Fixture(); var resource = Resource(f); if (exists) Observe(f, resource, [1]);
            var before = Snapshot(f); var prior = Repo(f).Read(resource); var calls = 0; using var ct = new CancellationTokenSource();
            void Attempt() => Repo(f).ObserveContent(resource, unchanged ? [1] : [2], () =>
            {
                Check(Repo(f).Read(resource) == prior); // outside readers see committed metadata only
                if (++calls != 2) return;
                if (cancel) ct.Cancel(); else throw new UnauthorizedAccessException("Synthetic final proof refused.");
            }, ct.Token);
            if (cancel) Reject<OperationCanceledException>(Attempt); else Reject<UnauthorizedAccessException>(Attempt);
            Check(calls == 2 && Snapshot(f) == before);
        }
        foreach (var exists in new[] { false, true }) foreach (var fault in new[] {
            "SELECT RAISE(ABORT,'fixture');", "DELETE FROM ConfigurationContentObservations;",
            "UPDATE ConfigurationContentObservations SET ContentFingerprint=zeroblob(32);",
            "UPDATE ConfigurationContentObservations SET ObservedRevision=ObservedRevision+1;",
            "UPDATE ConfigurationRevisions SET RevisionId=RevisionId+1;",
            "UPDATE HostIdentity SET HostBootstrapState='Uninitialized';" })
        {
            using var f = new PeerTrustTests.Fixture(); var resource = Resource(f); if (exists) Observe(f, resource, [1]);
            var before = Snapshot(f);
            f.Execute("CREATE TRIGGER ObservationFault AFTER " + (exists ? "UPDATE" : "INSERT") + " ON ConfigurationContentObservations BEGIN " + fault + " END;");
            var refused = false;
            try { Observe(f, resource, [2]); } catch (Exception) { refused = true; }
            Check(refused && Snapshot(f) == before);
            f.Execute("DROP TRIGGER ObservationFault;"); Check(Observe(f, resource, [2]).Revision == (exists ? 2 : 1));
        }
        return Task.CompletedTask;
    }
    private sealed class FailingMigration : IHostSchemaMigration
    {
        public int Version => 17;
        public void Apply(SqliteConnection c, SqliteTransaction tx)
        { HostSchema.AllMigrations().Single(m => m.Version == 17).Apply(c, tx); throw new InvalidOperationException("Synthetic migration fault."); }
    }
    public static Task MigrationPreservesHistoryAndRollsBack()
    {
        using var f = new PeerTrustTests.Fixture(schemaVersion: 16); var resource = Resource(f);
        var before = Repo(f).Write(resource, 0, (_, _) => () => { });
        f.Execute("INSERT INTO ConfigurationRevisions VALUES ('Legacy','historical-unqualified',9,'legacy-time');");
        var failing = new HostSchemaMigrationRunner(HostSchema.AllMigrations().Where(m => m.Version < 17).Concat([new FailingMigration()]));
        Reject<InvalidOperationException>(() => failing.Migrate(f.Writer));
        Check(HostSchemaMigrationRunner.ReadSchemaVersion(f.Writer) == 16 &&
            HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM sqlite_master WHERE name='ConfigurationContentObservations';") == 0 && Repo(f).Read(resource) == before);
        var runner = new HostSchemaMigrationRunner(HostSchema.AllMigrations().Where(m => m.Version <= 17));
        Check(runner.Migrate(f.Writer) == 1 && runner.Migrate(f.Writer) == 0 && f.Count("ConfigurationContentObservations") == 0);
        Check(HostDatabase.QueryScalarText(f.Writer, "SELECT LastModifiedUtc FROM ConfigurationRevisions WHERE ResourceKind='Legacy';") == "legacy-time");
        Check(Observe(f, resource, [1]).Revision == 2 && f.Count("AuditEvents") == 0 && f.Count("HostCapabilityGrants") == 0);
        foreach (var value in new[] { "zeroblob(31)", "'not-a-blob'", "NULL" })
            Reject<SqliteException>(() => f.Execute("UPDATE ConfigurationContentObservations SET ContentFingerprint=" + value + ";"));
        Reject<SqliteException>(() => f.Execute("UPDATE ConfigurationContentObservations SET ObservedRevision=0;"));
        Reject<SqliteException>(() => f.Execute("UPDATE ConfigurationContentObservations SET ResourceId='missing';"));
        return Task.CompletedTask;
    }
    public static Task InvalidMetadataBoundsAndOverflowRefuse()
    {
        foreach (var corruption in new[] { "UPDATE ConfigurationContentObservations SET ObservedRevision=2;",
            "UPDATE ConfigurationContentObservations SET ObservedRevision=1.5;", "UPDATE ConfigurationContentObservations SET ContentFingerprint=zeroblob(31);",
            "UPDATE ConfigurationContentObservations SET ContentFingerprint='invalid';", "UPDATE ConfigurationRevisions SET RevisionId=1.5;",
            "UPDATE ConfigurationRevisions SET LastModifiedUtc='invalid';" })
        {
            using var f = new PeerTrustTests.Fixture(); var resource = Resource(f); Observe(f, resource, [1]);
            f.Execute("PRAGMA ignore_check_constraints=ON;" + corruption); var before = Snapshot(f);
            Reject<InvalidDataException>(() => Observe(f, resource, [1])); Check(Snapshot(f) == before);
        }
        using (var f = new PeerTrustTests.Fixture())
        {
            var resource = Resource(f); Observe(f, resource, [1]);
            f.Execute("UPDATE ConfigurationRevisions SET RevisionId=9223372036854775807; UPDATE ConfigurationContentObservations SET ObservedRevision=9223372036854775807;");
            Check(Observe(f, resource, [1]).Revision == long.MaxValue); var before = Snapshot(f);
            Reject<OverflowException>(() => Observe(f, resource, [2])); Check(before == Snapshot(f));
        }
        foreach (var mode in new[] { 0, 1, 2, 3, 4 })
        {
            using var f = new PeerTrustTests.Fixture(); var resource = Resource(f); var repo = Repo(f); using var ct = new CancellationTokenSource();
            if (mode == 0) f.Execute("UPDATE HostIdentity SET HostBootstrapState='Uninitialized';");
            if (mode == 1) f.Execute("UPDATE LocalPrincipals SET IsOwner=0;");
            if (mode == 2) ct.Cancel(); var before = Snapshot(f); var calls = 0;
            void Attempt() => repo.ObserveContent(resource, mode == 3 ? new byte[4194309] : [1], () => { calls++; if (mode == 4) ct.Cancel(); }, ct.Token);
            if (mode is 2 or 4) Reject<OperationCanceledException>(Attempt);
            else if (mode == 3) Reject<ArgumentException>(Attempt); else Reject<InvalidOperationException>(Attempt);
            Check(before == Snapshot(f) && calls == (mode is 2 or 3 ? 0 : 1));
            Reject<ArgumentNullException>(() => repo.ObserveContent(resource, [1], null!));
        }
        return Task.CompletedTask;
    }
}
