using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static class LocalSecurityRpcTests
{
    internal sealed class Reader(LocalHostTrustAnchor anchor) : ILocalHostTrustReader
    { public Task<LocalHostTrustAnchor> ReadAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(anchor); } }
    internal sealed class Client : IDisposable
    {
        private readonly GrpcChannel _channel;
        public Client(Guid host, string pipe, ILocalHostTrustReader reader, bool unboundedSend = false)
        {
            _channel = GrpcChannel.ForAddress("https://localhost", new GrpcChannelOptions
            {
                HttpHandler = new WindowsLocalHostHttpTransportFactory(reader, pipe).CreateHandler(host), DisposeHttpClient = true,
                HttpVersion = HttpVersion.Version20, HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                MaxReceiveMessageSize = LocalSecurityRpcService.MaximumMessageBytes, MaxSendMessageSize = unboundedSend ? null : LocalSecurityRpcService.MaximumMessageBytes
            });
        }
        private static Marshaller<T> Codec<T>() where T : class, IMessage<T>, new() => Marshallers.Create<T>(value => value.ToByteArray(), bytes => { var value = new T(); value.MergeFrom(bytes); return value; });
        public async Task<TResponse> Call<TRequest, TResponse>(string name, TRequest request) where TRequest : class, IMessage<TRequest>, new() where TResponse : class, IMessage<TResponse>, new()
        {
            var method = new Method<TRequest, TResponse>(MethodType.Unary, "palworld.manager.v1.LocalSecurityProtocol", name, Codec<TRequest>(), Codec<TResponse>());
            using var call = _channel.CreateCallInvoker().AsyncUnaryCall(method, null, new CallOptions(deadline: DateTime.UtcNow.AddSeconds(15)), request);
            return await call.ResponseAsync;
        }
        public Task<LocalHandshakeReply> Negotiate(uint major = 1, bool feature = true)
        {
            var hello = new Handshake { Protocol = new() { Major = major, Minor = 1 }, ProductVersion = "display-only" };
            if (feature) hello.Capabilities.Add(FeatureCapability.LocalPrincipalSecurity);
            return Call<Handshake, LocalHandshakeReply>("Negotiate", hello);
        }
        public async Task<LocalPrincipalIdentity> Authenticate(Guid host, Guid principal, LocalPrincipalKeyPair key)
        {
            var challenge = await Call<LocalPrincipalRequest, LocalChallenge>("IssueChallenge", new() { LocalPrincipalId = principal.ToString("D") });
            var signature = new WindowsLocalPrincipalCryptography().Sign(new(principal, key), host, challenge.Payload.Span);
            return await Call<LocalProof, LocalPrincipalIdentity>("Authenticate", new() { Signature = ByteString.CopyFrom(signature) });
        }
        public void Dispose() => _channel.Dispose();
    }
    private sealed class Fixture(bool initialized = false) : IAsyncDisposable
    {
        internal readonly LocalEnrollmentTests.Fixture State = new(initialized);
        internal readonly string Pipe = "PSMAstraRpc" + Guid.NewGuid().ToString("N");
        internal string Native { get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; } }
        private readonly X509Certificate2 _certificate = LocalIpcSpike.CreateTestCertificate();
        private WebApplication? _app;
        internal int Delivered;
        internal readonly List<LocalAuthenticationFailure> Failures = [];
        internal Reader Trust(string? pin = null)
        {
            using var key = _certificate.GetECDsaPublicKey()!;
            return new(LocalHostTrustAnchor.Parse(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, hostId = State.HostId,
                currentHostCredentialFingerprint = pin ?? Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())),
                pendingHostCredentialFingerprint = (string?)null, pendingRotationId = (Guid?)null })));
        }
        internal Client Connect(string? pin = null) => new(State.HostId, Pipe, Trust(pin));
        internal async Task Start()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var runtime = new LocalSecurityRpcRuntime(State.Database, State.HostId, State.Secrets,
                context => { Interlocked.Increment(ref Delivered); return WindowsLocalTlsEndpoint.ReadNativePrincipal(context); },
                reason => { lock (Failures) Failures.Add(reason); }, State.Time);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] }); builder.Logging.ClearProviders();
            builder.Services.AddSingleton(runtime);
            builder.Services.AddGrpc(options => { options.EnableDetailedErrors = false; options.MaxReceiveMessageSize = LocalSecurityRpcService.MaximumMessageBytes; options.MaxSendMessageSize = LocalSecurityRpcService.MaximumMessageBytes; });
            WindowsLocalTlsEndpoint.Configure(builder.WebHost, Pipe, identity.User!, identity.User!, _certificate, runtime.BindConnection);
            _app = builder.Build(); _app.MapGrpcService<LocalSecurityRpcService>(); await _app.StartAsync();
        }
        internal (Guid Id, byte[] Secret) BootstrapTicket()
        {
            var id = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
            using var proof = State.Proof(id, bootstrap: true, code: secret);
            State.Repository.PrepareOfflineBootstrap(id, Native, proof, State.Time.Now.AddMinutes(15)); return (id, secret);
        }
        internal async Task<Guid> Bootstrap(Client client)
        {
            await client.Negotiate(); var ticket = BootstrapTicket();
            try
            {
                var result = await client.Call<LocalCredentialCompletion, LocalCredentialResult>("CompleteBootstrap", new()
                { TicketId = ticket.Id.ToString("D"), Secret = ByteString.CopyFrom(ticket.Secret), PublicKey = ByteString.CopyFrom(State.OwnerKey.PublicKey) });
                return Guid.Parse(result.LocalPrincipalId);
            }
            finally { CryptographicOperations.ZeroMemory(ticket.Secret); }
        }
        public async ValueTask DisposeAsync()
        {
            try { if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); } }
            finally
            {
                using var key = (ECDsaCng)_certificate.GetECDsaPrivateKey()!;
                key.Key.Delete(); _certificate.Dispose(); State.Dispose();
            }
        }
    }
    private static async Task Refused<T>(Task<T> call, StatusCode expected)
    {
        try { await call; throw new Exception("RPC unexpectedly succeeded."); }
        catch (RpcException ex) { Check(ex.StatusCode == expected, "Unexpected RPC refusal: " + ex.StatusCode + ": " + ex.Status.Detail); }
    }
    public static async Task NegotiationAndBootstrap()
    {
        await using var f = new Fixture(); await f.Start();
        using (var wrong = f.Connect(new string('0', 64)))
        {
            try { await wrong.Negotiate(); throw new Exception("Wrong-pin gRPC call succeeded."); }
            catch (RpcException ex)
            {
                var cause = ex.Status.DebugException;
                while (cause is not null && cause is not LocalHostAuthenticationException) cause = cause.InnerException;
                Check(cause is LocalHostAuthenticationException, "TLS failure lost its authentication classification.");
            }
            Check(f.Delivered == 0, "RPC reached Host before TLS authentication.");
        }
        using (var mismatch = f.Connect())
        {
            await Refused(mismatch.Negotiate(2), StatusCode.FailedPrecondition);
            await Refused(mismatch.Negotiate(), StatusCode.FailedPrecondition);
        }
        using (var unsupported = f.Connect())
        {
            await unsupported.Negotiate(feature: false);
            await Refused(unsupported.Call<LocalEmpty, LocalPrincipalIdentity>("GetIdentity", new()), StatusCode.FailedPrecondition);
        }
        using var client = f.Connect();
        await Refused(client.Call<LocalEmpty, LocalPrincipalIdentity>("GetIdentity", new()), StatusCode.FailedPrecondition);
        var hello = await client.Negotiate(); Check(!hello.Initialized && hello.Host.HostId == f.State.HostId.ToString("D"), "Handshake identity/bootstrap state wrong.");
        await Refused(client.Call<LocalEnrollmentTarget, PalworldServerManager.Contracts.Wire.LocalEnrollmentInvitation>("CreateEnrollment", new() { IntendedOsPrincipal = f.Native }), StatusCode.Unauthenticated);
        await Refused(client.Call<LocalPrincipalRequest, LocalChallenge>("IssueChallenge", new() { LocalPrincipalId = Guid.NewGuid().ToString("D") }), StatusCode.Unauthenticated);
        var ticket = f.BootstrapTicket();
        try
        {
            var completion = new LocalCredentialCompletion { TicketId = ticket.Id.ToString("D"), Secret = ByteString.CopyFrom(ticket.Secret), PublicKey = ByteString.CopyFrom(f.State.OwnerKey.PublicKey) };
            var result = await client.Call<LocalCredentialCompletion, LocalCredentialResult>("CompleteBootstrap", completion);
            var principal = Guid.Parse(result.LocalPrincipalId);
            Check((await client.Authenticate(f.State.HostId, principal, f.State.OwnerKey)).IsOwner, "Real RPC bootstrap did not authenticate intended Owner.");
            Check((await client.Call<LocalEmpty, LocalPrincipalIdentity>("GetIdentity", new())).LocalPrincipalId == result.LocalPrincipalId, "RPC identity was not retained on connection.");
            Check((await client.Call<LocalCredentialCompletion, LocalCredentialResult>("CompleteBootstrap", completion)).LocalPrincipalId == result.LocalPrincipalId, "Lost bootstrap result could not be retried.");
            using var freshConnection = f.Connect(); await freshConnection.Negotiate();
            await Refused(freshConnection.Call<LocalEmpty, LocalPrincipalIdentity>("GetIdentity", new()), StatusCode.Unauthenticated);
            await Refused(freshConnection.Call<LocalPrincipalRequest, LocalChallenge>("IssueChallenge", new() { LocalPrincipalId = Guid.NewGuid().ToString("D") }), StatusCode.Unauthenticated);
        }
        finally { CryptographicOperations.ZeroMemory(ticket.Secret); }
    }
    public static async Task AuthenticationAndAuthority()
    {
        await using var f = new Fixture(); await f.Start(); using var client = f.Connect(); var owner = await f.Bootstrap(client);
        await client.Authenticate(f.State.HostId, owner, f.State.OwnerKey);
        await Refused(client.Call<LocalPrincipalRequest, LocalEmpty>("RevokePrincipal", new() { LocalPrincipalId = owner.ToString("D") }), StatusCode.Unauthenticated);
        await Refused(client.Call<LocalPrincipalRequest, LocalChallenge>("IssueChallenge", new() { LocalPrincipalId = "not-an-identity" }), StatusCode.Unauthenticated);
        await Refused(client.Call<LocalEmpty, LocalPrincipalIdentity>("GetIdentity", new()), StatusCode.Unauthenticated);
        var challenge = await client.Call<LocalPrincipalRequest, LocalChallenge>("IssueChallenge", new() { LocalPrincipalId = owner.ToString("D") });
        var signed = new LocalProof { Signature = ByteString.CopyFrom(new WindowsLocalPrincipalCryptography().Sign(new(owner, f.State.OwnerKey), f.State.HostId, challenge.Payload.Span)) };
        using (var other = f.Connect())
        {
            await other.Negotiate(); await Refused(other.Call<LocalProof, LocalPrincipalIdentity>("Authenticate", signed), StatusCode.Unauthenticated);
        }
        async Task<bool> Attempt()
        {
            try { await client.Call<LocalProof, LocalPrincipalIdentity>("Authenticate", signed); return true; }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated) { return false; }
        }
        var attempts = await Task.WhenAll(Attempt(), Attempt()); Check(attempts.Count(x => x) == 1, "Concurrent RPCs reused the same challenge.");
        await Refused(client.Call<LocalEmpty, LocalPrincipalIdentity>("GetIdentity", new()), StatusCode.Unauthenticated);
        await client.Authenticate(f.State.HostId, owner, f.State.OwnerKey);
        var invitation = await client.Call<LocalEnrollmentTarget, PalworldServerManager.Contracts.Wire.LocalEnrollmentInvitation>("CreateEnrollment", new() { IntendedOsPrincipal = "different-native-principal" });
        var completion = new LocalCredentialCompletion { TicketId = invitation.TicketId, Secret = invitation.Code, PublicKey = ByteString.CopyFrom(f.State.UserKey.PublicKey) };
        await Refused(client.Call<LocalCredentialCompletion, LocalCredentialResult>("CompleteEnrollment", completion), StatusCode.Unauthenticated);
        // Seed the other native principal through the real repository; the actual RPC native SID
        // must still reject attempts to authenticate that principal from this Windows process.
        using var verifier = f.State.Proof(Guid.Parse(invitation.TicketId), code: invitation.Code.ToByteArray());
        var otherId = f.State.Repository.CompleteEnrollment(Guid.Parse(invitation.TicketId), "different-native-principal", verifier, Convert.ToBase64String(f.State.UserKey.PublicKey));
        await Refused(client.Call<LocalPrincipalRequest, LocalChallenge>("IssueChallenge", new() { LocalPrincipalId = otherId.ToString("D") }), StatusCode.Unauthenticated);
        Check(f.Failures.Contains(LocalAuthenticationFailure.NativeIdentityMismatch), "RPC native mismatch did not reach sanitized failure reporting.");
        await client.Authenticate(f.State.HostId, owner, f.State.OwnerKey);
        await client.Call<LocalPrincipalRequest, LocalEmpty>("RevokePrincipal", new() { LocalPrincipalId = otherId.ToString("D") });
        Check(f.State.Text("SELECT State FROM LocalPrincipals WHERE OsPrincipalRef='different-native-principal';") == "Revoked", "RPC removal did not persist a tombstone.");
        await using var ordinary = new Fixture(initialized: true); var user = ordinary.State.Enroll(ordinary.Native); await ordinary.Start();
        using var userClient = ordinary.Connect(); await userClient.Negotiate();
        Check(!(await userClient.Authenticate(ordinary.State.HostId, user, ordinary.State.UserKey)).IsOwner, "Non-Owner RPC identity gained Owner authority.");
        var before = ordinary.State.Count("SELECT COUNT(*) FROM AuditEvents;");
        await Refused(userClient.Call<LocalEnrollmentTarget, PalworldServerManager.Contracts.Wire.LocalEnrollmentInvitation>("CreateEnrollment", new() { IntendedOsPrincipal = "not-authorized" }), StatusCode.Unauthenticated);
        await Refused(userClient.Call<LocalPrincipalRequest, LocalEmpty>("RevokePrincipal", new() { LocalPrincipalId = ordinary.State.Owner.ToString("D") }), StatusCode.Unauthenticated);
        Check(ordinary.State.Count("SELECT COUNT(*) FROM AuditEvents;") == before, "Refused non-Owner mutation created an audit or authority effect.");
    }
    public static async Task RecoveryAndRollback()
    {
        await using var f = new Fixture(); await f.Start(); using var client = f.Connect(); var owner = await f.Bootstrap(client);
        await client.Authenticate(f.State.HostId, owner, f.State.OwnerKey);
        var rotation = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var verifier = LocalEnrollmentVerifier.Compute(f.State.Key, f.State.HostId, LocalEnrollmentPurpose.OwnerRotation, rotation, secret);
            f.State.Repository.PrepareOfflineOwnerRotation(rotation, verifier, f.State.Time.Now.AddMinutes(15));
            var request = new LocalCredentialCompletion { TicketId = rotation.ToString("D"), Secret = ByteString.CopyFrom(secret), PublicKey = ByteString.CopyFrom(f.State.UserKey.PublicKey) };
            f.State.Sql("CREATE TRIGGER rpc_audit_failure BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT,'private-input-must-not-be-echoed'); END;");
            try { await client.Call<LocalCredentialCompletion, LocalCredentialResult>("CompleteOwnerRotation", request); throw new Exception("RPC audit failure was ignored."); }
            catch (RpcException ex) { Check(ex.StatusCode == StatusCode.Internal && ex.Status.Detail == "Local request failed.", "RPC leaked an exception or misreported audit failure."); }
            f.State.Sql("DROP TRIGGER rpc_audit_failure;");
            Check((await client.Call<LocalEmpty, LocalPrincipalIdentity>("GetIdentity", new())).IsOwner, "Rolled-back RPC recovery invalidated prior Owner.");
            Check((await client.Call<LocalCredentialCompletion, LocalCredentialResult>("CompleteOwnerRotation", request)).LocalPrincipalId == owner.ToString("D"), "RPC rotation changed Owner identity.");
            await Refused(client.Call<LocalEmpty, LocalPrincipalIdentity>("GetIdentity", new()), StatusCode.Unauthenticated);
            Check((await client.Authenticate(f.State.HostId, owner, f.State.UserKey)).IsOwner, "RPC rotated key could not authenticate.");
            await client.Call<LocalCredentialCompletion, LocalCredentialResult>("CompleteOwnerRotation", request);
            // Synthetic prior Owner identity relocation only to exercise the re-home wire adapter
            // under this one real native token. Final two-account ceremony remains 42d3c.
            f.State.Sql("UPDATE LocalPrincipals SET OsPrincipalRef='prior-owner' WHERE IsOwner=1;");
            var rehome = Guid.NewGuid(); using var rehomeProof = LocalEnrollmentVerifier.Compute(f.State.Key, f.State.HostId, LocalEnrollmentPurpose.OwnerRehome, rehome, secret);
            f.State.Repository.PrepareOfflineOwnerRehome(rehome, f.Native, rehomeProof, f.State.Time.Now.AddMinutes(15));
            request.TicketId = rehome.ToString("D"); request.PublicKey = ByteString.CopyFrom(f.State.OwnerKey.PublicKey);
            var result = await client.Call<LocalCredentialCompletion, LocalCredentialResult>("CompleteOwnerRehome", request);
            Check(result.LocalPrincipalId != owner.ToString("D") && (await client.Authenticate(f.State.HostId, Guid.Parse(result.LocalPrincipalId), f.State.OwnerKey)).IsOwner,
                "RPC re-home did not install exactly the intended replacement.");
            Check(f.State.Count("SELECT COUNT(*) FROM LocalPrincipals WHERE IsOwner=1 AND State='Active';") == 1, "RPC recovery broke Owner cardinality.");
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    public static async Task LimitsAndScope()
    {
        await using var f = new Fixture(); await f.Start();
        using var client = new Client(f.State.HostId, f.Pipe, f.Trust(), unboundedSend: true); await client.Negotiate();
        var request = new LocalCredentialCompletion { TicketId = Guid.NewGuid().ToString("D"), Secret = ByteString.CopyFrom(new byte[32]), PublicKey = ByteString.CopyFrom(f.State.OwnerKey.PublicKey) };
        foreach (var method in new[] { "CompleteEnrollment", "CompleteOwnerRotation", "CompleteOwnerRehome" })
            await Refused(client.Call<LocalCredentialCompletion, LocalCredentialResult>(method, request), StatusCode.Unauthenticated);
        foreach (var method in new[] { "PrepareBootstrap", "RecoverMachine", "SetOwner" })
            await Refused(client.Call<LocalEmpty, LocalEmpty>(method, new()), StatusCode.Unimplemented);
        request.Secret = ByteString.CopyFrom(new byte[33]);
        await Refused(client.Call<LocalCredentialCompletion, LocalCredentialResult>("CompleteBootstrap", request), StatusCode.InvalidArgument);
        request.Secret = ByteString.CopyFrom(new byte[LocalSecurityRpcService.MaximumMessageBytes + 1]);
        var delivered = f.Delivered;
        await Refused(client.Call<LocalCredentialCompletion, LocalCredentialResult>("CompleteBootstrap", request), StatusCode.ResourceExhausted);
        Check(f.Delivered == delivered && f.State.Count("SELECT COUNT(*) FROM LocalPrincipals;") == 0, "Oversized RPC reached authority handler or created a principal.");
        using var malformed = f.Connect(); var hello = new Handshake { Protocol = new() { Major = 1, Minor = 1 }, ProductVersion = new string('x', 257) };
        await Refused(malformed.Call<Handshake, LocalHandshakeReply>("Negotiate", hello), StatusCode.InvalidArgument);
        await Refused(malformed.Call<LocalEmpty, LocalPrincipalIdentity>("GetIdentity", new()), StatusCode.FailedPrecondition);
    }
}
