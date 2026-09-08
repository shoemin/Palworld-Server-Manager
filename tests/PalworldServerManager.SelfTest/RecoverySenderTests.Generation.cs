using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using Fixture = PalworldServerManager.SelfTest.PeerSecurityRpcTests.Fixture;

namespace PalworldServerManager.SelfTest;

internal static partial class RecoverySenderTests
{
    private sealed class NoGenerationPake : IPairingKeyExchangeFactory
    {
        public IPairingKeyExchange Start(PairingRole role, byte[] code, byte[] nonce, CancellationToken ct = default)
            => throw new CryptographicException("Generation sender fixture does not perform PAKE.");
    }
    private static HostNetworkGeneration RecoveryGeneration(Fixture local, X509Certificate2 certificate, IPeerHttpTransportFactory transport)
    {
        var generation = new HostNetworkGeneration(certificate);
        using var key = certificate.GetECDsaPublicKey()!;
        var runtime = new PeerSecurityRpcRuntime(local.State.Database, local.State.HostId, local.Runtime.Hook, local.State.Time);
        var pairing = new PeerPairingRpcRuntime(local.State.Database, local.State.HostId, key.ExportSubjectPublicKeyInfo(),
            new NoGenerationPake(), (_, _) => { }, local.State.Time);
        generation.SetPeerWork(runtime, pairing, transport);
        return generation;
    }
    public static async Task GenerationComposedSenderAndClosedAdmission()
    {
        await using var f = new Pair(); await f.B.Start();
        using var identity = WindowsIdentity.GetCurrent();
        await using var generation = await WindowsHostComposition.CreateNetworkGenerationAsync(f.A.State.Database, f.A.State.HostId,
            new LocalEnrollmentTests.Store(new byte[32]), identity.User!, identity.User!, f.A.Certificate.Value,
            "PSMRecoveryGeneration" + Guid.NewGuid().ToString("N"), new(IPAddress.Loopback, 0), new(IPAddress.Loopback, 0),
            new NoGenerationPake(), f.A.Runtime.Hook, default, f.A.State.Time);
        Check(await generation.ConfirmRecoveryAsync(f.B.State.HostId, f.B.Address) == PeerRecoveryCompletionExchange.Confirmed);
        Check(await generation.ConfirmRecoveryAsync(f.B.State.HostId, f.B.Address) == PeerRecoveryCompletionExchange.NoPending);
        Check(Confirmed(f.A) && CountEvent(f.A, "PeerRecoveryCompletionConfirmed") == 1 && CountEvent(f.B, "PeerRecoveryCompletionReceived") == 1);
        await generation.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(f.A.Certificate.Value.Handle == IntPtr.Zero);
        await Failed(Task.Run(() => generation.ConfirmRecoveryAsync(f.B.State.HostId, f.B.Address)));
    }
    public static async Task GenerationStopDrainsActualRecoveryCallback()
    {
        await using var f = new Pair(); await f.B.Start();
        var tracked = new TrackingFactory(f.A.Certificate.Value); var entered = Signal(); using var release = new ManualResetEventSlim();
        tracked.Observe = _ => { entered.TrySetResult(); release.Wait(); };
        await using var generation = RecoveryGeneration(f.A, f.A.Certificate.Value, tracked);
        await generation.StartAsync(default);
        var call = Task.Run(() => generation.ConfirmRecoveryAsync(f.B.State.HostId, f.B.Address));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = generation.StopAsync(); await Task.Delay(100);
            Check(!stop.IsCompleted && !call.IsCompleted && tracked.Disposed == 0 && f.A.Certificate.Value.Handle != IntPtr.Zero);
            await Failed(Task.Run(() => generation.ConfirmRecoveryAsync(f.B.State.HostId, f.B.Address)));
            release.Set(); await Failed(call); await stop.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.Set(); try { await call; } catch { } }
        Check(tracked.Observed == 1 && tracked.Disposed == 1 && tracked.ReceiptAttempts == 0 && !Confirmed(f.A));
        Check(f.A.Certificate.Value.Handle == IntPtr.Zero && CountEvent(f.B, "PeerRecoveryCompletionReceived") == 0);
    }
    public static async Task GenerationInterruptedReplyReopensExactApproval()
    {
        await using var f = new Pair(); await f.B.Start();
        // Duplicate the same real certificate before the generation releases its handle.
        using var reopened = new X509Certificate2(f.A.Certificate.Value);
        var tracked = new TrackingFactory(f.A.Certificate.Value); var entered = Signal();
        tracked.After = async (request, response, ct) =>
        { if (IsReceipt(request)) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return response; };
        await using (var generation = RecoveryGeneration(f.A, f.A.Certificate.Value, tracked))
        {
            await generation.StartAsync(default);
            var call = generation.ConfirmRecoveryAsync(f.B.State.HostId, f.B.Address);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await generation.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); await Failed(call);
            Check(!Confirmed(f.A) && tracked.Disposed == 1 && CountEvent(f.B, "PeerRecoveryCompletionReceived") == 1);
            Check(f.A.Certificate.Value.Handle == IntPtr.Zero);
        }
        var fresh = new TrackingFactory(reopened);
        await using (var generation = RecoveryGeneration(f.A, reopened, fresh))
        {
            await generation.StartAsync(default);
            Check(await generation.ConfirmRecoveryAsync(f.B.State.HostId, f.B.Address) == PeerRecoveryCompletionExchange.Confirmed);
            Check(Confirmed(f.A) && CountEvent(f.A, "PeerRecoveryCompletionConfirmed") == 1 && CountEvent(f.B, "PeerRecoveryCompletionReceived") == 1);
            Check(f.A.State.Count("PeerReplacementCompletions") == 1 && fresh.Disposed == 1);
            Check(PalworldServerManager.Host.Persistence.HostDatabase.QueryScalarLong(f.A.State.Writer,
                $"SELECT COUNT(*) FROM PeerReplacementCompletions WHERE ReplacementId='{f.AApproval:D}' AND ConfirmedUtc IS NOT NULL;") == 1);
        }
        Check(reopened.Handle == IntPtr.Zero);
    }
    public static async Task GenerationRecoveryRequiresConfiguredServingLifetime()
    {
        await using var f = new Pair();
        var tracked = new TrackingFactory(f.A.Certificate.Value);
        await using var configured = RecoveryGeneration(f.A, f.A.Certificate.Value, tracked);
        var address = new Uri("https://127.0.0.1:1/");
        await Failed(Task.Run(() => configured.ConfirmRecoveryAsync(f.B.State.HostId, address)));
        await configured.StopAsync();
        await Failed(Task.Run(() => configured.ConfirmRecoveryAsync(f.B.State.HostId, address)));
        Check(tracked.Observed == 0 && tracked.Disposed == 0 && !Confirmed(f.A));
        using var certificate = new PeerTlsTests.Certificate();
        await using var empty = new HostNetworkGeneration(certificate.Value); await empty.StartAsync(default);
        Check(await Failed(empty.ConfirmRecoveryAsync(f.B.State.HostId, address)) is InvalidOperationException);
        await empty.StopAsync(); Check(certificate.Value.Handle == IntPtr.Zero);
    }
}
