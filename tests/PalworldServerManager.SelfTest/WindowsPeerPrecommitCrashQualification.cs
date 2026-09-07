using System.Security.Authentication;
using System.Security.Principal;
using System.Text.Json;
using Grpc.Core;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.WindowsPeerProcessFixture;

namespace PalworldServerManager.SelfTest;

internal static class WindowsPeerPrecommitCrashQualification
{
    private static void Unbound(FixtureHost host, string peerPin)
    {
        host.RequireFixture(); host.RequireNoGrants(0);
        Check(host.Peers.Read(host.Config.Peer) is null, "An interrupted exchange persisted trust.");
        using var c = host.Database.OpenConnection();
        foreach (var table in new[] { "TrustedManagers", "TrustedManagerPairings", "PeerLocalBindingEvidence", "PeerRelationshipIncarnations" })
            Check(HostDatabase.QueryScalarLong(c, "SELECT COUNT(*) FROM " + table + ";") == 0, "Interrupted exchange left durable binding evidence.");
        Check(HostDatabase.QueryScalarLong(c, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerBoundCreated';") == 0,
            "Interrupted exchange recorded a binding audit.");
        foreach (var purpose in new[] { PeerTrafficPurpose.PairingFinalization, PeerTrafficPurpose.OrdinaryManagement, PeerTrafficPurpose.TrustMaintenance })
        {
            try { new PeerTransportAuthentication(host.Peers).AdmitHandshake(host.Config.Peer, peerPin, purpose); throw new Exception("Unbound peer admitted."); }
            catch (AuthenticationException) { }
        }
    }
    internal static async Task Run(string serviceRoot, SecurityIdentifier sid, string nativePath, string nativeHash, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var ct = timeout.Token; var nonce = Guid.NewGuid(); var aId = Guid.NewGuid(); var bId = Guid.NewGuid();
        var parent = Path.GetFullPath(serviceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(parent, "peer-process-" + nonce.ToString("N")));
        Check(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(root), "Unsafe precommit fixture root.");
        var a = new FixtureHost(new(nonce, aId, bId, Guid.NewGuid(), Path.Combine(root, "host-" + aId.ToString("N")), sid.Value));
        var b = new FixtureHost(new(nonce, bId, aId, Guid.NewGuid(), Path.Combine(root, "host-" + bId.ToString("N")), sid.Value, nativePath, nativeHash, true));
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
                Unbound(a, receiverPin); Unbound(b, senderPin);
                File.WriteAllText(Path.Combine(b.Config.Root, ConfigName), JsonSerializer.Serialize(b.Config));
            }
            sender = a.Create(provider); await sender.StartAsync(ct);
            receiver = new Child(b.Config); var first = await receiver.Read(ct);
            Check(first.Kind == "ready" && first.Pin == receiverPin && !string.IsNullOrEmpty(first.Key) && first.PeerState is null,
                "Initial precommit process has unexpected trust or identity.");
            using (var denied = HostExclusivityLock.TryAcquire(TimeSpan.Zero, b.Config.Mutex)) Check(denied is null, "Child lease not held.");
            Guid oldInvitation; Report barrier;
            using (var invitation = await sender.CreateInvitationAsync(ct))
            {
                oldInvitation = invitation.Id;
                barrier = await receiver.Pair(sender.Endpoints!.Value.Pairing, invitation.Code, ct);
            }
            Check(barrier.Kind == "verified-before-store" && barrier.VerifiedPeerPin == senderPin && barrier.PeerState is null &&
                barrier.CurrentPeerPin is null && barrier.BindingExpiry is null, "Actual native verification did not stop before persistence.");
            Unbound(a, receiverPin);
            await receiver.Kill(requireRunning: true);
            await sender.CancelInvitationAsync(oldInvitation, ct);
            // One interrupted attempt incurs the coordinator's real one-second source
            // backoff. Keep its policy intact and wait before attempting a fresh exchange.
            await Task.Delay(TimeSpan.FromMilliseconds(1100), ct);
            using (var lease = Lease(b.Config))
            {
                Unbound(b, senderPin); Unbound(a, receiverPin);
                b = new FixtureHost(b.Config with { PauseBeforePeerBound = false });
                File.WriteAllText(Path.Combine(b.Config.Root, ConfigName), JsonSerializer.Serialize(b.Config));
            }
            receiver.Dispose(); receiver = null;
            receiver = new Child(b.Config); var restarted = await receiver.Read(ct);
            Check(restarted.Kind == "ready" && restarted.Instance != first.Instance && restarted.Pin == first.Pin && restarted.Key == first.Key &&
                restarted.PeerState is null && restarted.CurrentPeerPin is null && restarted.BindingExpiry is null,
                "Restart resurrected ephemeral trust or changed the native identity.");
            // The peer listener is live, but neither Host has a credential binding. The
            // direct admission assertions above also exclude a generic network failure as proof.
            try { await sender.ActivateAsync(bId, restarted.Address, ct); throw new Exception("Unbound activation succeeded."); }
            catch (RpcException error) when (error.StatusCode == StatusCode.Unavailable) { }
            Unbound(a, receiverPin);
            Report paired;
            using (var invitation = await sender.CreateInvitationAsync(ct))
            {
                Check(invitation.Id != oldInvitation, "Fresh pairing reused an invitation identity.");
                paired = await receiver.Pair(sender.Endpoints!.Value.Pairing, invitation.Code, ct);
            }
            Check(paired.Kind == "paired" && paired.CurrentPeerPin == senderPin && paired.PeerState == "PeerBound" && paired.BindingExpiry is not null,
                "Fresh native pairing did not create trust.");
            a.RequireNoGrants(0); Check(a.Peers.Read(bId)!.State == "PeerBound", "Responder did not commit fresh binding.");
            var activated = await receiver.Command("activate", sender.Endpoints!.Value.Peer, ct);
            Check(activated.Kind == "activated" && activated.PeerState == "Active", "Fresh pairing could not activate.");
            a.RequireNoGrants(); Check(a.Peers.Read(bId)!.State == "Active", "Responder did not activate independently.");
            await receiver.Stop(ct); receiver.Dispose(); receiver = null;
            using (var lease = Lease(b.Config))
            {
                b.RequireNoGrants(); Check(b.Peers.Read(aId)!.State == "Active", "Fresh activation did not persist.");
                foreach (var host in new[] { a, b })
                {
                    using var c = host.Database.OpenConnection();
                    Check(HostDatabase.QueryScalarLong(c, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerBoundCreated';") == 1,
                        "Interrupted pairing contributed durable binding history.");
                }
            }
        }
        finally
        {
            try { if (receiver is not null) { await receiver.Kill(); receiver.Dispose(); } }
            finally { if (sender is not null) await sender.StopAsync(); }
            if (initialized)
            {
                using var lease = Lease(b.Config); await b.Cleanup(); await a.Cleanup();
                Check(Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "Unsafe precommit cleanup root.");
                Directory.Delete(root, false);
            }
        }
    }
}
