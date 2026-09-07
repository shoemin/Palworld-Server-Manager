using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Contracts;
using PalworldServerManager.Host;
using PalworldServerManager.Platform.Contracts;
using Fixture = PalworldServerManager.SelfTest.PeerSecurityRpcTests.Fixture;

namespace PalworldServerManager.SelfTest;

internal static class HostGenerationDiscoveryTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Generation discovery assertion failed."); }
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Bounded(Task task) => task.WaitAsync(TimeSpan.FromSeconds(15));
    private static async Task<Exception> Failure(Func<Task> work)
    { try { await Bounded(work()); } catch (Exception ex) when (ex is not TimeoutException) { return ex; } throw new Exception("Required discovery generation failure missing."); }
    private static string Pipe() => "PSMDiscovery" + Guid.NewGuid().ToString("N");
    private sealed class RefusingFactory : IPairingKeyExchangeFactory
    {
        public IPairingKeyExchange Start(PairingRole role, byte[] code, byte[] nonce, CancellationToken ct = default)
            => throw new CryptographicException("Fixture cannot produce PAKE proof.");
    }
    internal sealed class Receiver(LanDiscoveryReceived callback) : ILanDiscoveryReceiver
    {
        internal readonly TaskCompletionSource End = Signal(), Disposing = Signal();
        internal Task Release = Task.CompletedTask;
        internal Exception? CleanupFailure;
        internal int Disposes;
        public int Port => 45678;
        public Task Completion => End.Task;
        internal ValueTask Emit(byte[] bytes) => callback(7, IPAddress.Parse("192.0.2.15"), bytes, CancellationToken.None);
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposes); Disposing.TrySetResult(); await Release;
            if (CleanupFailure is not null) throw CleanupFailure;
            End.TrySetResult(); await End.Task;
        }
    }
    internal sealed class Probe : ILanDiscoveryBroadcaster
    {
        internal HostDiscoveryRuntime Runtime = null!;
        internal Receiver Receiver = null!;
        internal UnverifiedHostAdvertisement Advertisement = null!;
        internal readonly TaskCompletionSource Sent = Signal(), ReleaseSend = Signal();
        internal bool BlockSend;
        internal async Task<HostDiscoveryRuntime> CreateAsync(UnverifiedHostAdvertisement ad, CancellationToken ct)
        {
            Advertisement = ad;
            return Runtime = await HostDiscoveryRuntime.StartAsync(ad, callback => Receiver = new(callback), this, ct);
        }
        public async ValueTask<int> BroadcastAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            Check(HostDiscoveryCodec.Decode(bytes.Span) == Advertisement); Sent.TrySetResult();
            if (BlockSend) await ReleaseSend.Task;
            ct.ThrowIfCancellationRequested(); return 1;
        }
    }
    private sealed class UnavailableSender : ILanDiscoveryBroadcaster
    {
        public ValueTask<int> BroadcastAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
            => ValueTask.FromException<int>(new LanDiscoveryUnavailableException(new IOException("fixture native outage")));
    }
    private static Task<HostNetworkGeneration> Start(Fixture f, string pipe,
        Func<UnverifiedHostAdvertisement, CancellationToken, Task<HostDiscoveryRuntime>> discovery, CancellationToken ct = default)
    {
        f.State.Time.Now = DateTimeOffset.UtcNow; using var identity = WindowsIdentity.GetCurrent();
        return WindowsHostComposition.CreateNetworkGenerationAsync(f.State.Database, f.State.HostId, new LocalEnrollmentTests.Store(new byte[32]),
            identity.User!, identity.User!, f.Certificate.Value, pipe, new(IPAddress.Loopback, 0), new(IPAddress.Loopback, 0),
            new RefusingFactory(), f.Runtime.Hook, ct, discoveryFactory: discovery);
    }
    private static LocalSecurityRpcTests.Reader Reader(Guid host, string pin) => new(LocalHostTrustAnchor.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
        new { schemaVersion = 1, hostId = host, currentHostCredentialFingerprint = pin, pendingHostCredentialFingerprint = (string?)null, pendingRotationId = (Guid?)null })));
    private static async Task LocalWorks(Fixture f, string pipe, string pin)
    {
        using var local = new LocalSecurityRpcTests.Client(f.State.HostId, pipe, Reader(f.State.HostId, pin));
        Check((await local.Negotiate()).Host.HostId == f.State.HostId.ToString("D"));
    }
    private static void Closed(string pipe, UnverifiedHostAdvertisement ad)
    {
        using var pipeProbe = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        using var pairing = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        peer.Bind(new IPEndPoint(IPAddress.Loopback, ad.PeerPort)); pairing.Bind(new IPEndPoint(IPAddress.Loopback, ad.PairingPort));
    }
    private static void QuiescenceRefused(HostNetworkGeneration generation)
    {
        try { generation.QuiescedCutover(null!, null!); } catch (InvalidOperationException) { return; }
        throw new Exception("Incomplete discovery cleanup supplied cutover authority.");
    }

    public static async Task ActualBoundMetadataAndSealedConfiguration()
    {
        await using var f = new Fixture(); var pin = f.Pin; var pipe = Pipe(); var probe = new Probe();
        await using var generation = await Start(f, pipe, probe.CreateAsync);
        await Bounded(probe.Sent.Task); var ad = probe.Advertisement; var hello = PeerPairingRpcRuntime.Hello();
        Check(ad.ClaimedHostId == f.State.HostId && ad.ProtocolMajor == hello.Protocol.Major && ad.ProtocolMinor == hello.Protocol.Minor);
        Check(ad.PeerPort > 0 && ad.PairingPort > 0 && ad.PeerPort != ad.PairingPort
            && ad.PeerPort == generation.Endpoints!.Value.Peer.Port && ad.PairingPort == generation.Endpoints.Value.Pairing.Port);
        using (var peer = new TcpClient()) await peer.ConnectAsync(IPAddress.Loopback, ad.PeerPort);
        using (var pairing = new TcpClient()) await pairing.ConnectAsync(IPAddress.Loopback, ad.PairingPort);
        await LocalWorks(f, pipe, pin);
        await probe.Receiver.Emit(HostDiscoveryCodec.Encode(ad with { ClaimedHostId = Guid.NewGuid() }));
        var hints = await generation.DiscoverAsync(); Check(hints.Count == 1 && hints[0].PeerAddress.Host == "192.0.2.15");
        Check(f.State.Count("TrustedManagers") == 0 && f.State.Count("HostCapabilityGrants") == 0);
        Check(await Failure(() => { generation.ConfigureDiscovery(probe.CreateAsync); return Task.CompletedTask; }) is InvalidOperationException);
        Check(await Failure(() => { generation.ConfigurePeerEndpoints(() => (new("https://localhost:1"), new("https://localhost:2"))); return Task.CompletedTask; }) is InvalidOperationException);
        await Bounded(generation.StopAsync()); Check(probe.Runtime.Completion.IsCompletedSuccessfully && probe.Receiver.Disposes == 1);
        Check(await Failure(() => generation.DiscoverAsync()) is InvalidOperationException); Closed(pipe, ad);
    }

    public static async Task StopStartsDiscoveryBeforeTrafficDrain()
    {
        await using var f = new Fixture(); var pipe = Pipe(); var probe = new Probe { BlockSend = true };
        var generation = await Start(f, pipe, probe.CreateAsync); var releaseReceiver = Signal(); var releaseTraffic = Signal(); var entered = Signal();
        probe.Receiver.Release = releaseReceiver.Task;
        var work = generation.RunAsync(async _ => { entered.SetResult(); await probe.Receiver.Disposing.Task; await releaseTraffic.Task; });
        try
        {
            await Bounded(entered.Task); await Bounded(probe.Sent.Task);
            var stop = generation.StopAsync(); Check(ReferenceEquals(stop, generation.StopAsync()));
            await Bounded(probe.Receiver.Disposing.Task); Check(!stop.IsCompleted && f.Certificate.Value.Handle != IntPtr.Zero); QuiescenceRefused(generation);
            releaseReceiver.SetResult(); probe.ReleaseSend.SetResult(); await Bounded(probe.Runtime.Completion);
            Check(!stop.IsCompleted && f.Certificate.Value.Handle != IntPtr.Zero);
            releaseTraffic.SetResult(); await Bounded(work); await Bounded(stop);
            Check(f.Certificate.Value.Handle == IntPtr.Zero); Closed(pipe, probe.Advertisement);
        }
        finally { releaseReceiver.TrySetResult(); probe.ReleaseSend.TrySetResult(); releaseTraffic.TrySetResult(); await work; await generation.StopAsync(); }
    }

    public static async Task StartupCancellationOwnsReturnedDiscovery()
    {
        await using var f = new Fixture(); var pipe = Pipe(); var probe = new Probe { BlockSend = true }; using var cancel = new CancellationTokenSource();
        var startup = Start(f, pipe, async (ad, _) =>
        {
            var runtime = await probe.CreateAsync(ad, CancellationToken.None); await probe.Sent.Task;
            cancel.Cancel(); return runtime;
        }, cancel.Token);
        try
        {
            await Bounded(probe.Sent.Task); await Bounded(probe.Receiver.Disposing.Task);
            Check(!startup.IsCompleted && f.Certificate.Value.Handle != IntPtr.Zero);
            probe.ReleaseSend.SetResult(); Check(await Failure(() => startup) is OperationCanceledException);
            Check(probe.Runtime.Completion.IsCompletedSuccessfully && probe.Receiver.Disposes == 1 && f.Certificate.Value.Handle == IntPtr.Zero);
            Closed(pipe, probe.Advertisement);
        }
        finally { probe.ReleaseSend.TrySetResult(); await Failure(() => startup); }
    }

    public static async Task StartupFailureAndUnavailableManualService()
    {
        await using (var f = new Fixture())
        {
            var pipe = Pipe(); UnverifiedHostAdvertisement? metadata = null; var denied = new IOException("discovery startup denied");
            Check(ReferenceEquals(await Failure(() => Start(f, pipe, (ad, _) => { metadata = ad; throw denied; })), denied));
            Check(f.Certificate.Value.Handle == IntPtr.Zero); Closed(pipe, metadata!);
        }
        await using (var f = new Fixture())
        {
            var pipe = Pipe(); var pin = f.Pin; HostDiscoveryRuntime? runtime = null; var attempts = 0;
            await using var generation = await Start(f, pipe, async (ad, token) => runtime = await HostDiscoveryRuntime.StartAsync(ad,
                _ => { Interlocked.Increment(ref attempts); throw new LanDiscoveryUnavailableException(new IOException("fixture native outage")); }, new UnavailableSender(), token));
            Check(attempts >= 1 && !runtime!.Completion.IsCompleted && (await generation.DiscoverAsync()).Count == 0);
            await LocalWorks(f, pipe, pin); using var invitation = await generation.CreateInvitationAsync(); await generation.CancelInvitationAsync(invitation.Id);
            Check(!generation.ListenerStopped.IsCompleted && f.State.Count("HostCapabilityGrants") == 0);
            await Bounded(generation.StopAsync()); Check(runtime!.Completion.IsCompletedSuccessfully);
        }
        await using (var f = new Fixture())
        {
            await using var localOnly = new HostNetworkGeneration(f.Certificate.Value);
            Check(await Failure(() => { localOnly.ConfigureDiscovery(new Probe().CreateAsync); return Task.CompletedTask; }) is InvalidOperationException);
            Check(await Failure(() => { localOnly.ConfigurePeerEndpoints(() => (new("https://localhost:1"), new("https://localhost:2"))); return Task.CompletedTask; }) is InvalidOperationException);
        }
    }

    public static async Task FatalCompletionAndCleanupRefuseCutover()
    {
        foreach (var mode in new[] { "receive", "unexpected-stop", "cleanup" })
        {
            await using var f = new Fixture(); var pipe = Pipe(); var probe = new Probe(); var generation = await Start(f, pipe, probe.CreateAsync);
            var cause = new IOException("discovery " + mode);
            if (mode == "receive") probe.Receiver.End.SetException(cause);
            else if (mode == "unexpected-stop") await probe.Runtime.DisposeAsync();
            else { probe.Receiver.CleanupFailure = cause; _ = generation.StopAsync(); }
            await Bounded(generation.ListenerStopped);
            var failure = await Failure(generation.StopAsync); Check(failure is AggregateException);
            if (mode != "unexpected-stop") Check(((AggregateException)failure).Flatten().InnerExceptions.Contains(cause));
            Check(f.Certificate.Value.Handle == IntPtr.Zero && probe.Receiver.Disposes == 1); QuiescenceRefused(generation);
            Check(await Failure(() => generation.CreateInvitationAsync()) is InvalidOperationException);
            Check(await Failure(() => generation.DiscoverAsync()) is InvalidOperationException); Closed(pipe, probe.Advertisement);
        }
    }
}
