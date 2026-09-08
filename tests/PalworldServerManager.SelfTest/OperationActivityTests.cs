using Google.Protobuf;
using PalworldServerManager.Contracts;
using PalworldServerManager.Core.Operations;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using Wire = PalworldServerManager.Contracts.Wire;
using static PalworldServerManager.SelfTest.OperationLifecycleTests;

namespace PalworldServerManager.SelfTest;

internal static class OperationActivityTests
{
    private static NegotiatedProtocol Protocol(bool offer = true)
    {
        var a = new Wire.Handshake { Protocol = new() { Major = 1, Minor = 11 } };
        var b = a.Clone(); a.Capabilities.Add(Wire.FeatureCapability.OperationActivity);
        if (offer) b.Capabilities.Add(Wire.FeatureCapability.OperationActivity);
        return NegotiatedProtocol.Negotiate(a, b);
    }
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(15));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Wire.OperationActivitySnapshot Read(HostOperationRuntime runtime, Guid host)
        => HostOperationActivity.ToWire(host, runtime.Read(), Protocol());
    private static Wire.OperationActivitySnapshot Sample()
    {
        var host = Guid.NewGuid(); var op = Guid.NewGuid(); var stamp = DateTimeOffset.UtcNow.ToString("O");
        var result = new Wire.OperationActivitySnapshot { AuthoritativeHostId = host.ToString("D"), DurableRevision = 2 };
        result.Operations.Add(new Wire.OperationActivityItem { OperationId = op.ToString("D"), Kind = "Fixture", Phase = "Start",
            Target = new() { Server = new() { AuthoritativeHostId = host.ToString("D"), ServerProfileId = Guid.NewGuid().ToString("D") } },
            Status = Wire.OperationActivityStatus.AwaitingRecovery, Recovery = Wire.OperationRecoveryDisposition.SafeToRetryFromStart,
            RecordRevision = 1, StartedUtc = stamp });
        result.Locks.Add(new Wire.OperationActivityLock { LockId = Guid.NewGuid().ToString("D"), Kind = "Fixture",
            Scope = new() { HostId = host.ToString("D") }, OwningOperationId = op.ToString("D"), AcquiredUtc = stamp });
        return result;
    }

    public static async Task TargetsScopesAndIndependentReaders()
    {
        var profile = Guid.NewGuid(); var results = new List<Wire.OperationActivitySnapshot>();
        foreach (var hostTarget in new[] { false, true }) foreach (var serverLock in new[] { false, true })
        {
            if (hostTarget && serverLock) continue;
            using var f = new PeerTrustTests.Fixture();
            var definition = Definition("Fixture", serverLock ? LockRequirement.ServerExclusive : LockRequirement.HostExclusive);
            var repository = new OperationRepository(f.Database, f.HostId, [definition]);
            OperationTarget target = hostTarget ? new HostTarget(f.HostId) : new ServerTarget(new(f.HostId, profile));
            var op = repository.Start(Guid.NewGuid(), definition.Kind, target, Admit);
            await using var runtime = new HostOperationRuntime(f.Database, f.HostId, [new(definition, (_, _) => Task.CompletedTask)]);
            var first = Read(runtime, f.HostId); var second = Wire.OperationActivitySnapshot.Parser.ParseFrom(Read(runtime, f.HostId).ToByteArray());
            Check(first.Equals(second) && OperationActivityValidation.IsValid(second));
            Check(first.Operations.Single().OperationId == op.OperationId.ToString("D"));
            Check(first.Operations.Single().Target.TargetCase == (hostTarget ? Wire.OperationTarget.TargetOneofCase.HostId : Wire.OperationTarget.TargetOneofCase.Server));
            Check(first.Locks.Single().Scope.ScopeCase == (serverLock ? Wire.OperationLockScope.ScopeOneofCase.Server : Wire.OperationLockScope.ScopeOneofCase.HostId));
            if (!hostTarget) { Check(first.Operations.Single().Target.Server.ServerProfileId == profile.ToString("D")); results.Add(first); }
            first.Operations[0].Kind = "Changed"; first.Locks.Clear(); Check(Read(runtime, f.HostId).Equals(second));
        }
        Check(results.Count == 2 && results[0].AuthoritativeHostId != results[1].AuthoritativeHostId);
        Check(!results[0].Operations[0].Target.Server.Equals(results[1].Operations[0].Target.Server));
    }

    public static Task MalformedAndUnknownWireValuesRefuse()
    {
        var sample = Sample(); Check(OperationActivityValidation.IsValid(sample));
        Action<Wire.OperationActivitySnapshot>[] damage = [
            s => s.AuthoritativeHostId = "", s => s.DurableRevision = -1,
            s => s.Operations[0].OperationId = Guid.Empty.ToString("D"), s => s.Operations[0].Kind = "raw exception text",
            s => s.Operations[0].Phase = new string('A', 65), s => s.Operations[0].RecordRevision = -1,
            s => s.Operations[0].Target = new(), s => s.Operations[0].Target.Server.AuthoritativeHostId = Guid.NewGuid().ToString("D"),
            s => s.Operations[0].Target.Server.ServerProfileId = "bad", s => s.Operations[0].StartedUtc = "today",
            s => s.Operations[0].LastHeartbeatUtc = "", s => s.Operations[0].Status = (Wire.OperationActivityStatus)999,
            s => s.Operations[0].Status = 0, s => s.Operations[0].Recovery = (Wire.OperationRecoveryDisposition)999,
            s => s.Operations[0].Recovery = 0, s => s.Locks[0].Scope = new(),
            s => s.Locks[0].Scope.HostId = Guid.NewGuid().ToString("D"), s => s.Locks[0].LockId = "bad",
            s => s.Locks[0].OwningOperationId = "bad", s => s.Locks[0].AcquiredUtc = "2026-01-01T00:00:00.0000000+01:00",
            s => s.Operations.Add(s.Operations[0].Clone()), s => s.Locks.Add(s.Locks[0].Clone()) ];
        foreach (var change in damage)
        { var broken = sample.Clone(); change(broken); Check(!OperationActivityValidation.IsValid(Wire.OperationActivitySnapshot.Parser.ParseFrom(broken.ToByteArray()))); }
        Check(!OperationActivityValidation.IsValid(null));
        var duplicateCase = sample.Clone(); var copied = duplicateCase.Operations[0].Clone(); copied.OperationId = copied.OperationId.ToUpperInvariant();
        duplicateCase.Operations.Add(copied); Check(!OperationActivityValidation.IsValid(duplicateCase));
        return Task.CompletedTask;
    }

    public static async Task RuntimeStatusChangesWithoutDurableRevision()
    {
        using var f = new PeerTrustTests.Fixture(); var definition = Definition("Fixture", LockRequirement.ServerExclusive);
        var entered = Signal(); var release = Signal();
        await using var runtime = new HostOperationRuntime(f.Database, f.HostId,
            [new(definition, async (_, _) => { entered.SetResult(); await release.Task; throw new Exception("private fixture failure"); })]);
        try
        {
            runtime.Start(Guid.NewGuid(), definition.Kind, new ServerTarget(new(f.HostId, f.PeerId)), Admit); await Wait(entered.Task);
            var running = Read(runtime, f.HostId); Check(running.Operations[0].Status == Wire.OperationActivityStatus.Running);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Reject<OperationCanceledException>(() => runtime.Read(canceled.Token));
            Check(Read(runtime, f.HostId).Equals(running));
            release.SetResult(); await Wait(runtime.WaitForCurrentWorkersAsync());
            var failed = Read(runtime, f.HostId); Check(failed.DurableRevision == running.DurableRevision && !failed.Equals(running));
            Check(failed.Operations[0].Status == Wire.OperationActivityStatus.RecoveryRequired && failed.Locks.Equals(running.Locks));
            Check(!failed.ToString().Contains("private fixture failure", StringComparison.Ordinal));
            Check(Wire.OperationActivitySnapshot.Parser.ParseFrom(failed.ToByteArray()).Equals(Read(runtime, f.HostId)));
        }
        finally { release.TrySetResult(); }
    }

    public static async Task InconsistentHistoryAndOrphanLocksRemainVisible()
    {
        using var f = new PeerTrustTests.Fixture(); var definition = Definition("Fixture", LockRequirement.HostExclusive);
        var repository = new OperationRepository(f.Database, f.HostId, [definition]);
        var op = repository.Start(Guid.NewGuid(), definition.Kind, new HostTarget(f.HostId), Admit);
        f.Execute("UPDATE OperationRecords SET IsTerminal=1,Phase='Done',RecoveryDisposition=NULL,RecordRevision=RecordRevision+1;");
        await using var runtime = new HostOperationRuntime(f.Database, f.HostId, [new(definition, (_, _) => Task.CompletedTask)]);
        var damaged = Read(runtime, f.HostId); Check(damaged.HasUnqualifiedState && damaged.Locks.Count == 1);
        Check(damaged.Operations[0].Status == Wire.OperationActivityStatus.RecoveryRequired && !damaged.Operations[0].HasRecovery);
        // Deliberately corrupt this isolated fixture through an offline-style connection;
        // normal foreign-key enforcement correctly prevents creating an orphan.
        f.Execute("PRAGMA foreign_keys=OFF;");
        try { f.Execute("DELETE FROM OperationRecords;"); }
        finally { f.Execute("PRAGMA foreign_keys=ON;"); }
        var orphan = Read(runtime, f.HostId); Check(orphan.HasUnqualifiedState && orphan.Operations.Count == 0 && orphan.Locks.Count == 1);
        Check(orphan.Locks[0].OwningOperationId == op.OperationId.ToString("D") && OperationActivityValidation.IsValid(orphan));
    }

    public static Task NegotiationAndEveryObservationValue()
    {
        var host = Guid.NewGuid(); var definitions = new List<OperationObservation>(); var observations = new List<HostOperationObservation>();
        foreach (var status in Enum.GetValues<HostOperationStatus>()) foreach (var recovery in Enum.GetValues<RecoveryDisposition>())
        {
            var op = new DurableOperation(Guid.NewGuid(), "History", new HostTarget(host), "KnownShape", false, recovery, null, null, 0, DateTimeOffset.UtcNow, null);
            definitions.Add(new(op, false)); observations.Add(new(op, status));
        }
        var state = new HostOperationRuntimeSnapshot(new(0, definitions, [], true), observations.AsReadOnly());
        Reject<InvalidOperationException>(() => HostOperationActivity.ToWire(host, state, Protocol(false)));
        var result = HostOperationActivity.ToWire(host, state, Protocol());
        Check(result.Operations.Count == 16 && result.Operations.All(o => o.HasRecovery && !o.HasLastHeartbeatUtc && o.RecordRevision == 0));
        Check(result.Operations.Select(o => o.Status).Distinct().Count() == 4 && result.Operations.Select(o => o.Recovery).Distinct().Count() == 4);
        for (var i = 0; i < observations.Count; i++)
        {
            var expected = observations[i]; var actual = result.Operations[i];
            Check(actual.OperationId == expected.Operation.OperationId.ToString("D"));
            Check((int)actual.Status == (int)expected.Status + 1 && (int)actual.Recovery == (int)expected.Operation.Recovery!.Value);
        }
        Reject<InvalidDataException>(() => HostOperationActivity.ToWire(Guid.NewGuid(), state, Protocol()));
        var badStatus = new HostOperationRuntimeSnapshot(state.DurableState, [observations[0] with { Status = (HostOperationStatus)999 }]);
        Reject<InvalidDataException>(() => HostOperationActivity.ToWire(host, badStatus, Protocol()));
        var badRecovery = new HostOperationRuntimeSnapshot(state.DurableState,
            [observations[0] with { Operation = observations[0].Operation with { Recovery = (RecoveryDisposition)999 } }]);
        Reject<InvalidDataException>(() => HostOperationActivity.ToWire(host, badRecovery, Protocol()));
        using var bytes = new MemoryStream(); result.WriteTo(bytes);
        using (var writer = new CodedOutputStream(bytes, true)) { writer.WriteTag(100, WireFormat.WireType.LengthDelimited); writer.WriteString("future"); writer.Flush(); }
        var serialized = bytes.ToArray(); var parsed = Wire.OperationActivitySnapshot.Parser.ParseFrom(serialized);
        Check(OperationActivityValidation.IsValid(parsed) && parsed.ToByteArray().SequenceEqual(serialized));
        return Task.CompletedTask;
    }
}
