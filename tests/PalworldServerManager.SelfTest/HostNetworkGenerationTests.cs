using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Grpc.Core;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using Fixture = PalworldServerManager.SelfTest.PeerSecurityRpcTests.Fixture;

namespace PalworldServerManager.SelfTest;

internal static class HostNetworkGenerationTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Host generation assertion failed."); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Bounded(Task task) => task.WaitAsync(TimeSpan.FromSeconds(15));
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected generation refusal: " + typeof(T).Name); }
    private sealed class RefusingFactory : IPairingKeyExchangeFactory
    {
        public IPairingKeyExchange Start(PairingRole role, byte[] code, byte[] nonce, CancellationToken ct = default)
            => throw new CryptographicException("Fixture cannot produce PAKE proof.");
    }
    private sealed class Clock : TimeProvider
    {
        private int active, created;
        internal int FailOnTimer;
        private readonly List<ITimer> timers = [];
        internal int Active => Volatile.Read(ref active);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
        {
            if (Interlocked.Increment(ref created) == FailOnTimer) throw new InvalidOperationException("Injected timer creation failure.");
            var timer = new Tracked(this, base.CreateTimer(callback, state, due, period));
            Interlocked.Increment(ref active); timers.Add(timer); return timer;
        }
        internal async Task Cleanup() { foreach (var timer in timers) await timer.DisposeAsync(); }
        private sealed class Tracked(Clock owner, ITimer inner) : ITimer
        {
            private int disposed;
            public bool Change(TimeSpan due, TimeSpan period) => inner.Change(due, period);
            public void Dispose() { inner.Dispose(); if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Decrement(ref owner.active); }
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Decrement(ref owner.active); }
        }
    }
    private static string Pipe() => "PSMGeneration" + Guid.NewGuid().ToString("N");
    private static Task<HostNetworkGeneration> Start(Fixture f, string pipe, Clock clock, X509Certificate2? certificate = null,
        IPEndPoint? peerEndpoint = null, CancellationToken ct = default)
    {
        f.State.Time.Now = DateTimeOffset.UtcNow;
        using var identity = WindowsIdentity.GetCurrent();
        return WindowsHostComposition.CreateNetworkGenerationAsync(f.State.Database, f.State.HostId, new LocalEnrollmentTests.Store(new byte[32]),
            identity.User!, identity.User!, certificate ?? f.Certificate.Value, pipe, peerEndpoint ?? new(IPAddress.Loopback, 0), new(IPAddress.Loopback, 0),
            new RefusingFactory(), f.Runtime.Hook, ct, clock);
    }
    private static LocalSecurityRpcTests.Reader Reader(Guid host, string pin) => new(LocalHostTrustAnchor.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
        new { schemaVersion = 1, hostId = host, currentHostCredentialFingerprint = pin, pendingHostCredentialFingerprint = (string?)null, pendingRotationId = (Guid?)null })));
    private sealed class Material(Guid host, string reference, string pin, X509Certificate2 certificate) : IHostRotationMaterial
    {
        public Task<string> EnsurePreparedAsync(Guid id, string reserved, string? expected, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Check(id == host && reserved == reference && expected == pin && certificate.HasPrivateKey); return Task.FromResult(pin); }
    }
    private sealed class Publisher : ILocalHostTrustPublisher
    {
        internal readonly List<LocalHostTrustPublication> Calls = [];
        public Task PublishAsync(LocalHostTrustPublication value, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Calls.Add(value); return Task.CompletedTask; }
    }
    private static void QuiescenceRefused(HostNetworkGeneration generation)
    {
        try { generation.QuiescedCutover(null!, null!); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Incomplete generation shutdown supplied a cutover coordinator.");
    }

    public static async Task ActualNetworkWorkAndClosedAdmission()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); var ca = new Clock(); var cb = new Clock();
        var pipeA = Pipe(); var pinA = a.Pin;
        await using var ga = await Start(a, pipeA, ca); await using var gb = await Start(b, Pipe(), cb);
        Check(ca.Active == 2 && cb.Active == 2); QuiescenceRefused(ga);
        using var local = new LocalSecurityRpcTests.Client(a.State.HostId, pipeA, Reader(a.State.HostId, pinA));
        Check((await local.Negotiate()).Host.HostId == a.State.HostId.ToString("D"));
        a.Bind(b); b.Bind(a);
        Check(await ga.ActivateAsync(b.State.HostId, gb.Endpoints!.Value.Peer) == PeerActivationDisposition.Activated);
        Check(await ga.ConfirmRotationAsync(b.State.HostId, gb.Endpoints.Value.Peer) == PeerRotationReceiptExchange.NoReceiptPending);
        using var invitation = await gb.CreateInvitationAsync();
        await Reject<RpcException>(() => ga.PairAsync(gb.Endpoints.Value.Pairing, invitation.Id, invitation.Code));
        await gb.CancelInvitationAsync(invitation.Id);
        var stops = Enumerable.Range(0, 8).Select(_ => ga.StopAsync()).ToArray();
        Check(stops.All(t => ReferenceEquals(t, stops[0]))); await Bounded(stops[0]);
        Check(ca.Active == 0 && a.Certificate.Value.Handle == IntPtr.Zero && ga.ListenerStopped.IsCompleted);
        using var fresh = new LocalSecurityRpcTests.Client(a.State.HostId, pipeA, Reader(a.State.HostId, pinA));
        await Reject<RpcException>(() => fresh.Negotiate());
        var address = gb.Endpoints.Value.Peer;
        await Reject<InvalidOperationException>(() => ga.ActivateAsync(b.State.HostId, address));
        await Reject<InvalidOperationException>(() => ga.PairAsync(address, Guid.NewGuid(), invitation.Code));
        await Reject<InvalidOperationException>(() => ga.CreateInvitationAsync());
        await Reject<InvalidOperationException>(() => ga.CancelInvitationAsync(Guid.NewGuid()));
        await Reject<InvalidOperationException>(() => ga.CheckRotationAsync(b.State.HostId, address));
        await Reject<InvalidOperationException>(() => ga.StageRotationAsync(b.State.HostId, address, Guid.NewGuid()));
        await Reject<InvalidOperationException>(() => ga.ConfirmRotationAsync(b.State.HostId, address));
        await Reject<InvalidOperationException>(() => ga.CollectRotationAsync(Guid.NewGuid(), new Dictionary<Guid, Uri>()));
        Check(a.State.Count("HostCapabilityGrants") == 0 && b.State.Count("ServerCapabilityGrants") == 0);
    }

    public static async Task StopWaitsForWorkAndRejectsPrematureCutover()
    {
        await using var f = new Fixture(); var clock = new Clock(); await using var generation = await Start(f, Pipe(), clock);
        var entered = Signal(); var canceled = Signal(); var release = Signal();
        var work = generation.RunAsync(async ct =>
        {
            using var registration = ct.Register(() => canceled.SetResult()); entered.SetResult();
            await release.Task; f.State.Execute("CREATE TABLE GenerationFinished (Value INTEGER); INSERT INTO GenerationFinished VALUES (1);");
        });
        try
        {
            await Bounded(entered.Task); var stop = generation.StopAsync(); await Bounded(canceled.Task);
            Check(!stop.IsCompleted && f.Certificate.Value.Handle != IntPtr.Zero); QuiescenceRefused(generation);
        }
        finally { release.TrySetResult(); await Bounded(work); await Bounded(generation.StopAsync()); }
        Check(clock.Active == 0 && f.Certificate.Value.Handle == IntPtr.Zero && f.State.Count("GenerationFinished") == 1);
    }

    public static async Task PartialStartupAndCancellationReleaseOwnedResources()
    {
        await using (var f = new Fixture())
        {
            var clock = new Clock(); var pipe = Pipe(); using var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Start();
            await Reject<IOException>(() => Start(f, pipe, clock, peerEndpoint: (IPEndPoint)occupied.LocalEndpoint));
            Check(clock.Active == 0 && f.Certificate.Value.Handle == IntPtr.Zero);
            using var probe = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Reject<TimeoutException>(() => probe.ConnectAsync(250));
        }
        await using (var f = new Fixture())
        {
            var clock = new Clock(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Reject<OperationCanceledException>(() => Start(f, Pipe(), clock, ct: canceled.Token));
            Check(clock.Active == 0 && f.Certificate.Value.Handle == IntPtr.Zero);
        }
        await using (var f = new Fixture())
        {
            var clock = new Clock(); using var wrong = new PeerTlsTests.Certificate();
            await Reject<AuthenticationException>(() => Start(f, Pipe(), clock, wrong.Value));
            Check(clock.Active == 0 && wrong.Value.Handle == IntPtr.Zero);
        }
    }

    public static async Task AuditCleanupFailureCannotAuthorizeCutover()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); var clock = new Clock();
        var ga = await Start(a, Pipe(), clock); await using var gb = await Start(b, Pipe(), new Clock());
        try
        {
            a.State.Execute("CREATE TRIGGER generation_audit_failure BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='PairingAttemptFailed' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
            using var invitation = await gb.CreateInvitationAsync();
            await Reject<RpcException>(() => ga.PairAsync(gb.Endpoints!.Value.Pairing, invitation.Id, invitation.Code));
            await Reject<AggregateException>(() => Bounded(ga.StopAsync()));
            Check(clock.Active == 0 && a.Certificate.Value.Handle == IntPtr.Zero); QuiescenceRefused(ga);
            await Reject<InvalidOperationException>(() => ga.CreateInvitationAsync());
        }
        finally { await Reject<AggregateException>(() => ga.DisposeAsync().AsTask()); }
    }

    public static async Task RuntimeConstructionFailureCleansEarlierTimer()
    {
        await using var f = new Fixture(); var clock = new Clock { FailOnTimer = 2 };
        try
        {
            await Reject<InvalidOperationException>(() => Start(f, Pipe(), clock));
            if (clock.Active != 0) throw new Exception("Audit timer remained after failed runtime construction: " + clock.Active);
            Check(f.Certificate.Value.Handle == IntPtr.Zero);
        }
        finally { await clock.Cleanup(); } // test-only cleanup must not hide a failed production assertion
    }

    public static async Task StopDuringActualStartupWaitsBeforeCredentialDisposal()
    {
        await using var f = new Fixture(); using var identity = WindowsIdentity.GetCurrent();
        await using var generation = new HostNetworkGeneration(f.Certificate.Value);
        var runtime = new LocalSecurityRpcRuntime(f.State.Database, f.State.HostId, new LocalEnrollmentTests.Store(new byte[32]),
            WindowsLocalTlsEndpoint.ReadNativePrincipal, _ => { });
        var app = WindowsHostComposition.BuildLocalApplication(runtime, identity.User!, identity.User!, f.Certificate.Value, Pipe(), generation.BindConnection);
        generation.AddListener(app); var entered = Signal(); using var release = new ManualResetEventSlim();
        using var registration = app.Lifetime.ApplicationStarted.Register(() =>
        { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Startup barrier was not released."); });
        var start = Task.Run(() => generation.StartAsync(CancellationToken.None));
        try
        {
            await Bounded(entered.Task); var stop = generation.StopAsync();
            Check(!stop.IsCompleted && f.Certificate.Value.Handle != IntPtr.Zero); QuiescenceRefused(generation);
        }
        finally
        {
            release.Set(); await Reject<OperationCanceledException>(() => start); await Bounded(generation.StopAsync());
        }
        Check(f.Certificate.Value.Handle == IntPtr.Zero);
    }

    public static async Task DrainCallbackFailureStillCleansEveryOwnedResource()
    {
        await using var f = new Fixture(); var clock = new Clock(); var generation = await Start(f, Pipe(), clock);
        var entered = Signal(); var callback = Signal(); var release = Signal();
        var work = generation.RunAsync(async ct =>
        {
            using var registration = ct.Register(() => { callback.TrySetResult(); throw new IOException("Injected drain callback failure."); });
            entered.SetResult(); await release.Task;
        });
        try
        {
            await Bounded(entered.Task); var stop = generation.StopAsync(); await Bounded(callback.Task);
            Check(!stop.IsCompleted && f.Certificate.Value.Handle != IntPtr.Zero); QuiescenceRefused(generation);
        }
        finally
        {
            release.TrySetResult(); await Bounded(work); await Reject<AggregateException>(() => Bounded(generation.StopAsync()));
            await Reject<AggregateException>(() => generation.DisposeAsync().AsTask());
        }
        Check(clock.Active == 0 && f.Certificate.Value.Handle == IntPtr.Zero && generation.ListenerStopped.IsCompleted);
        QuiescenceRefused(generation);
    }

    public static async Task OwnedStopThenActualCutoverAndNewGenerationReceipts()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); using var next = new PeerTlsTests.Certificate();
        var clock = new Clock(); var pipe = Pipe(); var oldPin = a.Pin; var nextPin = WindowsPeerTls.PublicFingerprint(next.Value);
        await using var ga = await Start(a, pipe, clock); await using var gb = await Start(b, Pipe(), new Clock());
        a.Bind(b); b.Bind(a); await ga.ActivateAsync(b.State.HostId, gb.Endpoints!.Value.Peer);
        var owner = new LocalPrincipalMutationActor(a.State.HostId, a.State.OwnerId, "native-owner", "fixture-public");
        var state = a.Runtime.Credentials; var prepared = state.PrepareRoutineRotation(owner, Guid.NewGuid());
        state.RecordRoutineRotationMaterial(owner, prepared.RotationId, nextPin); state.BeginRoutineRotationStaging(owner, prepared.RotationId);
        state.PrepareRoutineRotationProposal(owner, prepared.RotationId);
        Check((await ga.StageRotationAsync(b.State.HostId, gb.Endpoints.Value.Peer, prepared.RotationId)).PeerHostId == b.State.HostId);
        Check(await gb.CheckRotationAsync(a.State.HostId, ga.Endpoints!.Value.Peer) == PeerRotationStatusExchange.Unchanged);
        var round = await ga.CollectRotationAsync(prepared.RotationId, new Dictionary<Guid, Uri> { [b.State.HostId] = gb.Endpoints.Value.Peer });
        QuiescenceRefused(ga); await Bounded(ga.StopAsync());
        var publisher = new Publisher(); var material = new Material(a.State.HostId, prepared.NewReference, nextPin, next.Value);
        var coordinator = ga.QuiescedCutover(material, publisher);
        Check(ReferenceEquals(coordinator, ga.QuiescedCutover(material, publisher)));
        await coordinator.CutOverWhileQuiescedAsync(owner, round);
        Check(publisher.Calls.Count == 2 && publisher.Calls[0].CurrentHostCredentialFingerprint == oldPin &&
            publisher.Calls[0].PendingHostCredentialFingerprint == nextPin && publisher.Calls[1].CurrentHostCredentialFingerprint == nextPin);
        await using var replacement = await Start(a, pipe, new Clock(), next.Value);
        using var local = new LocalSecurityRpcTests.Client(a.State.HostId, pipe, Reader(a.State.HostId, nextPin));
        Check((await local.Negotiate()).Host.HostId == a.State.HostId.ToString("D"));
        Check(await gb.ConfirmRotationAsync(a.State.HostId, replacement.Endpoints!.Value.Peer) == PeerRotationReceiptExchange.Confirmed);
        Check(b.State.Repository.Read(a.State.HostId)!.CurrentFingerprint == nextPin && b.State.Repository.Read(a.State.HostId)!.PendingRotationId is null);
        Check(state.Read().CurrentReference == prepared.NewReference && state.Read().Credentials.All(c => !c.Retired));
    }
}
