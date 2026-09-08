using Google.Protobuf;
using PalworldServerManager.Contracts;
using PalworldServerManager.Core.Operations;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using Wire = PalworldServerManager.Contracts.Wire;
using static PalworldServerManager.SelfTest.OperationLifecycleTests;

namespace PalworldServerManager.SelfTest;

internal static class OperationCrossHostTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(15));
    private static Wire.OperationActivitySnapshot Observe(HostOperationRuntime runtime, Guid host)
    {
        var offer = new Wire.Handshake { Protocol = new() { Major = 1, Minor = 11 } };
        offer.Capabilities.Add(Wire.FeatureCapability.OperationActivity);
        var wire = HostOperationActivity.ToWire(host, runtime.Read(), NegotiatedProtocol.Negotiate(offer, offer.Clone()));
        var clientCopy = Wire.OperationActivitySnapshot.Parser.ParseFrom(wire.ToByteArray());
        Check(OperationActivityValidation.IsValid(clientCopy)); return clientCopy;
    }

    // Representative routing and independent contract consumers, not installed business
    // RPCs, remote authorization or physical PCs. Both Hosts own real SQLite state/tasks.
    public static async Task DestinationOwnershipVisibilityAndDisconnectAcrossScopes()
    {
        foreach (var shape in new[] { "host", "server", "server-host-lock" })
        {
            using var a = new PeerTrustTests.Fixture(); using var b = new PeerTrustTests.Fixture();
            var profile = Guid.NewGuid(); var enteredA = Signal(); var enteredB = Signal(); var releaseA = Signal(); var releaseB = Signal();
            var definition = Definition("RoutingFixture", shape == "server" ? LockRequirement.ServerExclusive : LockRequirement.HostExclusive);
            OperationTarget targetA = shape == "host" ? new HostTarget(a.HostId) : new ServerTarget(new(a.HostId, profile));
            OperationTarget targetB = shape == "host" ? new HostTarget(b.HostId) : new ServerTarget(new(b.HostId, profile));
            var callsA = 0; var callsB = 0; CancellationToken tokenB = default;
            await using var ownerA = new HostOperationLifetime(a.Database, a.HostId,
                [new(definition, async (execution, _) => { Interlocked.Increment(ref callsA); enteredA.SetResult(); await releaseA.Task; execution.Transition("Done", Admit); })]);
            await using var ownerB = new HostOperationLifetime(b.Database, b.HostId,
                [new(definition, async (execution, stop) => { tokenB = stop; Interlocked.Increment(ref callsB); enteredB.SetResult(); await releaseB.Task; execution.Transition("Done", Admit); })]);
            var runtimeA = ownerA.GetReady(); var runtimeB = ownerB.GetReady();
            try
            {
                var initialA = Observe(runtimeA, a.HostId);
                Reject<ArgumentException>(() => runtimeA.Start(Guid.NewGuid(), definition.Kind, targetB, Admit));
                Check(Observe(runtimeA, a.HostId).Equals(initialA) && callsA == 0);
                using var client = new CancellationTokenSource();
                var operationB = runtimeB.Start(Guid.NewGuid(), definition.Kind, targetB, Admit, client.Token);
                await Wait(enteredB.Task);
                var firstClient = Observe(runtimeB, b.HostId); var secondClient = Observe(runtimeB, b.HostId);
                Check(firstClient.Equals(secondClient) && firstClient.Locks.Count == 1 && firstClient.Operations.Count == 1);
                Check(firstClient.Locks[0].OwningOperationId == operationB.OperationId.ToString("D"));
                Check(firstClient.Locks[0].Scope.ScopeCase == (shape == "server" ? Wire.OperationLockScope.ScopeOneofCase.Server : Wire.OperationLockScope.ScopeOneofCase.HostId));
                Check(firstClient.Operations[0].Target.TargetCase == (shape == "host" ? Wire.OperationTarget.TargetOneofCase.HostId : Wire.OperationTarget.TargetOneofCase.Server));
                client.Cancel(); Check(!tokenB.IsCancellationRequested);
                try { await runtimeB.WaitForCurrentWorkersAsync(client.Token); throw new Exception("Canceled client wait succeeded."); }
                catch (OperationCanceledException) { }
                Check(Observe(runtimeB, b.HostId).Equals(secondClient) && Observe(runtimeA, a.HostId).Equals(initialA));
                Reject<OperationConflictException>(() => runtimeB.Start(Guid.NewGuid(), definition.Kind, targetB, Admit));
                var operationA = runtimeA.Start(Guid.NewGuid(), definition.Kind, targetA, Admit); await Wait(enteredA.Task);
                Check(callsA == 1 && callsB == 1 && runtimeA.Read().DurableState.Locks.Count == 1 && runtimeB.Read().DurableState.Locks.Count == 1);
                if (shape != "host")
                {
                    var serverA = Observe(runtimeA, a.HostId).Operations[0].Target.Server;
                    var serverB = secondClient.Operations[0].Target.Server;
                    Check(serverA.ServerProfileId == serverB.ServerProfileId && serverA.AuthoritativeHostId != serverB.AuthoritativeHostId);
                }
                releaseB.SetResult(); await Wait(runtimeB.WaitForCurrentWorkersAsync());
                Check(Observe(runtimeB, b.HostId).Locks.Count == 0 && Observe(runtimeB, b.HostId).Operations[0].Status == Wire.OperationActivityStatus.Resolved);
                Check(runtimeA.Read().DurableState.Locks.Single().OwningOperationId == operationA.OperationId);
                Check(Observe(runtimeA, a.HostId).Operations[0].Status == Wire.OperationActivityStatus.Running);
                releaseA.SetResult(); await Wait(runtimeA.WaitForCurrentWorkersAsync());
                Check(runtimeA.Read().DurableState.Locks.Count == 0 && callsA == 1 && callsB == 1);
            }
            finally { releaseA.TrySetResult(); releaseB.TrySetResult(); }
        }
    }
}
