using System.Security.Authentication;
using System.Security.Principal;
using System.Text.Json;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using static PalworldServerManager.SelfTest.WindowsPeerProcessFixture;

namespace PalworldServerManager.SelfTest;

// Actual receiving-process receipt recovery; verified bindings remain explicit fixture inputs.
internal static class WindowsPeerReceiptCrashQualification
{
    internal static async Task Run(string serviceRoot, SecurityIdentifier sid, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var ct = timeout.Token; var nonce = Guid.NewGuid(); var aId = Guid.NewGuid(); var bId = Guid.NewGuid();
        var parent = Path.GetFullPath(serviceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(parent, "peer-process-" + nonce.ToString("N")));
        Check(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(root), "Unsafe or reused fixture root.");
        var a = new FixtureHost(new(nonce, aId, bId, Guid.NewGuid(), Path.Combine(root, "host-" + aId.ToString("N")), sid.Value));
        var b = new FixtureHost(new(nonce, bId, aId, Guid.NewGuid(), Path.Combine(root, "host-" + bId.ToString("N")), sid.Value));
        RequirePath(a.Config); RequirePath(b.Config);
        using var aLease = Lease(a.Config);
        HostGenerationTransitions? sender = null; Child? receiver = null; bool initialized = false;
        try
        {
            var oldPin = await a.Initialize(ct); string receiverPin;
            using (var lease = Lease(b.Config))
            {
                receiverPin = await b.Initialize(ct); initialized = true;
                // These are explicit verified-binding fixture inputs using actual public pins.
                a.Peers.RecordVerifiedBinding(bId, receiverPin, oldPin);
                b.Peers.RecordVerifiedBinding(aId, oldPin, receiverPin);
                File.WriteAllText(Path.Combine(b.Config.Root, ConfigName), JsonSerializer.Serialize(b.Config));
            }
            sender = a.Create(); await sender.StartAsync(ct);
            receiver = new Child(b.Config); var first = await receiver.Read(ct);
            Check(first.Kind == "ready" && first.Pin == receiverPin && first.CurrentPeerPin == oldPin, "Initial child state differs.");
            using (var denied = HostExclusivityLock.TryAcquire(TimeSpan.Zero, b.Config.Mutex))
                Check(denied is null, "Receiver did not hold its own fixture lease.");
            var activated = await receiver.Command("activate", sender.Endpoints!.Value.Peer, ct);
            Check(activated.Kind == "activated", "Actual activation failed."); a.RequireNoGrants();
            var rotation = a.State.PrepareRoutineRotation(a.Actor, Guid.NewGuid());
            rotation = await new RoutineRotationMaterialCoordinator(a.State, a.Material).PrepareAsync(a.Actor, rotation.RotationId, ct);
            a.State.BeginRoutineRotationStaging(a.Actor, rotation.RotationId);
            var proposal = a.State.PrepareRoutineRotationProposal(a.Actor, rotation.RotationId);
            await sender.StageRotationAsync(bId, first.Address, rotation.RotationId, ct);
            await sender.CutOverAsync(a.Actor, rotation.RotationId, new Dictionary<Guid, Uri> { [bId] = first.Address }, ct);
            var promoted = await receiver.Command("activate", sender.Endpoints!.Value.Peer, ct);
            Check(promoted.CurrentPeerPin == proposal.NewFingerprint && promoted.PendingRotation == rotation.RotationId,
                "Actual New TLS did not leave an undelivered receipt.");
            await SecureStoreTests.Reject<AuthenticationException>(() => sender.CompleteRotationAsync(a.Actor, rotation.RotationId, ct));
            await receiver.Kill(requireRunning: true); // Actual termination, without Host/generation Dispose.
            using (var lease = Lease(b.Config))
            {
                b.RequireFixture(); b.RequireNoGrants(); var persisted = b.Peers.Read(aId)!;
                Check(persisted.State == "Active" && !persisted.RecoveryRequired && persisted.CurrentFingerprint == proposal.NewFingerprint &&
                    persisted.PendingRotationId == rotation.RotationId && persisted.PendingFingerprint is null && persisted.PendingRotationExpiresUtc is null,
                    "Process exit lost durable promotion or retained receipt.");
            }
            receiver.Dispose(); receiver = null;
            receiver = new Child(b.Config); var restarted = await receiver.Read(ct);
            Check(restarted.Kind == "ready" && restarted.Instance != first.Instance && restarted.Pin == first.Pin && restarted.Key == first.Key &&
                restarted.CurrentPeerPin == proposal.NewFingerprint && restarted.PendingRotation == rotation.RotationId,
                "Fresh process changed protected/native identity or lost receipt.");
            var confirmed = await receiver.Command("receipt", sender.Endpoints!.Value.Peer, ct);
            Check(confirmed.Kind == "confirmed" && confirmed.PendingRotation is null, "Ordinary receipt retry did not clear retained ID.");
            await receiver.Stop(ct); receiver.Dispose(); receiver = null;
            using (var lease = Lease(b.Config))
            {
                b.RequireNoGrants(); Check(b.Peers.Read(aId)!.PendingRotationId is null, "Receipt acknowledgement did not survive orderly exit.");
            }
            using (var c = a.Database.OpenConnection())
                Check(HostDatabase.QueryScalarLong(c, "SELECT COUNT(*) FROM HostRotationPromotionEvidence e JOIN PeerRelationshipIncarnations i " +
                    "ON i.PeerHostId=e.PeerHostId AND i.Incarnation=e.Incarnation;") == 1, "Expected exactly one current-relationship receipt.");
            Check((await sender.CompleteRotationAsync(a.Actor, rotation.RotationId, ct)).State == HostCredentialRotationState.Completed,
                "Recovered receipt did not permit completion."); a.RequireNoGrants();
        }
        finally
        {
            // Failure to observe exit/closure must stop cleanup; never delete possibly borrowed material.
            try { if (receiver is not null) { await receiver.Kill(); receiver.Dispose(); } }
            finally { if (sender is not null) await sender.StopAsync(); }
            if (initialized)
            {
                using var lease = Lease(b.Config); await b.Cleanup(); await a.Cleanup();
                Check(Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "Unsafe cleanup root.");
                Directory.Delete(root, false);
            }
        }
    }
}
