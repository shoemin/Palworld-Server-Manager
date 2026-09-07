using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Grpc.Core;
using Microsoft.AspNetCore.Connections;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using Fixture = PalworldServerManager.SelfTest.PeerSecurityRpcTests.Fixture;

namespace PalworldServerManager.SelfTest;

internal static class HostTrafficLifetimeTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Host traffic lifetime assertion failed."); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Bounded(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected traffic refusal: " + typeof(T).Name); }

    public static async Task CancellationWaitsForWholeOperationCleanup()
    {
        await using var lifetime = new HostTrafficLifetime();
        var entered = Signal(); var cleanup = Signal(); var release = Signal();
        var work = lifetime.RunAsync(async ct =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cleanup.SetResult(); await release.Task; }
        });
        try
        {
            await Bounded(entered.Task); var drain = lifetime.DrainAsync();
            Check(ReferenceEquals(drain, lifetime.DrainAsync()));
            await Bounded(cleanup.Task); Check(!drain.IsCompleted);
            var invoked = false;
            await Reject<InvalidOperationException>(() => lifetime.RunAsync(_ => { invoked = true; return Task.CompletedTask; }));
            Check(!invoked);
        }
        finally { release.TrySetResult(); await Reject<OperationCanceledException>(() => work); await Bounded(lifetime.DrainAsync()); }
    }

    public static async Task ConcurrentWorkAndTerminalAdmission()
    {
        await using var lifetime = new HostTrafficLifetime();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Reject<OperationCanceledException>(() => lifetime.RunAsync(_ => throw new Exception("Pre-canceled work ran."), canceled.Token));
        await Reject<IOException>(() => lifetime.RunAsync(_ => throw new IOException("Synchronous work failure.")));
        Check(await lifetime.RunAsync(_ => Task.FromResult(7)) == 7);
        var entered = Signal(); var release = Signal(); var canceledAll = Signal(); var count = 0; var canceledCount = 0;
        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() => lifetime.RunAsync(async ct =>
        {
            using var registration = ct.Register(() => { if (Interlocked.Increment(ref canceledCount) == 32) canceledAll.SetResult(); });
            if (Interlocked.Increment(ref count) == 32) entered.SetResult();
            await release.Task; Check(ct.IsCancellationRequested); return 1;
        }))).ToArray();
        try
        {
            await Bounded(entered.Task); var drains = Enumerable.Range(0, 16).Select(_ => lifetime.DrainAsync()).ToArray();
            await Bounded(canceledAll.Task);
            Check(drains.All(t => ReferenceEquals(t, drains[0])) && !drains[0].IsCompleted);
        }
        finally { release.TrySetResult(); await Bounded(Task.WhenAll(tasks)); await Bounded(lifetime.DrainAsync()); }
        await lifetime.DisposeAsync(); await lifetime.DisposeAsync();
        await Reject<InvalidOperationException>(() => lifetime.RunAsync(_ => Task.CompletedTask));
    }

    public static async Task CancellationCallbackFailureStillWaitsForWork()
    {
        var lifetime = new HostTrafficLifetime(); var entered = Signal(); var callback = Signal(); var release = Signal();
        var work = lifetime.RunAsync(async ct =>
        {
            using var registration = ct.Register(() => { callback.SetResult(); throw new IOException("Injected cancellation callback failure."); });
            entered.SetResult(); await release.Task;
        });
        try
        {
            await Bounded(entered.Task); var drain = lifetime.DrainAsync();
            await Bounded(callback.Task); Check(!drain.IsCompleted);
            await Reject<InvalidOperationException>(() => lifetime.RunAsync(_ => Task.CompletedTask));
        }
        finally
        {
            release.TrySetResult(); await Bounded(work);
            await Reject<AggregateException>(() => Bounded(lifetime.DrainAsync()));
            await Reject<AggregateException>(() => lifetime.DisposeAsync().AsTask());
        }
    }

    public static async Task ActualLocalConnectionsCloseAndNewTrafficIsRefused()
    {
        await using var fixture = new LocalSecurityRpcTests.Fixture();
        await using var lifetime = new HostTrafficLifetime();
        await fixture.Start(lifetime);
        using var existing = fixture.Connect(); await existing.Negotiate();
        Check(fixture.Delivered > 0); var delivered = fixture.Delivered;
        await Bounded(lifetime.DrainAsync());
        await Reject<RpcException>(() => existing.Negotiate());
        using var fresh = fixture.Connect(); await Reject<RpcException>(() => fresh.Negotiate());
        Check(fixture.Delivered == delivered);
    }

    public static async Task ActualPeerAndOutgoingPostReplyWorkAreOwned()
    {
        await using var a = new Fixture(); await using var b = new Fixture();
        await using var incoming = new HostTrafficLifetime(); await using var outgoing = new HostTrafficLifetime();
        a.Bind(b); b.Bind(a); await b.Start(traffic: incoming);
        var replied = Signal(); var release = Signal();
        var work = outgoing.RunAsync(async ct =>
        {
            var result = await WindowsHostComposition.CreatePeerActivationClient(a.Runtime, a.Certificate.Value).FinalizeAsync(b.State.HostId, b.Address, ct);
            Check(result == PeerActivationDisposition.Activated); replied.SetResult();
            // The actual exchange is already disposed. A caller's subsequent mutation still belongs to the operation.
            await release.Task;
            a.State.Execute("CREATE TABLE TrafficPostReplyProof (Value INTEGER); INSERT INTO TrafficPostReplyProof VALUES (1);");
        });
        try { await Bounded(replied.Task); Check(!outgoing.DrainAsync().IsCompleted); }
        finally { release.TrySetResult(); await Bounded(work); await Bounded(outgoing.DrainAsync()); }
        Check(a.State.Count("TrafficPostReplyProof") == 1);
        await Reject<InvalidOperationException>(() => outgoing.RunAsync(ct => WindowsHostComposition.CreatePeerActivationClient(a.Runtime, a.Certificate.Value)
            .FinalizeAsync(b.State.HostId, b.Address, ct)));
        using var connected = new PeerSecurityRpcTests.RawClient(a, b); await connected.Negotiate(PeerSecurityRpcRuntime.Hello(a.State.HostId));
        await Bounded(incoming.DrainAsync());
        using var fresh = new PeerSecurityRpcTests.RawClient(a, b);
        await Reject<RpcException>(() => fresh.Negotiate(PeerSecurityRpcRuntime.Hello(a.State.HostId)));
        Check(a.State.Count("ActivationRpcEffects") == 1 && b.State.Count("ActivationRpcEffects") == 1);
    }

    private sealed class UnusedFactory : IPairingKeyExchangeFactory
    {
        public IPairingKeyExchange Start(PairingRole role, byte[] code, byte[] nonce, CancellationToken ct = default)
            => throw new Exception("Partial TLS test must not reach PAKE.");
    }
    private sealed class BlockingHook : IPeerActivationHook, IDisposable
    {
        internal readonly TaskCompletionSource Entered = Signal();
        internal readonly ManualResetEventSlim Release = new();
        public void Apply(SqliteConnection c, SqliteTransaction tx, PeerActivationContext activation)
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Blocked mutation was not released.");
            HostDatabase.Execute(c, "INSERT INTO ActivationRpcEffects VALUES ('blocked-incoming');", tx);
        }
        public void Dispose() => Release.Dispose();
    }
    public static async Task ActualAbortedConnectionWaitsForItsDatabaseMutation()
    {
        using var hook = new BlockingHook();
        await using var a = new Fixture(); await using var b = new Fixture(hook);
        await using var lifetime = new HostTrafficLifetime();
        a.Bind(b); b.Bind(a); await b.Start(traffic: lifetime);
        var request = WindowsHostComposition.CreatePeerActivationClient(a.Runtime, a.Certificate.Value).FinalizeAsync(b.State.HostId, b.Address);
        try
        {
            await Bounded(hook.Entered.Task);
            var drain = lifetime.DrainAsync();
            // Wait for the actual caller to observe connection abort while the Host mutation remains blocked.
            await Reject<RpcException>(() => Bounded(request));
            Check(!drain.IsCompleted);
        }
        finally
        {
            hook.Release.Set();
            try { await request; } catch (RpcException) { }
            await Bounded(lifetime.DrainAsync());
        }
        Check(b.State.Count("ActivationRpcEffects") == 1);
    }
    public static async Task PartialHandshakesAcrossAllThreeListenersAreDrained()
    {
        await using var state = new Fixture(); await using var lifetime = new HostTrafficLifetime();
        using var identity = WindowsIdentity.GetCurrent();
        using var key = state.Certificate.Value.GetECDsaPublicKey()!;
        using var pairing = new PeerPairingRpcRuntime(state.State.Database, state.State.HostId, key.ExportSubjectPublicKeyInfo(), new UnusedFactory(), (_, _) => { });
        var local = new LocalSecurityRpcRuntime(state.State.Database, state.State.HostId, new LocalEnrollmentTests.Store(new byte[32]),
            _ => throw new Exception("Partial TLS test reached local RPC."), _ => throw new Exception("Unexpected authentication report."));
        var entered = Signal(); var count = 0;
        ConnectionDelegate BeforeTls(ConnectionDelegate next) => lifetime.BindConnection(async connection =>
        { if (Interlocked.Increment(ref count) == 3) entered.SetResult(); await next(connection); });
        var pipe = "PSMTraffic" + Guid.NewGuid().ToString("N");
        await using var localApp = WindowsHostComposition.BuildLocalApplication(local, identity.User!, identity.User!, state.Certificate.Value, pipe, BeforeTls);
        await using var peerApp = WindowsHostComposition.BuildPeerApplication(state.Runtime, state.Certificate.Value, new(IPAddress.Loopback, 0), BeforeTls);
        await using var pairingApp = WindowsHostComposition.BuildPairingApplication(pairing, state.Certificate.Value, new(IPAddress.Loopback, 0), BeforeTls);
        try
        {
            await localApp.StartAsync(); await peerApp.StartAsync(); await pairingApp.StartAsync();
            using var localPipe = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            using var peerTcp = new TcpClient(); using var pairingTcp = new TcpClient();
            await localPipe.ConnectAsync(5000);
            await peerTcp.ConnectAsync(IPAddress.Loopback, new Uri(peerApp.Urls.Single()).Port);
            await pairingTcp.ConnectAsync(IPAddress.Loopback, new Uri(pairingApp.Urls.Single()).Port);
            await Bounded(entered.Task); await Bounded(lifetime.DrainAsync());
            foreach (var stream in new Stream[] { localPipe, peerTcp.GetStream(), pairingTcp.GetStream() })
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { Check(await stream.ReadAsync(new byte[1], deadline.Token) == 0); }
                catch (IOException) { } // actual connection abort may reset rather than send EOF
            }
            Check(state.State.Count("TrustedManagers") == 0);
        }
        finally
        {
            await localApp.StopAsync(); await peerApp.StopAsync(); await pairingApp.StopAsync();
            await Bounded(lifetime.DrainAsync());
        }
    }
}
