using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class PeerSecurityRpcTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Peer RPC assertion failed."); }
    private static async Task Refused<T>(Task<T> task, StatusCode status)
    {
        try { await task; } catch (RpcException ex) when (ex.StatusCode == status) { return; }
        throw new Exception("Expected peer RPC refusal: " + status);
    }
    private static async Task TlsRefused(Task<PeerHello> task)
    {
        try { await task; }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Internal or StatusCode.Unavailable)
        {
            // Require a real transport/TLS cause, not an application Internal status or timeout.
            for (Exception? cause = ex.InnerException; cause is not null; cause = cause.InnerException)
                if (cause is System.Security.Authentication.AuthenticationException or IOException) return;
            throw;
        }
        throw new Exception("Expected actual TLS refusal.");
    }
    private sealed class Hook : IPeerActivationHook
    {
        public void Apply(SqliteConnection c, SqliteTransaction tx, PeerActivationContext activation)
        {
            using var command = c.CreateCommand(); command.Transaction = tx;
            command.CommandText = "INSERT INTO ActivationRpcEffects VALUES ($peer);";
            command.Parameters.AddWithValue("$peer", activation.PeerHostId.ToString("D")); command.ExecuteNonQuery();
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly PeerTrustTests.Fixture State = new();
        internal readonly PeerTlsTests.Certificate Certificate = new();
        internal PeerSecurityRpcRuntime Runtime { get; }
        internal string Pin => WindowsPeerTls.PublicFingerprint(Certificate.Value);
        private WebApplication? app;
        internal Uri Address => new(app!.Urls.Single());
        internal Fixture()
        {
            State.Execute($"UPDATE SecureCredentialReferences SET PublicKeyFingerprint='{Pin}' WHERE CredentialRef='current';");
            State.Execute("CREATE TABLE ActivationRpcEffects (Peer TEXT PRIMARY KEY);");
            Runtime = new(State.Database, State.HostId, new Hook(), State.Time);
        }
        internal void Bind(Fixture peer) => State.Repository.RecordVerifiedBinding(peer.State.HostId, peer.Pin, Pin);
        internal async Task Start()
        {
            app = WindowsHostComposition.BuildPeerApplication(Runtime, Certificate.Value, new(IPAddress.Loopback, 0));
            Check(app.Configuration["urls"] is null && app.Environment.EnvironmentName == "Production");
            await app.StartAsync(); Check(app.Urls.Count == 1 && Address.Scheme == "https");
        }
        internal async Task Stop()
        {
            if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); app = null; }
        }
        public async ValueTask DisposeAsync()
        { try { await Stop(); } finally { Certificate.Dispose(); State.Dispose(); } }
    }
    private sealed class RawClient : IDisposable
    {
        private readonly SocketsHttpHandler handler;
        private readonly GrpcChannel channel;
        internal readonly PeerSecurityProtocol.PeerSecurityProtocolClient Rpc;
        internal RawClient(Fixture client, Fixture server, string? expectedPin = null, bool unboundedSend = false)
        {
            handler = new() { UseProxy = false, AllowAutoRedirect = false,
                SslOptions = WindowsPeerTls.ClientOptions(client.Certificate.Value, pin => pin == (expectedPin ?? server.Pin)) };
            channel = GrpcChannel.ForAddress(server.Address, new GrpcChannelOptions
            {
                HttpHandler = handler, HttpVersion = HttpVersion.Version20, HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                MaxReceiveMessageSize = PeerSecurityRpcService.MaximumMessageBytes,
                MaxSendMessageSize = unboundedSend ? null : PeerSecurityRpcService.MaximumMessageBytes
            });
            Rpc = new(channel);
        }
        internal Task<PeerHello> Negotiate(PeerHello hello) => Rpc.NegotiateAsync(hello, deadline: DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
        internal Task<PeerActivationReply> Activate(PeerActivationAck ack) => Rpc.ActivateAsync(ack, deadline: DateTime.UtcNow.AddSeconds(5)).ResponseAsync;
        public void Dispose() { channel.Dispose(); handler.Dispose(); }
    }
    private static PeerActivationAck Ack(Fixture sender, Fixture receiver) => PeerSecurityRpcService.Wire(
        sender.State.Repository.PrepareActivationAcknowledgement(receiver.State.HostId, receiver.Pin, sender.Pin));
    public static async Task ActualActivationAndLostReply()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); a.Bind(b); b.Bind(a); await b.Start();
        using (var first = new RawClient(a, b))
        {
            await first.Negotiate(PeerSecurityRpcRuntime.Hello(a.State.HostId));
            // Deliver A's acknowledgment to B, then discard B's reply without committing A.
            await first.Activate(Ack(a, b));
        }
        Check(a.State.Repository.Read(b.State.HostId)!.State == "PeerBound" && b.State.Repository.Read(a.State.HostId)!.State == "Active");
        await b.Stop(); await b.Start(); // New real listener/connection; durable trust survives.
        var result = await new PeerActivationRpcClient(a.Runtime, a.Certificate.Value).FinalizeAsync(b.State.HostId, b.Address);
        Check(result == PeerActivationDisposition.Activated && a.State.Repository.Read(b.State.HostId)!.State == "Active");
        Check(await new PeerActivationRpcClient(a.Runtime, a.Certificate.Value).FinalizeAsync(b.State.HostId, b.Address) == PeerActivationDisposition.AlreadyActive);
        Check(a.State.Count("ActivationRpcEffects") == 1 && b.State.Count("ActivationRpcEffects") == 1);
        Check(a.State.Count("HostCapabilityGrants") == 0 && b.State.Count("ServerCapabilityGrants") == 0);
    }
    public static async Task ProtocolAndConnectionIdentity()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); a.Bind(b); b.Bind(a); await b.Start();
        using (var client = new RawClient(a, b))
        {
            await Refused(client.Activate(Ack(a, b)), StatusCode.FailedPrecondition);
            var hello = PeerSecurityRpcRuntime.Hello(a.State.HostId); await client.Negotiate(hello);
            await Refused(client.Negotiate(hello), StatusCode.FailedPrecondition);
            var wrong = Ack(a, b); wrong.FromHostId = Guid.NewGuid().ToString("D");
            await Refused(client.Activate(wrong), StatusCode.FailedPrecondition);
            b.State.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{a.State.HostId:D}';");
            await Refused(client.Activate(Ack(a, b)), StatusCode.Unauthenticated); // Same TLS session, fresh state.
            b.State.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=0 WHERE PeerHostId='{a.State.HostId:D}';");
        }
        using (var client = new RawClient(a, b))
            await Refused(client.Negotiate(PeerSecurityRpcRuntime.Hello(Guid.NewGuid())), StatusCode.Unauthenticated);
        using (var client = new RawClient(a, b))
        {
            var hello = PeerSecurityRpcRuntime.Hello(a.State.HostId); hello.Handshake.Protocol.Major = 2;
            await Refused(client.Negotiate(hello), StatusCode.FailedPrecondition);
        }
        using (var client = new RawClient(a, b))
        {
            var hello = PeerSecurityRpcRuntime.Hello(a.State.HostId); hello.Handshake.Capabilities.Clear();
            hello.Handshake.Capabilities.Add((FeatureCapability)999);
            await Refused(client.Negotiate(hello), StatusCode.FailedPrecondition);
        }
        Check(a.State.Count("ActivationRpcEffects") == 0 && b.State.Count("ActivationRpcEffects") == 0);
    }
    public static async Task TlsRefusalsAndLimits()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); await using var rogue = new Fixture();
        a.Bind(b); b.Bind(a); await b.Start();
        using (var wrongServer = new RawClient(a, b, new string('F', 64)))
            await TlsRefused(wrongServer.Negotiate(PeerSecurityRpcRuntime.Hello(a.State.HostId)));
        using (var wrongClient = new RawClient(rogue, b))
            await TlsRefused(wrongClient.Negotiate(PeerSecurityRpcRuntime.Hello(a.State.HostId)));
        using (var oversized = new RawClient(a, b, unboundedSend: true))
        {
            var hello = PeerSecurityRpcRuntime.Hello(a.State.HostId); hello.Handshake.ProductVersion = new string('X', 20000);
            await Refused(oversized.Negotiate(hello), StatusCode.ResourceExhausted);
        }
        using (var expired = new RawClient(a, b))
        {
            await expired.Negotiate(PeerSecurityRpcRuntime.Hello(a.State.HostId));
            b.State.Time.Now += TimeSpan.FromMinutes(30);
            await Refused(expired.Activate(Ack(a, b)), StatusCode.Unauthenticated);
            Check(!b.State.Repository.RecognizesTransportFingerprint(a.Pin));
            Check(b.State.Repository.ExpirePending() == 1);
            await Refused(expired.Activate(Ack(a, b)), StatusCode.Unauthenticated);
        }
        Check(!b.State.Repository.RecognizesTransportFingerprint(a.Pin));
        Check(b.State.Count("ActivationRpcEffects") == 0);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await new PeerActivationRpcClient(a.Runtime, a.Certificate.Value).FinalizeAsync(b.State.HostId, b.Address, cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
    }
    public static async Task RecordedPendingPin()
    {
        await using var a = new Fixture(); await using var b = new Fixture(); await using var rotated = new Fixture();
        a.Bind(b); b.Bind(a);
        // Represents a previously authenticated rotation staging, not a rotation API.
        b.State.Execute($"UPDATE TrustedManagers SET State='Active',PendingTrustedPublicKeyFingerprint='{rotated.Pin}',PendingReconfirmationRequired=1 WHERE PeerHostId='{a.State.HostId:D}';");
        b.State.Time.Now += TimeSpan.FromHours(1);
        Check(b.State.Repository.RecognizesTransportFingerprint(rotated.Pin));
        await b.Start();
        using var client = new RawClient(rotated, b);
        await client.Negotiate(PeerSecurityRpcRuntime.Hello(a.State.HostId));
        var reply = await client.Activate(new() { FromHostId = a.State.HostId.ToString("D"), RecordedHostId = b.State.HostId.ToString("D"), RecordedFingerprint = b.Pin });
        Check(reply.Result == PeerActivationResult.AlreadyActive && reply.Acknowledgement.RecordedFingerprint == rotated.Pin);
        Check(b.State.Count("ActivationRpcEffects") == 0);
    }
}
