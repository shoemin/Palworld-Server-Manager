using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static class ServerInventoryTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Inventory assertion failed."); }
    private static T Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T ex) { return ex; } throw new Exception("Expected inventory refusal."); }
    private static void Insert(PeerTrustTests.Fixture f, ServerRef server, string name = "Recorded server", string? path = null,
        int game = 8211, int rest = 8212, string? import = null)
    {
        using var cmd = f.Writer.CreateCommand();
        cmd.CommandText = "INSERT INTO ServerInventory VALUES ($server,$host,$name,$path,$game,$rest,$import,$utc);";
        foreach (var (key, value) in new (string, object?)[] { ("$server", server.ServerProfileId.ToString("D")),
            ("$host", server.AuthoritativeHostId.ToString("D")), ("$name", name), ("$path", path), ("$game", game),
            ("$rest", rest), ("$import", import), ("$utc", f.Time.Now.ToString("O", CultureInfo.InvariantCulture)) })
            cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    private static string Snapshot(PeerTrustTests.Fixture f)
    {
        var rows = new List<object[]>();
        foreach (var table in new[] { "ServerInventory", "HostIdentity", "LocalPrincipals", "ConfigurationRevisions",
            "ServerCapabilityGrants", "HostCapabilityGrants", "AuthorizationRevision" })
        {
            using var cmd = f.Writer.CreateCommand(); cmd.CommandText = "SELECT * FROM " + table + " ORDER BY 1,2;";
            using var r = cmd.ExecuteReader(); rows.Add([table]);
            while (r.Read()) rows.Add(Enumerable.Range(0, r.FieldCount).Select(r.GetValue).ToArray());
        }
        return JsonSerializer.Serialize(rows);
    }

    public static Task ExactTargetAndRecordedValuesSurviveReopen()
    {
        using var f = new PeerTrustTests.Fixture(); var id = Guid.NewGuid(); var local = new ServerRef(f.HostId, id);
        var foreign = new ServerRef(f.PeerId, id); var repo = new ServerInventoryRepository(f.Database, f.HostId);
        Check(repo.Read(local) is null);
        const string path = " relative/recorded/../path "; const string name = " Recorded 日本語\nname ";
        const string provenance = "fixture-private-provenance";
        Insert(f, local, name, path, 19211, 19212, provenance); Insert(f, foreign, "Foreign collision");
        var before = Snapshot(f); var actual = repo.Read(local)!;
        Check(actual.Server == local && actual.DisplayName == name && actual.InstallPath == path &&
            actual.GamePort == 19211 && actual.RestApiPort == 19212 && actual.ImportProvenance == provenance && actual.CreatedUtc == f.Time.Now);
        Check(!actual.ToString()!.Contains(provenance) && !actual.ToString()!.Contains(path));
        Check(new ServerInventoryRepository(f.Database, f.HostId).Read(local)!.DisplayName == name);
        Reject<ArgumentException>(() => repo.Read(foreign));
        Check(repo.Read(new(f.HostId, Guid.NewGuid())) is null && before == Snapshot(f));
        return Task.CompletedTask;
    }

    public static Task NullableAndHistoricalValuesAreNotLaunchApproval()
    {
        using var f = new PeerTrustTests.Fixture(); var server = new ServerRef(f.HostId, Guid.NewGuid());
        var repo = new ServerInventoryRepository(f.Database, f.HostId); Insert(f, server, "", null, int.MinValue, int.MaxValue);
        var before = Snapshot(f); var actual = repo.Read(server)!;
        Check(actual.DisplayName == "" && actual.InstallPath is null && actual.ImportProvenance is null &&
            actual.GamePort == int.MinValue && actual.RestApiPort == int.MaxValue && Snapshot(f) == before);
        f.Execute("UPDATE ServerInventory SET InstallPath='',ImportProvenance='';"); actual = repo.Read(server)!;
        Check(actual.InstallPath == "" && actual.ImportProvenance == "");
        return Task.CompletedTask;
    }

    public static Task MalformedStorageRefusesWithoutDisclosureOrRepair()
    {
        using var f = new PeerTrustTests.Fixture(); var server = new ServerRef(f.HostId, Guid.NewGuid());
        var repo = new ServerInventoryRepository(f.Database, f.HostId); Insert(f, server);
        foreach (var mutation in new[] { "DisplayName=X'736563726574'", "InstallPath=X'736563726574'", "ImportProvenance=X'736563726574'",
            "GamePort='secret-invalid'", "RestApiPort=8212.5", "GamePort=2147483648", "RestApiPort=-2147483649",
            "CreatedUtc='secret-invalid'", "CreatedUtc='2026-09-06T12:00:00.0000000+01:00'",
            "CreatedUtc=X'736563726574'" })
        {
            f.Execute("UPDATE ServerInventory SET " + mutation + ";"); var before = Snapshot(f);
            var ex = Reject<InvalidDataException>(() => repo.Read(server));
            Check(ex.Message == "Invalid persisted server inventory." && ex.InnerException is null && before == Snapshot(f));
            f.Execute("DELETE FROM ServerInventory;"); Insert(f, server);
        }
        return Task.CompletedTask;
    }

    public static Task IdentityOwnerCancellationAndMissingStorageRefuse()
    {
        using var f = new PeerTrustTests.Fixture(); var server = new ServerRef(f.HostId, Guid.NewGuid());
        var repo = new ServerInventoryRepository(f.Database, f.HostId); Insert(f, server);
        var before = Snapshot(f); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Reject<OperationCanceledException>(() => repo.Read(server, cancel.Token));
        Reject<ArgumentNullException>(() => repo.Read(null!)); Reject<ArgumentException>(() => new ServerInventoryRepository(f.Database, Guid.Empty));
        Reject<InvalidOperationException>(() => new ServerInventoryRepository(f.Database, f.PeerId).Read(new(f.PeerId, server.ServerProfileId)));
        Check(Snapshot(f) == before);
        f.Execute("UPDATE LocalPrincipals SET State='Revoked',PublicVerificationKey=NULL WHERE IsOwner=1;");
        Reject<InvalidOperationException>(() => repo.Read(server));
        f.Execute("UPDATE LocalPrincipals SET State='Active',PublicVerificationKey='fixture-public' WHERE IsOwner=1; UPDATE HostIdentity SET HostBootstrapState='Uninitialized';");
        Reject<InvalidOperationException>(() => repo.Read(server));
        var missing = Path.Combine(f.Database.Root.RootDirectory, "never-created");
        var db = new HostDatabase(new HostDataRoot(missing));
        Reject<SqliteException>(() => new ServerInventoryRepository(db, f.HostId).Read(server));
        Check(!Directory.Exists(missing));
        return Task.CompletedTask;
    }

    public static Task ReadsObserveCommittedStateWithoutBlockingWriter()
    {
        using var f = new PeerTrustTests.Fixture(); var server = new ServerRef(f.HostId, Guid.NewGuid()); Insert(f, server, "Before");
        var repo = new ServerInventoryRepository(f.Database, f.HostId);
        using (var tx = f.Writer.BeginTransaction())
        {
            using var cmd = f.Writer.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "UPDATE ServerInventory SET DisplayName='After'; UPDATE LocalPrincipals SET State='Revoked',PublicVerificationKey=NULL WHERE IsOwner=1;";
            cmd.ExecuteNonQuery(); Check(repo.Read(server)!.DisplayName == "Before"); tx.Rollback();
        }
        Check(repo.Read(server)!.DisplayName == "Before");
        f.Execute("UPDATE ServerInventory SET DisplayName='Committed';");
        var snapshot = repo.Read(server)!; Check(snapshot.DisplayName == "Committed");
        f.Execute("DELETE FROM ServerInventory;"); Check(repo.Read(server) is null && snapshot.DisplayName == "Committed");
        return Task.CompletedTask;
    }
}
