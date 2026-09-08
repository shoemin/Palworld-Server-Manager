using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Operations;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class ConfigurationRevisionTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Resource revision assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected resource refusal: " + typeof(T).Name); }
    private static void Sql(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    private static string Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, string key)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private sealed class Rig : IDisposable
    {
        internal readonly PeerTrustTests.Fixture F = new();
        internal ConfigurationRevisionRepository Repository => new(F.Database, F.HostId, F.Time);
        internal EditableResource Resource(string kind = "Fixture", Guid? profile = null)
            => new(kind, profile is { } id ? new ServerTarget(new(F.HostId, id)) : new HostTarget(F.HostId));
        internal Rig() => F.Execute("""
            CREATE TABLE RevisionFixtureValues (ResourceKey TEXT PRIMARY KEY, Payload TEXT NOT NULL);
            CREATE TABLE RevisionFixtureEvents (EventId TEXT PRIMARY KEY, ResourceKey TEXT NOT NULL, Payload TEXT NOT NULL);
            """);
        internal static string Key(EditableResource resource) => resource.Kind + "/" + resource.Target.AuthoritativeHostId.ToString("D") +
            (resource.Target is ServerTarget server ? "/server/" + server.Server.ServerProfileId.ToString("D") : "/host");
        internal Action Change(SqliteConnection c, SqliteTransaction tx, EditableResource resource, string value)
        {
            var key = Key(resource); var audit = Guid.NewGuid().ToString("D");
            Sql(c, tx, """
                INSERT INTO RevisionFixtureValues VALUES ($key,$value) ON CONFLICT(ResourceKey) DO UPDATE SET Payload=$value;
                INSERT INTO RevisionFixtureEvents VALUES ($audit,$key,$value);
                """, ("$key", key), ("$value", value), ("$audit", audit));
            return () =>
            {
                Check(Scalar(c, tx, "SELECT Payload FROM RevisionFixtureValues WHERE ResourceKey=$key;", key) == value);
                Check(Scalar(c, tx, "SELECT ResourceKey || '|' || Payload FROM RevisionFixtureEvents WHERE EventId=$key;", audit) == key + "|" + value);
            };
        }
        internal ConfigurationRevision Write(EditableResource resource, long expected, string value = "new")
            => Repository.Write(resource, expected, (c, tx) => Change(c, tx, resource, value));
        internal string Snapshot()
        {
            var rows = new List<object?[]>();
            foreach (var table in new[] { "ConfigurationRevisions", "RevisionFixtureValues", "RevisionFixtureEvents", "HostIdentity", "LocalPrincipals", "AuthorizationRevision" })
            {
                using var cmd = F.Writer.CreateCommand(); cmd.CommandText = "SELECT * FROM " + table + " ORDER BY 1;";
                using var reader = cmd.ExecuteReader(); rows.Add([table]);
                while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
            }
            return JsonSerializer.Serialize(rows);
        }
        public void Dispose() => F.Dispose();
    }

    public static Task QualifiedResourcesRemainIndependentAfterReopen()
    {
        using var a = new Rig(); using var b = new Rig(); var profile = a.F.HostId;
        EditableResource[] resources = [a.Resource(), a.Resource(profile: profile), a.Resource("Sharing", profile), a.Resource(profile: Guid.NewGuid())];
        foreach (var resource in resources)
        {
            Check(a.Repository.Read(resource) == new ConfigurationRevision(resource, 0, null));
            var result = a.Write(resource, 0); Check(result.Revision == 1 && result.LastModifiedUtc == a.F.Time.Now);
            Check(a.Repository.Read(resource) == result);
        }
        a.F.Execute("INSERT INTO ConfigurationRevisions VALUES ('Fixture','unqualified-history',7,'legacy-date');");
        a.F.Time.Now += TimeSpan.FromMinutes(1);
        Check(a.Write(resources[1], 1, "second").Revision == 2);
        Check(resources.Where((_, i) => i != 1).All(r => a.Repository.Read(r).Revision == 1));
        var remote = b.Resource(profile: profile); Check(b.Write(remote, 0).Revision == 1);
        Reject<ArgumentException>(() => a.Repository.Read(remote)); Reject<ArgumentException>(() => b.Write(resources[1], 2));
        Check(a.F.Count("ConfigurationRevisions") == 5 && b.F.Count("ConfigurationRevisions") == 1);
        Check(HostDatabase.QueryScalarLong(a.F.Writer, "SELECT RevisionId FROM ConfigurationRevisions WHERE ResourceId='unqualified-history';") == 7);
        return Task.CompletedTask;
    }

    public static Task ConcurrentWritersRejectStaleBeforeAction()
    {
        using var r = new Rig(); var resource = r.Resource(); var wins = 0; var stale = 0; var calls = 0;
        Parallel.For(0, 8, i =>
        {
            try
            {
                r.Repository.Write(resource, 0, (c, tx) => { Interlocked.Increment(ref calls); return r.Change(c, tx, resource, "writer" + i); });
                Interlocked.Increment(ref wins);
            }
            catch (StaleWriteConflictException) { Interlocked.Increment(ref stale); }
        });
        Check(wins == 1 && stale == 7 && calls == 1 && r.Repository.Read(resource).Revision == 1);
        Check(r.F.Count("RevisionFixtureValues") == 1 && r.F.Count("RevisionFixtureEvents") == 1);
        var before = r.Snapshot();
        foreach (var expected in new long[] { -1, 0, 2, long.MaxValue })
            Reject<StaleWriteConflictException>(() => r.Repository.Write(resource, expected, (_, _) => { calls++; throw new Exception("Stale callback ran."); }));
        Check(r.Snapshot() == before && calls == 1); Check(r.Write(resource, 1).Revision == 2);
        return Task.CompletedTask;
    }

    public static Task ActionAndFinalValidationFailuresRollBack()
    {
        for (var mode = 0; mode < 5; mode++)
        {
            using var r = new Rig(); var resource = r.Resource(); using var cancelled = new CancellationTokenSource();
            var before = r.Snapshot();
            void Attempt() => r.Repository.Write(resource, 0, (c, tx) =>
            {
                var validate = r.Change(c, tx, resource, "new");
                if (mode == 0) throw new InvalidOperationException("Fixture write failure.");
                if (mode == 1) return null!;
                if (mode == 2) return () => throw new UnauthorizedAccessException("Fixture final proof refused.");
                if (mode == 3) return () => { validate(); cancelled.Cancel(); };
                Sql(c, tx, "INSERT INTO ConfigurationRevisions VALUES ('Fixture',$key,0,$now);",
                    ("$key", "H:" + r.F.HostId.ToString("D")), ("$now", r.F.Time.Now.ToString("O")));
                return validate;
            }, cancelled.Token);
            if (mode == 2) Reject<UnauthorizedAccessException>(Attempt);
            else if (mode == 3) Reject<OperationCanceledException>(Attempt);
            else Reject<InvalidOperationException>(Attempt);
            Check(r.Snapshot() == before); Check(r.Write(resource, 0).Revision == 1);
        }
        return Task.CompletedTask;
    }

    public static Task RevisionTriggerFaultsCannotCommitPartialData()
    {
        string[] effects = [
            "DELETE FROM ConfigurationRevisions;",
            "UPDATE ConfigurationRevisions SET RevisionId=RevisionId+10;",
            "UPDATE ConfigurationRevisions SET LastModifiedUtc='invalid';",
            "UPDATE ConfigurationRevisions SET ResourceKind='Other';",
            "UPDATE ConfigurationRevisions SET ResourceId='unqualified';",
            "UPDATE RevisionFixtureValues SET Payload='changed';",
            "DELETE FROM RevisionFixtureEvents;"
        ];
        foreach (var update in new[] { false, true }) foreach (var effect in effects)
        {
            using var r = new Rig(); var resource = r.Resource(); if (update) r.Write(resource, 0, "old");
            var before = r.Snapshot(); var operation = update ? "UPDATE" : "INSERT";
            r.F.Execute("CREATE TRIGGER FixtureLate AFTER " + operation + " ON ConfigurationRevisions BEGIN " + effect + " END;");
            var refused = false;
            try { r.Write(resource, update ? 1 : 0); }
            catch (Exception) { refused = true; }
            Check(refused && r.Snapshot() == before);
            r.F.Execute("DROP TRIGGER FixtureLate;"); Check(r.Write(resource, update ? 1 : 0).Revision == (update ? 2 : 1));
        }
        return Task.CompletedTask;
    }

    public static Task InvalidStorageHostAndCancellationNeverRunAction()
    {
        for (var mode = 0; mode < 8; mode++)
        {
            using var r = new Rig(); var resource = r.Resource(); var calls = 0;
            if (mode < 4)
            {
                r.Write(resource, 0);
                r.F.Execute(mode switch {
                    0 => "UPDATE ConfigurationRevisions SET RevisionId=9223372036854775807;",
                    1 => "UPDATE ConfigurationRevisions SET RevisionId=1.5;",
                    2 => "UPDATE ConfigurationRevisions SET LastModifiedUtc='invalid';",
                    _ => "UPDATE ConfigurationRevisions SET LastModifiedUtc='2026-09-07T00:00:00.0000000+01:00';"
                });
            }
            else if (mode == 4) r.F.Execute("UPDATE HostIdentity SET HostBootstrapState='Uninitialized';");
            else if (mode == 5) r.F.Execute("UPDATE LocalPrincipals SET IsOwner=0;");
            else if (mode == 6) r.F.Execute("DELETE FROM HostIdentity;");
            using var ct = new CancellationTokenSource(); if (mode == 7) ct.Cancel();
            var before = r.Snapshot();
            void Attempt() => r.Repository.Write(resource, mode == 0 ? long.MaxValue : mode < 4 ? 1 : 0,
                (_, _) => { calls++; throw new Exception("Invalid state callback ran."); }, ct.Token);
            if (mode == 0) Reject<OverflowException>(Attempt);
            else if (mode < 4) Reject<InvalidDataException>(Attempt);
            else if (mode == 7) Reject<OperationCanceledException>(Attempt);
            else Reject<InvalidOperationException>(Attempt);
            Check(calls == 0 && r.Snapshot() == before);
        }
        using (var r = new Rig())
        {
            r.F.Execute("DROP TABLE ConfigurationRevisions;");
            Reject<SqliteException>(() => r.Write(r.Resource(), 0));
            Check(r.F.Count("RevisionFixtureValues") == 0 && r.F.Count("RevisionFixtureEvents") == 0);
        }
        return Task.CompletedTask;
    }

    public static Task ReadersSeeCommittedStateAndLateHostChangesRollBack()
    {
        using var r = new Rig(); var resource = r.Resource(); r.Write(resource, 0, "old");
        var before = r.Repository.Read(resource);
        var after = r.Repository.Write(resource, 1, (c, tx) =>
        {
            var validate = r.Change(c, tx, resource, "new");
            Check(r.Repository.Read(resource) == before);
            Check(Scalar(r.F.Writer, null, "SELECT Payload FROM RevisionFixtureValues WHERE ResourceKey=$key;", Rig.Key(resource)) == "old");
            return validate;
        });
        Check(after.Revision == 2 && r.Repository.Read(resource) == after);
        var snapshot = r.Snapshot();
        Reject<InvalidOperationException>(() => r.Repository.Write(resource, 2, (c, tx) =>
        {
            var validate = r.Change(c, tx, resource, "bad");
            return () => { validate(); Sql(c, tx, "UPDATE HostIdentity SET HostBootstrapState='Uninitialized';"); };
        }));
        Check(r.Snapshot() == snapshot); Check(r.Write(resource, 2).Revision == 3);
        Reject<ArgumentNullException>(() => r.Repository.Write(resource, 3, null!));
        Reject<ArgumentException>(() => new EditableResource("../path", resource.Target));
        Reject<ArgumentNullException>(() => new EditableResource("Fixture", null!));
        Check(typeof(EditableResource).GetProperties().All(p => p.SetMethod is null));
        return Task.CompletedTask;
    }
}
