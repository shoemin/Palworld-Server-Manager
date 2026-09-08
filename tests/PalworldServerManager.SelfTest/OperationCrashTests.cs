using System.Diagnostics;
using PalworldServerManager.Core.Operations;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;
using static PalworldServerManager.SelfTest.OperationLifecycleTests;

namespace PalworldServerManager.SelfTest;

internal static class OperationCrashTests
{
    private static string CheckedRoot(string root)
    {
        var full = Path.GetFullPath(root); var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        var name = Path.GetFileName(full);
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !name.StartsWith("PSMOperationCrash", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[17..], "N", out _)) throw new InvalidOperationException("An isolated operation crash fixture root is required.");
        return full;
    }

    // Runs only as an explicitly spawned test child, with its own real machine-writer lease.
    internal static int RunChild(string root, string hostText, string operationText, string profileText, string scope, string mode, string mutex)
    {
        root = CheckedRoot(root); var host = Guid.ParseExact(hostText, "D"); var id = Guid.ParseExact(operationText, "D");
        var profile = Guid.ParseExact(profileText, "D");
        if (scope is not "host" and not "server" || mode is not "start-before" and not "start-after" and not "finish-before" and not "finish-after")
            throw new ArgumentException("Unknown operation crash fixture mode.");
        using var lease = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(5), mutex) ?? throw new InvalidOperationException("Fixture writer lease unavailable.");
        var repo = new OperationRepository(new HostDatabase(new HostDataRoot(root)), host, Definitions());
        var kind = scope == "host" ? "HostFixture" : "ServerFixture";
        OperationTarget target = scope == "host" ? new HostTarget(host) : new ServerTarget(new(host, profile));
        void Pause() { File.WriteAllText(Path.Combine(root, "ready"), "ready"); Thread.Sleep(Timeout.Infinite); }
        void CheckUncommittedPair(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, bool terminal)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.Parameters.AddWithValue("$id", id.ToString("D"));
            cmd.CommandText = "SELECT TargetKind,TargetHostId,TargetServerProfileId,Kind,IsTerminal,Phase FROM OperationRecords WHERE OperationId=$id;";
            using (var reader = cmd.ExecuteReader())
            {
                Check(reader.Read() && reader.GetString(0) == (scope == "host" ? "HostTarget" : "ServerTarget") && reader.GetString(1) == host.ToString("D"));
                Check(scope == "host" ? reader.IsDBNull(2) : reader.GetString(2) == profile.ToString("D"));
                Check(reader.GetString(3) == kind && reader.GetInt64(4) == (terminal ? 1 : 0) && reader.GetString(5) == (terminal ? "Done" : "Start"));
                Check(!reader.Read());
            }
            cmd.CommandText = "SELECT ScopeKind,ScopeHostId,ScopeServerProfileId FROM OperationLocks WHERE OwningOperationId=$id;";
            using var locks = cmd.ExecuteReader();
            if (terminal) Check(!locks.Read());
            else
            {
                Check(locks.Read() && locks.GetString(0) == (scope == "host" ? "HostScope" : "ServerScope") && locks.GetString(1) == host.ToString("D"));
                Check(scope == "host" ? locks.IsDBNull(2) : locks.GetString(2) == profile.ToString("D")); Check(!locks.Read());
            }
        }
        var calls = 0;
        if (mode == "start-before") repo.Start(id, kind, target, (c, tx) => { if (++calls == 2) { CheckUncommittedPair(c, tx, false); Pause(); } });
        else
        {
            repo.Start(id, kind, target, Admit);
            if (mode == "start-after") Pause();
            else
            {
                repo.Transition(id, 1, "Done", (c, tx) => { if (++calls == 2 && mode == "finish-before") { CheckUncommittedPair(c, tx, true); Pause(); } });
                Pause();
            }
        }
        throw new InvalidOperationException("The operation crash fixture must be terminated at its checkpoint.");
    }

    public static async Task RealProcessTerminationPreservesAtomicPairs()
    {
        foreach (var scope in new[] { "host", "server" }) foreach (var mode in new[] { "start-before", "start-after", "finish-before", "finish-after" })
        {
            var root = CheckedRoot(Path.Combine(Path.GetTempPath(), "PSMOperationCrash" + Guid.NewGuid().ToString("N")));
            var database = new HostDatabase(new HostDataRoot(root)); var host = Guid.NewGuid(); var operation = Guid.NewGuid(); var profile = Guid.NewGuid();
            var mutex = @"Global\PSMOperationCrash" + Guid.NewGuid().ToString("N");
            Process? child = null;
            try
            {
                using (var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, mutex) ?? throw new InvalidOperationException("Setup lease unavailable."))
                using (var c = database.OpenConnection())
                {
                    HostSchemaMigrationRunner.Default().Migrate(c); var identities = new HostIdentityRepository(database);
                    identities.EnsureHostIdentity(c, hostIdFactory: () => host.ToString("D"));
                    using var tx = c.BeginTransaction(); identities.InitializeWithOwner(c, tx, Guid.NewGuid().ToString("D"), "fixture-native", "fixture-public"); tx.Commit();
                }
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Self-test executable unavailable.");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
                foreach (var argument in new[] { "--operation-lifecycle-child", root, host.ToString("D"), operation.ToString("D"), profile.ToString("D"), scope, mode, mutex })
                    start.ArgumentList.Add(argument);
                child = Process.Start(start) ?? throw new InvalidOperationException("Crash fixture child did not start.");
                var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(root, "ready")) && !child.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(30);
                if (!File.Exists(Path.Combine(root, "ready")))
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(); throw new Exception("Operation crash checkpoint not reached: " + await error);
                }
                // A second process cannot own the writer while the child is paused.
                using (var denied = HostExclusivityLock.TryAcquire(TimeSpan.Zero, mutex)) Check(denied is null);
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); await output; await error;
                using var recoveredLease = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(5), mutex) ?? throw new InvalidOperationException("Abandoned writer lease unavailable.");
                var state = new OperationRepository(database, host, Definitions()).Read(); Check(!state.HasUnqualifiedState);
                if (mode == "start-before") Check(state.Operations.Count == 0 && state.Locks.Count == 0 && state.Revision == 0);
                else
                {
                    var op = state.Operations.Single().Operation; Check(op.OperationId == operation && op.Target.AuthoritativeHostId == host);
                    OperationTarget expectedTarget = scope == "host" ? new HostTarget(host) : new ServerTarget(new(host, profile));
                    Check(op.Target == expectedTarget && op.Kind == (scope == "host" ? "HostFixture" : "ServerFixture"));
                    var finished = mode == "finish-after";
                    Check(op.IsTerminal == finished && op.Phase == (finished ? "Done" : "Start") && op.Revision == (finished ? 2 : 1));
                    Check(state.Locks.Count == (finished ? 0 : 1) && state.Revision == (finished ? 4 : 2));
                    if (!finished)
                    {
                        OperationLockScope expectedScope = scope == "host" ? new HostScope(host) : new ServerScope(new(host, profile));
                        Check(state.Locks.Single().OwningOperationId == operation && state.Locks.Single().Scope == expectedScope);
                    }
                }
            }
            finally
            {
                if (child is not null) { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } child.Dispose(); }
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(CheckedRoot(root), recursive: true);
            }
        }
    }
}
