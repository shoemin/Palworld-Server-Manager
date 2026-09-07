using System.Security.Principal;
using System.Text.Json;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.WindowsPeerProcessFixture;

namespace PalworldServerManager.SelfTest;

// Real native PAKE creates both bindings. Only Owner initialization and the activation hook
// are fixtures; the restarted child has no native provider and cannot repeat PAKE.
internal static class WindowsPeerPairingCrashQualification
{
    internal static async Task Run(string serviceRoot, SecurityIdentifier sid, string nativePath, string nativeHash, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var ct = timeout.Token; var nonce = Guid.NewGuid(); var aId = Guid.NewGuid(); var bId = Guid.NewGuid();
        var parent = Path.GetFullPath(serviceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(parent, "peer-process-" + nonce.ToString("N")));
        Check(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(root), "Unsafe or reused pairing fixture root.");
        var a = new FixtureHost(new(nonce, aId, bId, Guid.NewGuid(), Path.Combine(root, "host-" + aId.ToString("N")), sid.Value));
        var b = new FixtureHost(new(nonce, bId, aId, Guid.NewGuid(), Path.Combine(root, "host-" + bId.ToString("N")), sid.Value, nativePath, nativeHash));
        RequirePath(a.Config); RequirePath(b.Config);
        using var aLease = Lease(a.Config);
        using var provider = new WindowsSpake2Provider(nativePath, nativeHash);
        HostGenerationTransitions? sender = null; Child? receiver = null; bool initialized = false;
        try
        {
            var senderPin = await a.Initialize(ct); string receiverPin;
            using (var lease = Lease(b.Config))
            {
                receiverPin = await b.Initialize(ct); initialized = true;
                Check(a.Peers.Read(bId) is null && b.Peers.Read(aId) is null, "Pairing must start without seeded trust.");
                a.RequireNoGrants(0); b.RequireNoGrants(0);
                File.WriteAllText(Path.Combine(b.Config.Root, ConfigName), JsonSerializer.Serialize(b.Config));
            }
            sender = a.Create(provider); await sender.StartAsync(ct);
            receiver = new Child(b.Config); var first = await receiver.Read(ct);
            Check(first.Kind == "ready" && first.Pin == receiverPin && !string.IsNullOrEmpty(first.Key) && first.PeerState is null,
                "Initial receiver unexpectedly has trust or lacks a native identity.");
            using (var denied = HostExclusivityLock.TryAcquire(TimeSpan.Zero, b.Config.Mutex)) Check(denied is null, "Receiver lease not held.");
            using var invitation = await sender.CreateInvitationAsync(ct);
            var paired = await receiver.Pair(sender.Endpoints!.Value.Pairing, invitation.Code, ct);
            Check(paired.Kind == "paired" && paired.CurrentPeerPin == senderPin && paired.PeerState == "PeerBound" && paired.BindingExpiry is not null,
                "Actual native exchange did not bind the expected sender.");
            var senderBound = a.Peers.Read(bId)!;
            Check(senderBound.State == "PeerBound" && senderBound.CurrentFingerprint == receiverPin && !senderBound.RecoveryRequired &&
                senderBound.ExpiresUtc is not null, "Sender did not independently commit PeerBound.");
            a.RequireNoGrants(0);
            await receiver.Kill(requireRunning: true);
            using (var lease = Lease(b.Config))
            {
                b.RequireFixture(); b.RequireNoGrants(0); var persisted = b.Peers.Read(aId)!;
                Check(persisted.State == "PeerBound" && !persisted.RecoveryRequired && persisted.CurrentFingerprint == senderPin &&
                    persisted.LocalBoundFingerprint == receiverPin && persisted.ExpiresUtc == paired.BindingExpiry,
                    "Process termination lost or changed durable native binding.");
                // This new configuration cannot construct a native exchange. No code is kept
                // in config/storage; recovery must use the already bound mutual-TLS pins.
                b = new FixtureHost(b.Config with { NativePath = null, NativeHash = null });
                File.WriteAllText(Path.Combine(b.Config.Root, ConfigName), JsonSerializer.Serialize(b.Config));
            }
            receiver.Dispose(); receiver = null;
            receiver = new Child(b.Config); var restarted = await receiver.Read(ct);
            Check(restarted.Kind == "ready" && restarted.Instance != first.Instance && restarted.Pin == first.Pin && restarted.Key == first.Key &&
                restarted.CurrentPeerPin == senderPin && restarted.PeerState == "PeerBound" && restarted.BindingExpiry == paired.BindingExpiry,
                "Fresh receiver changed its durable binding or native identity.");
            var activated = await receiver.Command("activate", sender.Endpoints!.Value.Peer, ct);
            Check(activated.Kind == "activated" && activated.PeerState == "Active" && activated.BindingExpiry is null, "Pinned activation did not recover.");
            var retry = await receiver.Command("activate", sender.Endpoints!.Value.Peer, ct);
            Check(retry.Kind == "activated" && retry.PeerState == "Active" && retry.Pin == receiverPin, "Activation retry changed identity/state.");
            a.RequireNoGrants(); Check(a.Peers.Read(bId)!.State == "Active", "Sender activation did not commit independently.");
            await receiver.Stop(ct); receiver.Dispose(); receiver = null;
            using (var lease = Lease(b.Config))
            {
                b.RequireNoGrants(); var persisted = b.Peers.Read(aId)!;
                Check(persisted.State == "Active" && persisted.CurrentFingerprint == senderPin && persisted.ExpiresUtc is null,
                    "Activated trust did not survive final exit.");
                using var c = b.Database.OpenConnection();
                Check(HostDatabase.QueryScalarLong(c, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerBoundCreated';") == 1,
                    "Recovery repeated pairing or invented a second binding.");
            }
            using (var c = a.Database.OpenConnection())
                Check(HostDatabase.QueryScalarLong(c, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerBoundCreated';") == 1,
                    "Sender pairing was repeated during activation recovery.");
        }
        finally
        {
            try { if (receiver is not null) { await receiver.Kill(); receiver.Dispose(); } }
            finally { if (sender is not null) await sender.StopAsync(); }
            if (initialized)
            {
                using var lease = Lease(b.Config); await b.Cleanup(); await a.Cleanup();
                Check(Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "Unsafe pairing cleanup root.");
                Directory.Delete(root, false);
            }
        }
    }
}
