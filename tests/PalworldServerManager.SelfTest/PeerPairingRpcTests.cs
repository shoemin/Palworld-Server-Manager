using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class PeerPairingRpcTests
{
    private static void Check(bool value) { if (!value) throw new Exception("First-contact pairing assertion failed."); }
    private static async Task Refused(Func<Task> action, StatusCode status)
    { try { await action(); } catch (RpcException ex) when (ex.StatusCode == status) { return; } throw new Exception("Expected pairing refusal: " + status); }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly PeerTrustTests.Fixture State = new();
        internal readonly PeerTlsTests.Certificate Certificate = new();
        internal readonly PeerPairingRpcRuntime Runtime;
        internal readonly List<string> Events = [];
        private WebApplication? app;
        internal string Pin => WindowsPeerTls.PublicFingerprint(Certificate.Value);
        internal Uri Address => new(app!.Urls.Single());
        internal byte[] Public { get { using var key = Certificate.Value.GetECDsaPublicKey()!; return key.ExportSubjectPublicKeyInfo(); } }
        internal Fixture(IPairingKeyExchangeFactory factory)
        {
            State.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{Pin}' WHERE CredentialRef='current';");
            Runtime = new(State.Database, State.HostId, Public, factory, (_, outcome) => { lock (Events) Events.Add(outcome); }, State.Time);
        }
        internal async Task Start()
        {
            app = WindowsHostComposition.BuildPairingApplication(Runtime, Certificate.Value, new(IPAddress.Loopback, 0));
            Check(app.Configuration["urls"] is null && app.Environment.EnvironmentName == "Production"); await app.StartAsync();
        }
        public async ValueTask DisposeAsync()
        {
            try { if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); } }
            finally { try { Runtime.Dispose(); } finally { try { Certificate.Dispose(); } finally { State.Dispose(); } } }
        }
    }
    // Lifecycle-only fixture. It cannot verify a code or produce a cryptographic identity.
    private sealed class TrackingFactory : IPairingKeyExchangeFactory
    {
        internal int Created, Disposed;
        internal bool FailInitialization;
        public IPairingKeyExchange Start(PairingRole role, byte[] code, byte[] nonce, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Created); if (FailInitialization) throw new CryptographicException("Fixture initialization failed."); return new Exchange(this); }
        private sealed class Exchange(TrackingFactory owner) : IPairingKeyExchange
        {
            private int disposed;
            public PairingExchangeState State => PairingExchangeState.Created;
            public byte[] InitialMessage => new byte[65];
            public byte[] ReceivePeerMessage(byte[] message, CancellationToken cancellationToken = default) => throw new CryptographicException();
            public byte[] ConfirmPeer(byte[] confirmation, CancellationToken cancellationToken = default) => throw new CryptographicException();
            public byte[] CreateIdentityBinding(Guid hostId, byte[] credential, CancellationToken cancellationToken = default) => throw new CryptographicException();
            public VerifiedPairingIdentity VerifyIdentityBinding(byte[] message, CancellationToken cancellationToken = default) => throw new CryptographicException();
            public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Increment(ref owner.Disposed); }
        }
    }
    private static GrpcChannel Channel(Fixture client, Fixture server, out IPeerHttpTransport transport, bool unbounded = false)
    {
        transport = new WindowsPeerHttpTransportFactory(client.Certificate.Value).Create(_ => true);
        return GrpcChannel.ForAddress(server.Address, new GrpcChannelOptions
        { HttpHandler = transport.Handler, HttpVersion = HttpVersion.Version20, HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            MaxReceiveMessageSize = 4096, MaxSendMessageSize = unbounded ? null : 4096 });
    }
    private static PeerPairingFrame Start(Guid invitation) => new() { Start = new() { Handshake = PeerPairingRpcRuntime.Hello(), InvitationId = invitation.ToString("D") } };
    private static async Task SendInvalid(Fixture a, Fixture b, PeerPairingFrame frame, StatusCode status, bool unbounded = false)
    {
        using var channel = Channel(a, b, out var transport, unbounded); using var owner = transport;
        using var call = new PeerPairingProtocol.PeerPairingProtocolClient(channel).Pair(deadline: DateTime.UtcNow.AddSeconds(5));
        await call.RequestStream.WriteAsync(frame); await call.RequestStream.CompleteAsync();
        await Refused(async () => { await call.ResponseStream.MoveNext(CancellationToken.None); }, status);
    }
    public static async Task AdmissionAndFrameOrder()
    {
        var factory = new TrackingFactory(); await using var a = new Fixture(factory); await using var b = new Fixture(factory); await b.Start();
        using var invitation = b.Runtime.CreateInvitation();
        await SendInvalid(a, b, new() { Share = ByteString.CopyFrom(new byte[65]) }, StatusCode.InvalidArgument);
        var wrongProtocol = Start(invitation.Id); wrongProtocol.Start.Handshake.Protocol.Major = 2;
        await SendInvalid(a, b, wrongProtocol, StatusCode.FailedPrecondition);
        var unknown = Start(invitation.Id); unknown.Start.Handshake.Capabilities.Clear(); unknown.Start.Handshake.Capabilities.Add((FeatureCapability)999);
        await SendInvalid(a, b, unknown, StatusCode.FailedPrecondition);
        var huge = Start(invitation.Id); huge.Start.Handshake.ProductVersion = new string('X', 8000);
        await SendInvalid(a, b, huge, StatusCode.ResourceExhausted, true);
        Check(factory.Created == 0 && a.State.Count("TrustedManagers") == 0 && b.State.Count("TrustedManagers") == 0);
        using var channel = Channel(a, b, out var transport); using var owner = transport;
        // The first-contact application does not map pinned activation at all.
        await Refused(async () => { await new PeerSecurityProtocol.PeerSecurityProtocolClient(channel).ActivateAsync(new(), deadline: DateTime.UtcNow.AddSeconds(5)); }, StatusCode.Unimplemented);
    }
    public static async Task DisconnectAndSingleConnection()
    {
        var factory = new TrackingFactory(); await using var a = new Fixture(factory); await using var b = new Fixture(factory); await b.Start();
        using var invitation = b.Runtime.CreateInvitation();
        using var channel = Channel(a, b, out var transport); using var owner = transport;
        var client = new PeerPairingProtocol.PeerPairingProtocolClient(channel);
        using (var call = client.Pair(deadline: DateTime.UtcNow.AddSeconds(5)))
        {
            await call.RequestStream.WriteAsync(Start(invitation.Id));
            await PeerPairingRpcService.Read(call.ResponseStream, PeerPairingFrame.FrameOneofCase.Challenge, CancellationToken.None);
            Check(factory.Created == 1);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (Volatile.Read(ref factory.Disposed) != 1) await Task.Delay(10, deadline.Token);
        using var second = client.Pair(deadline: DateTime.UtcNow.AddSeconds(5));
        await second.RequestStream.WriteAsync(Start(invitation.Id)); await second.RequestStream.CompleteAsync();
        await Refused(async () => { await second.ResponseStream.MoveNext(CancellationToken.None); }, StatusCode.FailedPrecondition);
        Check(factory.Created == 1 && b.State.Count("TrustedManagers") == 0);
    }
    private sealed class Hook : IPeerActivationHook
    {
        public void Apply(SqliteConnection c, SqliteTransaction tx, PeerActivationContext activation)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO PairingActivationEffects VALUES ($peer);";
            cmd.Parameters.AddWithValue("$peer", activation.PeerHostId.ToString("D")); cmd.ExecuteNonQuery();
        }
    }
    public static async Task AuditFailureBlocksAdmission()
    {
        var factory = new TrackingFactory(); await using var a = new Fixture(factory); await using var b = new Fixture(factory); await b.Start();
        using var invitation = b.Runtime.CreateInvitation();
        b.State.Execute("CREATE TRIGGER FailTerminalAudit BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='PairingAttemptFailed' BEGIN SELECT RAISE(ABORT,'fixture secret must not be echoed'); END;");
        using var channel = Channel(a, b, out var transport); using var owner = transport;
        using (var call = new PeerPairingProtocol.PeerPairingProtocolClient(channel).Pair(deadline: DateTime.UtcNow.AddSeconds(5)))
        {
            await call.RequestStream.WriteAsync(Start(invitation.Id));
            await PeerPairingRpcService.Read(call.ResponseStream, PeerPairingFrame.FrameOneofCase.Challenge, CancellationToken.None);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!b.Runtime.AuditStorageUnavailable || b.Runtime.Audit.PendingCount != 1 || Volatile.Read(ref factory.Disposed) != 1) await Task.Delay(10, deadline.Token);
        await SendInvalid(a, b, Start(invitation.Id), StatusCode.FailedPrecondition);
        Check(factory.Created == 1 && b.State.Count("TrustedManagers") == 0);
        try { using var refused = b.Runtime.CreateInvitation(); throw new Exception("Expected audit admission refusal."); } catch (InvalidOperationException) { }
        b.State.Execute("DROP TRIGGER FailTerminalAudit;"); b.Runtime.Audit.Maintain(); Check(!b.Runtime.AuditStorageUnavailable && b.Runtime.Audit.PendingCount == 0);
        Check(HostDatabase.QueryScalarLong(b.State.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PairingAttemptFailed';") == 1);
        using var canceled = b.Runtime.CreateInvitation(); b.Runtime.CancelInvitation(canceled.Id);
        Check(HostDatabase.QueryScalarLong(b.State.Writer, $"SELECT COUNT(*) FROM AuditEvents WHERE AuditEventId='{canceled.Id:D}' AND EventKind='PairingAttemptFailed';") == 1);
        var slots = Enumerable.Range(0, 16).Select(_ => b.Runtime.Enter()).ToArray();
        try { try { using var refused = b.Runtime.Enter(); throw new Exception("Expected capacity refusal."); } catch (InvalidOperationException) { } }
        finally { foreach (var slot in slots) slot.Dispose(); }
        using var restored = b.Runtime.Enter();
        var failingFactory = new TrackingFactory { FailInitialization = true }; await using var initialization = new Fixture(failingFactory); await initialization.Start();
        using var first = initialization.Runtime.CreateInvitation();
        await SendInvalid(a, initialization, Start(first.Id), StatusCode.Unauthenticated);
        Check(failingFactory.Created == 1 && failingFactory.Disposed == 0);
        Check(HostDatabase.QueryScalarLong(initialization.State.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PairingAttemptFailed';") == 1);
    }
    public static async Task Native(string path)
    {
        using var provider = new WindowsSpake2Provider(path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        await using (var a = new Fixture(provider)) await using (var b = new Fixture(provider))
        {
            await b.Start(); using var invitation = b.Runtime.CreateInvitation();
            var paired = await WindowsHostComposition.CreatePeerPairingClient(a.Runtime, a.Certificate.Value).PairAsync(b.Address, invitation.Id, invitation.Code);
            Check(paired.Local.Disposition == PeerBindingDisposition.PeerBoundCreated && paired.Remote == PeerPairingResult.PeerBound);
            Check(a.State.Repository.Read(b.State.HostId)!.CurrentFingerprint == b.Pin && b.State.Repository.Read(a.State.HostId)!.CurrentFingerprint == a.Pin);
            Check(a.State.Repository.Read(b.State.HostId)!.State == "PeerBound" && b.State.Repository.Read(a.State.HostId)!.State == "PeerBound");
            Check(a.State.Count("HostCapabilityGrants") == 0 && a.State.Count("ServerCapabilityGrants") == 0 &&
                b.State.Count("HostCapabilityGrants") == 0 && b.State.Count("ServerCapabilityGrants") == 0);
            await Task.Delay(1100);
            using (var retry = b.Runtime.CreateInvitation())
            {
                var resumed = await WindowsHostComposition.CreatePeerPairingClient(a.Runtime, a.Certificate.Value).PairAsync(b.Address, retry.Id, retry.Code);
                Check(resumed.Local.Disposition == PeerBindingDisposition.ResumePeerBound && resumed.Remote == PeerPairingResult.Resumed && resumed.Local.ExpiresUtc == paired.Local.ExpiresUtc);
                Check(HostDatabase.QueryScalarLong(a.State.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerBoundCreated';") == 1);
                Check(HostDatabase.QueryScalarLong(b.State.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerBoundCreated';") == 1);
            }
            a.State.Execute("CREATE TABLE PairingActivationEffects (Peer TEXT PRIMARY KEY);"); b.State.Execute("CREATE TABLE PairingActivationEffects (Peer TEXT PRIMARY KEY);");
            var ar = new PeerSecurityRpcRuntime(a.State.Database, a.State.HostId, new Hook(), a.State.Time);
            var br = new PeerSecurityRpcRuntime(b.State.Database, b.State.HostId, new Hook(), b.State.Time);
            await using var activation = WindowsHostComposition.BuildPeerApplication(br, b.Certificate.Value, new(IPAddress.Loopback, 0)); await activation.StartAsync();
            var address = new Uri(activation.Urls.Single());
            Check(await WindowsHostComposition.CreatePeerActivationClient(ar, a.Certificate.Value).FinalizeAsync(b.State.HostId, address) == PeerActivationDisposition.Activated);
            Check(a.State.Count("PairingActivationEffects") == 1 && b.State.Count("PairingActivationEffects") == 1);
            await activation.StopAsync();
            await Task.Delay(1100); // Exceed source cooldown: rejection must prove consumed code, not just backoff.
            await Refused(async () => { await WindowsHostComposition.CreatePeerPairingClient(a.Runtime, a.Certificate.Value).PairAsync(b.Address, invitation.Id, invitation.Code); }, StatusCode.Unauthenticated);
        }
        await using (var a = new Fixture(provider)) await using (var b = new Fixture(provider))
        {
            await b.Start(); using var invitation = b.Runtime.CreateInvitation(); var bytes = invitation.Code.CopyBytes();
            bytes[0] = bytes[0] == (byte)'9' ? (byte)'0' : (byte)(bytes[0] + 1);
            using var wrong = new RedactedSecret(bytes); CryptographicOperations.ZeroMemory(bytes);
            await Refused(async () => { await WindowsHostComposition.CreatePeerPairingClient(a.Runtime, a.Certificate.Value).PairAsync(b.Address, invitation.Id, wrong); }, StatusCode.Unauthenticated);
            Check(a.State.Count("TrustedManagers") == 0 && b.State.Count("TrustedManagers") == 0);
            Check(HostDatabase.QueryScalarLong(a.State.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PairingAttemptFailed';") == 1);
            Check(HostDatabase.QueryScalarLong(b.State.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PairingAttemptFailed';") == 1);
        }
        await InvalidVerifiedBinding(provider, false); await InvalidVerifiedBinding(provider, true);
        await using (var a = new Fixture(provider)) await using (var b = new Fixture(provider))
        {
            await b.Start(); using var invitation = b.Runtime.CreateInvitation();
            var loss = new PairingLostResultTransport(new WindowsPeerHttpTransportFactory(a.Certificate.Value));
            try
            {
                await new PeerPairingRpcClient(a.Runtime, loss).PairAsync(b.Address, invitation.Id, invitation.Code);
                throw new Exception("Expected the result read to fail.");
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable && ex.Status.DebugException is IOException io && io.Message == "Fixture pairing result read lost.") { }
            Check(loss.ResultReadFailed && a.State.Repository.Read(b.State.HostId)!.State == "PeerBound");
            Check(HostDatabase.QueryScalarLong(a.State.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PairingAttemptFailed';") == 0);
            Check(HostDatabase.QueryScalarLong(a.State.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerBoundCreated';") == 1);
        }
        Console.WriteLine("PASS actual native first-contact gRPC: reciprocal PAKE/TLS binding and retry, separate pinned activation, consumed/wrong code, credential substitution, trailing-frame refusal and retained PeerBound after result loss.");
    }
    private static async Task InvalidVerifiedBinding(IPairingKeyExchangeFactory factory, bool trailingFrame)
    {
        await using var a = new Fixture(factory); await using var b = new Fixture(factory); await b.Start(); using var invitation = b.Runtime.CreateInvitation();
        using var channel = Channel(a, b, out var transport); using var owner = transport;
        using var call = new PeerPairingProtocol.PeerPairingProtocolClient(channel).Pair(deadline: DateTime.UtcNow.AddSeconds(10));
        await call.RequestStream.WriteAsync(Start(invitation.Id));
        var challenge = (await PeerPairingRpcService.Read(call.ResponseStream, PeerPairingFrame.FrameOneofCase.Challenge, CancellationToken.None)).Challenge;
        var code = invitation.Code.CopyBytes(); IPairingKeyExchange exchange;
        try { exchange = factory.Start(PairingRole.Initiator, code, challenge.Nonce.ToByteArray()); }
        finally { CryptographicOperations.ZeroMemory(code); }
        using (exchange)
        {
            var confirm = exchange.ReceivePeerMessage(challenge.Share.ToByteArray());
            await call.RequestStream.WriteAsync(new() { Share = ByteString.CopyFrom(exchange.InitialMessage) });
            await call.RequestStream.WriteAsync(new() { Confirmation = ByteString.CopyFrom(confirm) });
            var confirmed = await PeerPairingRpcService.Read(call.ResponseStream, PeerPairingFrame.FrameOneofCase.Confirmation, CancellationToken.None); exchange.ConfirmPeer(confirmed.Confirmation.ToByteArray());
            // Either a valid MAC for a DIFFERENT TLS key, or a valid identity followed by a forbidden extra frame.
            var substitution = exchange.CreateIdentityBinding(a.State.HostId, trailingFrame ? a.Public : b.Public);
            var binding = await PeerPairingRpcService.Read(call.ResponseStream, PeerPairingFrame.FrameOneofCase.Binding, CancellationToken.None);
            Check(exchange.VerifyIdentityBinding(binding.Binding.ToByteArray()).HostId == b.State.HostId);
            await call.RequestStream.WriteAsync(new() { Binding = ByteString.CopyFrom(substitution) });
            if (trailingFrame) await call.RequestStream.WriteAsync(new() { Result = PeerPairingResult.PeerBound });
            await call.RequestStream.CompleteAsync();
            await Refused(async () => { await call.ResponseStream.MoveNext(CancellationToken.None); }, trailingFrame ? StatusCode.InvalidArgument : StatusCode.Unauthenticated);
            Check(a.State.Count("TrustedManagers") == 0 && b.State.Count("TrustedManagers") == 0);
            Check(HostDatabase.QueryScalarLong(b.State.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PairingAttemptFailed';") == 1);
        }
    }
}
