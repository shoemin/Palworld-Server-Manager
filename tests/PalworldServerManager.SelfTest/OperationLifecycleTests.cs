using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Operations;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.SelfTest;

internal static class OperationLifecycleTests
{
    internal static void Check(bool value) { if (!value) throw new Exception("Operation lifecycle assertion failed."); }
    internal static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected operation refusal: " + typeof(T).Name); }
    // These are representative declarations and a trusted test admission, not real permissions.
    internal static readonly Action<SqliteConnection, SqliteTransaction> Admit = (_, _) => { };
    internal static OperationDefinition Definition(string kind, LockRequirement requirement, int version = 1)
        => new(kind, requirement, "Start", [OperationPhase.Active("Start", RecoveryDisposition.SafeToRetryFromStart, "Work", "Done", "Failed"),
            OperationPhase.Active("Work", RecoveryDisposition.RequiresManualReview, "Done", "Failed"),
            OperationPhase.Terminal("Done"), OperationPhase.Terminal("Failed")], version);
    internal static OperationDefinition[] Definitions(int version = 1) =>
        [Definition("HostFixture", LockRequirement.HostExclusive, version), Definition("ServerFixture", LockRequirement.ServerExclusive, version),
         Definition("ReadFixture", LockRequirement.None, version)];

    private sealed class Rig : IDisposable
    {
        internal readonly PeerTrustTests.Fixture F;
        internal Rig(int? schema = null) => F = new(schemaVersion: schema);
        internal OperationRepository Repository => new(F.Database, F.HostId, Definitions(), F.Time);
        internal OperationTarget Host => new HostTarget(F.HostId);
        internal OperationTarget Server(Guid? profile = null) => new ServerTarget(new(F.HostId, profile ?? F.PeerId));
        internal DurableOperation Start(string kind = "HostFixture", OperationTarget? target = null, Guid? id = null)
            => Repository.Start(id ?? Guid.NewGuid(), kind, target ?? Host, Admit);
        internal string Snapshot()
        {
            var rows = new List<object?[]>();
            foreach (var table in new[] { "OperationRecords", "OperationLocks", "OperationStateRevision", "HostIdentity", "LocalPrincipals", "AuthorizationRevision" })
            {
                using var cmd = F.Writer.CreateCommand(); cmd.CommandText = "SELECT * FROM " + table + " ORDER BY 1;";
                using var reader = cmd.ExecuteReader(); rows.Add([table]);
                while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
            }
            return JsonSerializer.Serialize(rows);
        }
        public void Dispose() => F.Dispose();
    }

    public static Task FingerprintsAndQualifiedTargets()
    {
        var d = Definition("HostFixture", LockRequirement.HostExclusive);
        var reordered = new OperationDefinition(d.Kind, d.LockRequirement, d.InitialPhase, d.Phases.Values.Reverse());
        Check(d.Fingerprint == reordered.Fingerprint && d.Fingerprint.Length == 64);
        Check(d.Fingerprint != Definition(d.Kind, d.LockRequirement, 2).Fingerprint);
        Check(d.Fingerprint != Definition(d.Kind, LockRequirement.None).Fingerprint);
        var reorderedEdges = d.Phases.Values.Select(p => p.IsTerminal ? OperationPhase.Terminal(p.Name) :
            OperationPhase.Active(p.Name, p.Recovery!.Value, p.NextPhases.Reverse().ToArray()));
        Check(d.Fingerprint == new OperationDefinition(d.Kind, d.LockRequirement, d.InitialPhase, reorderedEdges).Fingerprint);
        var changedRecovery = d.Phases.Values.Select(p => p.Name == "Start" ?
            OperationPhase.Active(p.Name, RecoveryDisposition.RequiresManualReview, p.NextPhases.ToArray()) : p);
        Check(d.Fingerprint != new OperationDefinition(d.Kind, d.LockRequirement, d.InitialPhase, changedRecovery).Fingerprint);
        Reject<ArgumentException>(() => Definition(d.Kind, d.LockRequirement, 0));
        using var r = new Rig(); var op = r.Start(); var state = r.Repository.Read();
        Check(op.Target is HostTarget && state.Locks.Single().Scope is HostScope && state.Locks.Single().OwningOperationId == op.OperationId);
        Check(op.Revision == 1 && state.Revision == 2 && state.Operations.Single().PolicyIsCurrent && !state.HasUnqualifiedState);
        r.Repository.Transition(op.OperationId, 1, "Done", Admit);
        var target = r.Server(); var broad = r.Start(target: target); state = r.Repository.Read();
        Check(broad.Target == target && state.Locks.Single().Scope == new HostScope(r.F.HostId));
        Check(state.Operations.Single(o => o.Operation.OperationId == op.OperationId).Operation.IsTerminal);
        using var other = new Rig(); var sharedProfile = Guid.NewGuid();
        var first = r.Repository.Transition(broad.OperationId, 1, "Done", Admit); Check(first.IsTerminal);
        r.Start("ServerFixture", r.Server(sharedProfile)); other.Start("ServerFixture", other.Server(sharedProfile));
        Check(r.Repository.Read().Locks.Single().Scope != other.Repository.Read().Locks.Single().Scope);
        return Task.CompletedTask;
    }

    public static Task ConflictMatrixAndReadOnlyVisibility()
    {
        using var r = new Rig(); var a = r.Start("ServerFixture", r.Server());
        var b = r.Start("ServerFixture", r.Server(Guid.NewGuid()));
        Reject<OperationConflictException>(() => r.Start());
        Reject<OperationConflictException>(() => r.Start("ServerFixture", a.Target));
        var observation = r.Start("ReadFixture"); var before = r.Repository.Read();
        Check(before.Locks.Count == 2 && before.Operations.Count == 3);
        var second = r.Repository.Read(); Check(second.Revision == before.Revision && second.Locks.SequenceEqual(before.Locks) && second.Operations.SequenceEqual(before.Operations));
        r.Repository.Transition(a.OperationId, 1, "Done", Admit); Reject<OperationConflictException>(() => r.Start());
        r.Repository.Transition(b.OperationId, 1, "Done", Admit); var host = r.Start();
        Reject<OperationConflictException>(() => r.Start("ServerFixture", a.Target));
        Reject<OperationConflictException>(() => r.Start("ServerFixture", b.Target));
        var heartbeat = r.Repository.Heartbeat(observation.OperationId, 1, Admit);
        Check(heartbeat.Revision == 2 && r.Repository.Read().Locks.Single().OwningOperationId == host.OperationId);
        return Task.CompletedTask;
    }

    public static Task ContendingAdmissionAndStaleTransitions()
    {
        using var r = new Rig(); var winners = new System.Collections.Concurrent.ConcurrentBag<DurableOperation>(); var refused = 0;
        Parallel.For(0, 8, i => { try { winners.Add(i % 2 == 0 ? r.Start() : r.Start("ServerFixture", r.Server())); }
            catch (OperationConflictException) { Interlocked.Increment(ref refused); } });
        Check(winners.Count == 1 && refused == 7 && r.Repository.Read().Locks.Count == 1); var op = winners.Single();
        var advances = 0; var stale = 0;
        Parallel.For(0, 8, _ => { try { r.Repository.Transition(op.OperationId, 1, "Work", Admit); Interlocked.Increment(ref advances); }
            catch (StaleWriteConflictException) { Interlocked.Increment(ref stale); } });
        Check(advances == 1 && stale == 7); var oldLock = r.Repository.Read().Locks.Single();
        var heartbeat = r.Repository.Heartbeat(op.OperationId, 2, Admit); Check(heartbeat.Revision == 3 && r.Repository.Read().Locks.Single() == oldLock);
        var before = r.Snapshot(); Reject<StaleWriteConflictException>(() => r.Repository.Transition(op.OperationId, 2, "Done", Admit));
        Reject<ArgumentException>(() => r.Repository.Transition(op.OperationId, 3, "Start", Admit)); Check(r.Snapshot() == before);
        var terminal = r.Repository.Transition(op.OperationId, 3, "Done", Admit); Check(terminal.IsTerminal && terminal.Recovery is null && r.Repository.Read().Locks.Count == 0);
        Reject<OperationConflictException>(() => r.Start("ReadFixture", id: op.OperationId));
        Reject<OperationConflictException>(() => r.Repository.Heartbeat(op.OperationId, 4, Admit));
        return Task.CompletedTask;
    }

    public static Task FaultMatricesRollBackAndRetry()
    {
        (int Stage, string Trigger)[] faults = [
            (0, "AFTER INSERT ON OperationRecords BEGIN DELETE FROM OperationRecords WHERE OperationId=NEW.OperationId; END;"),
            (0, "AFTER INSERT ON OperationLocks BEGIN DELETE FROM OperationLocks WHERE OperationLockId=NEW.OperationLockId; END;"),
            (0, "AFTER INSERT ON OperationLocks BEGIN UPDATE OperationLocks SET OperationKind='Other'; END;"),
            (0, "AFTER INSERT ON OperationRecords BEGIN UPDATE OperationRecords SET RecordRevision=RecordRevision+1; END;"),
            (1, "AFTER UPDATE ON OperationRecords BEGIN SELECT RAISE(ABORT,'fixture phase failure'); END;"),
            (1, "AFTER DELETE ON OperationLocks BEGIN INSERT INTO OperationLocks VALUES(OLD.OperationLockId,OLD.ScopeKind,OLD.ScopeHostId,OLD.ScopeServerProfileId,OLD.OperationKind,OLD.OwningOperationId,OLD.AcquiredUtc); END;"),
            (1, "AFTER UPDATE ON OperationRecords BEGIN UPDATE HostIdentity SET HostBootstrapState='Uninitialized'; END;"),
            (1, "AFTER UPDATE ON OperationRecords BEGIN UPDATE OperationRecords SET RecordRevision=RecordRevision+1; END;"),
            (2, "AFTER UPDATE ON OperationRecords BEGIN DELETE FROM OperationStateRevision; END;"),
            (2, "AFTER UPDATE ON OperationRecords BEGIN UPDATE OperationLocks SET AcquiredUtc='2026-01-01T00:00:00.0000000+00:00'; END;")
        ];
        foreach (var (stage, trigger) in faults)
        {
            using var r = new Rig(); var id = Guid.NewGuid(); if (stage != 0) r.Start(id: id);
            var before = r.Snapshot(); r.F.Execute("CREATE TRIGGER FixtureFault " + trigger);
            DurableOperation Attempt() => stage switch { 0 => r.Start(id: id), 1 => r.Repository.Transition(id, 1, "Done", Admit), _ => r.Repository.Heartbeat(id, 1, Admit) };
            var failed = false; try { Attempt(); } catch (Exception) { failed = true; }
            Check(failed && r.Snapshot() == before); r.F.Execute("DROP TRIGGER FixtureFault;");
            var result = Attempt(); Check(result.Revision == (stage == 0 ? 1 : 2));
            Check(r.Repository.Read().Locks.Count == (stage == 1 ? 0 : 1));
        }
        return Task.CompletedTask;
    }

    public static Task AuthorityCancellationAndClosedInputs()
    {
        foreach (var transition in new[] { false, true }) foreach (var late in new[] { false, true }) foreach (var cancel in new[] { false, true })
        {
            using var r = new Rig(); var id = Guid.NewGuid(); if (transition) r.Start(id: id);
            using var ct = new CancellationTokenSource(); var before = r.Snapshot(); var calls = 0;
            void Authority(SqliteConnection _, SqliteTransaction __)
            {
                calls++; if (calls != (late ? 2 : 1)) return;
                if (cancel) ct.Cancel(); else throw new UnauthorizedAccessException("Fixture current authority refused.");
            }
            void Attempt() { if (transition) r.Repository.Transition(id, 1, "Done", Authority, ct.Token); else r.Repository.Start(id, "HostFixture", r.Host, Authority, ct.Token); }
            if (cancel) Reject<OperationCanceledException>(Attempt); else Reject<UnauthorizedAccessException>(Attempt);
            Check(r.Snapshot() == before); Check(transition ? r.Repository.Transition(id, 1, "Done", Admit).IsTerminal : r.Start(id: id).Revision == 1);
        }
        using var rig = new Rig(); var snapshot = rig.Snapshot();
        Reject<ArgumentException>(() => rig.Start("ServerFixture"));
        Reject<ArgumentException>(() => rig.Start("Unknown"));
        Reject<ArgumentException>(() => rig.Start(target: new HostTarget(Guid.NewGuid())));
        Reject<ArgumentException>(() => rig.Start(id: Guid.Empty));
        Reject<ArgumentNullException>(() => rig.Repository.Start(Guid.NewGuid(), "HostFixture", rig.Host, null!));
        Reject<OperationCanceledException>(() => rig.Repository.Start(Guid.NewGuid(), "HostFixture", rig.Host, Admit, new CancellationToken(true)));
        Check(rig.Snapshot() == snapshot);
        return Task.CompletedTask;
    }

    public static Task HistoricalAndChangedDefinitionsRequireRecovery()
    {
        using (var r = new Rig(11))
        {
            var id = Guid.NewGuid(); var terminalId = Guid.NewGuid(); var now = r.F.Time.Now.ToString("O");
            r.F.Execute($"""
                INSERT INTO OperationRecords (OperationId,Kind,TargetKind,TargetHostId,Phase,StartedUtc)
                    VALUES ('{id:D}','Legacy','HostTarget','{r.F.HostId:D}','Start','{now}');
                INSERT INTO OperationRecords (OperationId,Kind,TargetKind,TargetHostId,Phase,IsTerminal,StartedUtc)
                    VALUES ('{terminalId:D}','Legacy','HostTarget','{r.F.HostId:D}','Done',1,'{now}');
                INSERT INTO OperationLocks VALUES ('{Guid.NewGuid():D}','HostScope','{r.F.HostId:D}',NULL,'Legacy','{id:D}','{now}');
                """);
            HostSchemaMigrationRunner.Default().Migrate(r.F.Writer); var state = r.Repository.Read();
            Check(state.HasUnqualifiedState && state.Locks.Count == 1 && state.Operations.Count == 2);
            Check(state.Operations.All(o => !o.PolicyIsCurrent && o.Operation.PolicyFingerprint is null && o.Operation.LockRequirement is null && o.Operation.Revision == 0));
            var before = r.Snapshot(); Reject<OperationRecoveryRequiredException>(() => r.Start());
            Check(r.Snapshot() == before && state.Operations.Single(o => o.Operation.OperationId == terminalId).Operation.IsTerminal);
        }
        using (var r = new Rig())
        {
            var op = r.Start(); var before = r.Snapshot();
            foreach (var definitions in new[] { Definitions(2), Array.Empty<OperationDefinition>() })
            {
                var changed = new OperationRepository(r.F.Database, r.F.HostId, definitions, r.F.Time);
                Check(changed.Read().HasUnqualifiedState && changed.Read().Locks.Count == 1);
                Reject<OperationRecoveryRequiredException>(() => changed.Transition(op.OperationId, 1, "Done", Admit));
                Check(r.Snapshot() == before);
            }
            r.F.Execute("DELETE FROM OperationLocks;"); Check(r.Repository.Read().HasUnqualifiedState);
            var damaged = r.Snapshot(); Reject<OperationRecoveryRequiredException>(() => r.Start()); Check(r.Snapshot() == damaged);
        }
        return Task.CompletedTask;
    }

    public static Task RevisionIntegrityAndCollateralMutation()
    {
        using (var r = new Rig())
        {
            r.Start(); var wrongScope = Definition("ServerFixture", LockRequirement.ServerExclusive);
            r.F.Execute($"UPDATE OperationRecords SET Kind='ServerFixture',LockRequirement='ServerExclusive',PolicyFingerprint='{wrongScope.Fingerprint}';");
            Check(r.Repository.Read().HasUnqualifiedState && !r.Repository.Read().Operations.Single().PolicyIsCurrent);
        }
        using (var r = new Rig())
        {
            r.F.Execute("UPDATE OperationStateRevision SET Revision=9223372036854775807;"); var before = r.Snapshot();
            Reject<OverflowException>(() => r.Start()); Check(r.Snapshot() == before);
        }
        using (var r = new Rig())
        {
            var op = r.Start(); r.F.Execute("UPDATE OperationRecords SET RecordRevision=9223372036854775807;"); var before = r.Snapshot();
            Reject<OverflowException>(() => r.Repository.Heartbeat(op.OperationId, long.MaxValue, Admit)); Check(r.Snapshot() == before);
        }
        using (var r = new Rig())
        {
            var a = r.Start("ServerFixture", r.Server()); var b = r.Start("ServerFixture", r.Server(Guid.NewGuid())); var before = r.Snapshot();
            r.F.Execute($"CREATE TRIGGER FixtureCollateral AFTER UPDATE ON OperationRecords WHEN NEW.OperationId='{a.OperationId:D}' BEGIN UPDATE OperationRecords SET RecordRevision=RecordRevision+1 WHERE OperationId='{b.OperationId:D}'; END;");
            Reject<InvalidOperationException>(() => r.Repository.Heartbeat(a.OperationId, 1, Admit)); Check(r.Snapshot() == before);
            r.F.Execute("DROP TRIGGER FixtureCollateral;"); r.F.Time.Now -= TimeSpan.FromDays(1);
            Check(r.Repository.Heartbeat(a.OperationId, 1, Admit).LastHeartbeatUtc == a.LastHeartbeatUtc);
        }
        return Task.CompletedTask;
    }
}
