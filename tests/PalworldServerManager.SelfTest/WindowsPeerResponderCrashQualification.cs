using System.Security.Principal;
using System.Text.Json;
using Grpc.Core;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.WindowsPeerProcessFixture;

namespace PalworldServerManager.SelfTest;

internal static class WindowsPeerResponderCrashQualification
{
    private static (long Incarnation, string Pin, string Bound) Evidence(FixtureHost host)
    {
        using var c = host.Database.OpenConnection(); using var q = c.CreateCommand();
        q.CommandText = "SELECT e.Incarnation,e.LocalFingerprint,e.BoundUtc,i.Incarnation FROM PeerLocalBindingEvidence e " +
            "JOIN PeerRelationshipIncarnations i ON i.PeerHostId=e.PeerHostId WHERE e.PeerHostId=$peer;";
        q.Parameters.AddWithValue("$peer", host.Config.Peer.ToString("D")); using var r = q.ExecuteReader();
        Check(r.Read() && r.GetInt64(0) > 0 && r.GetInt64(0) == r.GetInt64(3), "Missing current native binding evidence.");
        var result = (r.GetInt64(0), r.GetString(1), r.GetString(2)); Check(!r.Read(), "Duplicate binding evidence."); return result;
    }
    internal static async Task Run(string serviceRoot, SecurityIdentifier sid, string nativePath, string nativeHash, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var ct = timeout.Token; var nonce = Guid.NewGuid(); var aId = Guid.NewGuid(); var bId = Guid.NewGuid();
        var parent = Path.GetFullPath(serviceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(parent, "peer-process-" + nonce.ToString("N")));
        Check(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(root), "Unsafe responder fixture root.");
        var a = new FixtureHost(new(nonce, aId, bId, Guid.NewGuid(), Path.Combine(root, "host-" + aId.ToString("N")), sid.Value));
        var b = new FixtureHost(new(nonce, bId, aId, Guid.NewGuid(), Path.Combine(root, "host-" + bId.ToString("N")), sid.Value,
            nativePath, nativeHash, PauseResponderBeforePeerBound: true));
        RequirePath(a.Config); RequirePath(b.Config); using var aLease = Lease(a.Config);
        using var provider = new WindowsSpake2Provider(nativePath, nativeHash);
        HostGenerationTransitions? sender = null; Child? receiver = null; bool initialized = false;
        Invitation? invitation = null; Task<PeerPairingCompletion>? pending = null;
        try
        {
            var senderPin = await a.Initialize(ct); string receiverPin;
            using (var lease = Lease(b.Config))
            {
                receiverPin = await b.Initialize(ct); initialized = true;
                WindowsPeerPrecommitCrashQualification.Unbound(a, receiverPin);
                WindowsPeerPrecommitCrashQualification.Unbound(b, senderPin);
                File.WriteAllText(Path.Combine(b.Config.Root, ConfigName), JsonSerializer.Serialize(b.Config));
            }
            sender = a.Create(provider); await sender.StartAsync(ct);
            receiver = new Child(b.Config); var first = await receiver.Read(ct);
            Check(first.Kind == "ready" && first.Pin == receiverPin && !string.IsNullOrEmpty(first.Key) && first.PeerState is null,
                "Initial responder has unexpected identity or trust.");
            using (var denied = HostExclusivityLock.TryAcquire(TimeSpan.Zero, b.Config.Mutex)) Check(denied is null, "Responder lease not held.");
            invitation = await receiver.Invite(first.Address, ct); var oldInvitation = invitation.Metadata.Invitation;
            pending = sender.PairAsync(invitation.Metadata.PairingAddress!, invitation.Code, ct);
            var barrier = await receiver.Read(ct);
            Check(barrier.Kind == "verified-before-store" && barrier.VerifiedPeerPin == senderPin && barrier.PeerState is null &&
                barrier.CurrentPeerPin is null && barrier.BindingExpiry is null, "Responder did not stop before its Store.");
            var original = a.Peers.Read(bId)!; var evidence = Evidence(a);
            Check(original.State == "PeerBound" && original.CurrentFingerprint == receiverPin && original.LocalBoundFingerprint == senderPin &&
                !original.RecoveryRequired && original.ExpiresUtc is not null && evidence.Pin == senderPin,
                "Initiator did not independently persist actual native PeerBound.");
            a.RequireNoGrants(0); Check(!pending.IsCompleted, "Pairing finished before responder termination.");
            await receiver.Kill(requireRunning: true);
            try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Killed responder returned pairing success."); }
            catch (RpcException error) when (!ct.IsCancellationRequested && error.StatusCode is StatusCode.Internal or StatusCode.Unavailable) { }
            pending = null; invitation.Dispose(); invitation = null;
            Check(a.Peers.Read(bId) == original && Evidence(a) == evidence, "Failed response changed initiator binding history.");
            a.RequireNoGrants(0);
            using (var lease = Lease(b.Config))
            {
                WindowsPeerPrecommitCrashQualification.Unbound(b, senderPin);
                b = new FixtureHost(b.Config with { PauseResponderBeforePeerBound = false });
                File.WriteAllText(Path.Combine(b.Config.Root, ConfigName), JsonSerializer.Serialize(b.Config));
            }
            receiver.Dispose(); receiver = null;
            receiver = new Child(b.Config); var restarted = await receiver.Read(ct);
            Check(restarted.Kind == "ready" && restarted.Instance != first.Instance && restarted.Pin == first.Pin && restarted.Key == first.Key &&
                restarted.PeerState is null && restarted.CurrentPeerPin is null, "Restart invented responder trust or changed native identity.");
            var refused = await receiver.Command("refuse-activation", sender.Endpoints!.Value.Peer, ct);
            Check(refused.Kind == "activation-refused" && refused.PeerState is null, "Asymmetric state admitted activation.");
            Check(a.Peers.Read(bId) == original && Evidence(a) == evidence, "Refused activation changed original binding.");
            using (var fresh = await receiver.Invite(restarted.Address, ct))
            {
                Check(fresh.Metadata.Invitation != oldInvitation, "Fresh responder reused an invitation.");
                var result = await sender.PairAsync(fresh.Metadata.PairingAddress!, fresh.Code, ct);
                Check(result.Local.Disposition == PeerBindingDisposition.ResumePeerBound && result.Remote == PeerPairingResult.PeerBound &&
                    result.Local.ExpiresUtc == original.ExpiresUtc, "Fresh pairing did not resume/create the asymmetric bindings correctly.");
            }
            Check(a.Peers.Read(bId) == original && Evidence(a) == evidence, "Resumed native pairing rewrote original binding evidence.");
            a.RequireNoGrants(0);
            Check(await sender.ActivateAsync(bId, restarted.Address, ct) == PeerActivationDisposition.Activated, "Fresh responder did not activate.");
            Check(await sender.ActivateAsync(bId, restarted.Address, ct) == PeerActivationDisposition.AlreadyActive, "Activation retry was not idempotent.");
            a.RequireNoGrants(); Check(Evidence(a) == evidence && a.Peers.Read(bId)!.ExpiresUtc == original.ExpiresUtc,
                "Activation rewrote original initiator binding history.");
            await receiver.Stop(ct); receiver.Dispose(); receiver = null;
            using (var lease = Lease(b.Config))
            {
                b.RequireNoGrants(); Check(b.Peers.Read(aId)!.State == "Active", "Responder activation was not durable.");
                foreach (var host in new[] { a, b })
                {
                    using var c = host.Database.OpenConnection();
                    Check(HostDatabase.QueryScalarLong(c, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerBoundCreated';") == 1,
                        "Asymmetric repair created duplicate binding history.");
                }
            }
        }
        finally
        {
            try
            {
                try { if (receiver is not null) { await receiver.Kill(); receiver.Dispose(); } }
                finally
                {
                    // An earlier failure may leave the parent call borrowing its code/key.
                    // Observe its result after child exit, before code/generation cleanup.
                    if (pending is not null)
                        try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
                        catch (Exception error) when (pending.IsCompleted && error is not OutOfMemoryException) { }
                }
            }
            finally
            {
                // If the bounded result wait failed, Stop still cancels/drains borrowed
                // work before the remaining invitation code is disposed.
                try { if (sender is not null) await sender.StopAsync(); }
                finally { invitation?.Dispose(); }
            }
            if (initialized)
            {
                using var lease = Lease(b.Config); await b.Cleanup(); await a.Cleanup();
                Check(Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "Unsafe responder cleanup root.");
                Directory.Delete(root, false);
            }
        }
    }
}
