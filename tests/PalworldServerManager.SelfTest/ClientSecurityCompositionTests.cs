using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Client.Security;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static class ClientSecurityCompositionTests
{
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class Activation : IHostActivation
    {
        internal int Calls; internal Func<Task>? Start;
        public Task<HostActivationResult> IsHostRunningAsync(CancellationToken ct = default) => Task.FromResult(new HostActivationResult(HostActivationStatus.Stopped));
        public async Task<HostActivationResult> RequestStartAsync(CancellationToken ct = default)
        { Calls++; if (Start is not null) await Start(); return new(HostActivationStatus.StartRequested); }
    }
    private sealed class Handoff(byte[] bytes, Func<Task> beforeDelete) : IOwnerBootstrapHandoffReader
    {
        internal bool Deleted, FailDelete;
        public Task<byte[]?> ReadAsync(CancellationToken ct = default) => Task.FromResult(Deleted ? null : bytes.ToArray());
        public async Task DeleteAsync(CancellationToken ct = default)
        {
            await beforeDelete();
            if (FailDelete) { FailDelete = false; throw new IOException("synthetic deletion failure"); }
            Deleted = true; CryptographicOperations.ZeroMemory(bytes);
        }
        internal void Clear() => CryptographicOperations.ZeroMemory(bytes);
    }
    private sealed class ConfirmStore(ILocalPrincipalCredentialCeremonyStore inner) : ILocalPrincipalCredentialCeremonyStore
    {
        internal bool Fail;
        public Task<ClientCredentialCeremony?> ReadPreparedAsync(Guid host, Guid ticket, ClientCredentialPurpose purpose, CancellationToken ct = default) => inner.ReadPreparedAsync(host, ticket, purpose, ct);
        public Task<LocalPrincipalKeyPair> PrepareAsync(ClientCredentialCeremony ceremony, CancellationToken ct = default) => inner.PrepareAsync(ceremony, ct);
        public Task DiscardPendingAsync(ClientCredentialCeremony ceremony, CancellationToken ct = default) => inner.DiscardPendingAsync(ceremony, ct);
        public Task ConfirmAsync(ClientCredentialCeremony ceremony, Guid principal, ReadOnlyMemory<byte> key, CancellationToken ct = default)
        { if (Fail) { Fail = false; throw new IOException("synthetic confirmation failure"); } return inner.ConfirmAsync(ceremony, principal, key, ct); }
    }
    private sealed class Transport(ILocalHostHttpTransportFactory inner, Func<HttpMessageHandler, HttpMessageHandler> wrap) : ILocalHostHttpTransportFactory
    { public HttpMessageHandler CreateHandler(Guid host) => wrap(inner.CreateHandler(host)); }
    private sealed class LostReply(HttpMessageHandler inner, Func<HttpRequestMessage, bool> lose) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var reply = await base.SendAsync(request, ct);
            if (!lose(request)) return reply;
            reply.Dispose(); throw new HttpRequestException("SENSITIVE synthetic lost response");
        }
    }
    private sealed class FailureHandler(Exception failure) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromException<HttpResponseMessage>(failure); }
    // Pure post-transport protocol validation fixture, not TLS/field evidence.
    private sealed class HandshakeHandler(LocalHandshakeReply reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var payload = reply.ToByteArray(); var framed = new byte[payload.Length + 5];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(1, 4), payload.Length); payload.CopyTo(framed, 5);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20, Content = new ByteArrayContent(framed) };
            response.Content.Headers.ContentType = new("application/grpc"); response.TrailingHeaders.TryAddWithoutValidation("grpc-status", "0");
            return Task.FromResult(response);
        }
    }
    private sealed class SeedKeys(LocalPrincipalKeyPair key) : ILocalPrincipalKeyGenerator
    { public LocalPrincipalKeyPair Generate() => new(key.PublicKey.ToArray(), key.PrivateKey.ToArray()); }
    private sealed class Fixture(bool initialized = false) : IAsyncDisposable
    {
        internal readonly LocalSecurityRpcTests.Fixture Host = new(initialized);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PSMClientComposition" + Guid.NewGuid().ToString("N"));
        internal readonly Activation Activation = new();
        internal readonly Dictionary<Guid, Handoff> Handoffs = [];
        private string PathName => Path.Combine(_root, "principal.bin");
        internal WindowsLocalPrincipalCredentialStore Store => new(new WindowsLocalPrincipalCryptography(), PathName);
        internal ConfirmStore Confirm = null!;
        internal ILocalHostHttpTransportFactory Normal => new WindowsLocalHostHttpTransportFactory(Host.Trust(), Host.Pipe);
        internal LocalSecurityClient Client(ILocalHostHttpTransportFactory? transport = null)
        {
            Confirm ??= new(Store);
            return new(Host.Trust(), transport ?? Normal, Activation, Store, Confirm, new WindowsLocalPrincipalCryptography(), (_, ticket) => Handoffs[ticket]);
        }
        internal async Task Seed(Guid principal, LocalPrincipalKeyPair key)
        {
            var store = new WindowsLocalPrincipalCredentialStore(new SeedKeys(key), PathName);
            var pair = await store.CreateAndStoreAsync(); CryptographicOperations.ZeroMemory(pair.PrivateKey); await store.BindPrincipalIdAsync(principal);
        }
        internal Guid Ticket(LocalEnrollmentPurpose purpose)
        {
            var ticket = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
            try
            {
                using var verifier = LocalEnrollmentVerifier.Compute(Host.State.Key, Host.State.HostId, purpose, ticket, secret);
                var expires = Host.State.Time.Now.AddMinutes(15);
                switch (purpose)
                {
                    case LocalEnrollmentPurpose.InitialOwner: Host.State.Repository.PrepareOfflineBootstrap(ticket, Host.Native, verifier, expires); break;
                    case LocalEnrollmentPurpose.OwnerRotation: Host.State.Repository.PrepareOfflineOwnerRotation(ticket, verifier, expires); break;
                    case LocalEnrollmentPurpose.OwnerRehome: Host.State.Repository.PrepareOfflineOwnerRehome(ticket, Host.Native, verifier, expires); break;
                    default: throw new Exception("Unsupported fixture ticket.");
                }
                var bytes = new byte[73]; "PSMOH001"u8.CopyTo(bytes); Host.State.HostId.ToByteArray().CopyTo(bytes, 8); ticket.ToByteArray().CopyTo(bytes, 24);
                bytes[40] = (byte)purpose; secret.CopyTo(bytes, 41);
                Handoffs.Add(ticket, new(bytes, async () =>
                {
                    var current = await Store.LoadAsync(); Check(current is not null, "Handoff deletion preceded durable binding.");
                    try
                    {
                        var active = new LocalPrincipalAuthenticationRepository(Host.State.Database).TryReadActive(current!.LocalPrincipalId);
                        Check(active is not null && active.PublicVerificationKey == Convert.ToBase64String(current.KeyPair.PublicKey), "Deleted handoff while durable key could not authenticate.");
                    }
                    finally { CryptographicOperations.ZeroMemory(current!.KeyPair.PrivateKey); }
                }));
                return ticket;
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        internal async Task<byte[]> CurrentPublic()
        { var current = await Store.LoadAsync() ?? throw new Exception("Missing current credential."); CryptographicOperations.ZeroMemory(current.KeyPair.PrivateKey); return current.KeyPair.PublicKey; }
        public async ValueTask DisposeAsync()
        {
            foreach (var handoff in Handoffs.Values) handoff.Clear();
            await Host.DisposeAsync(); if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
    public static async Task BootstrapLostReply()
    {
        await using var f = new Fixture(); await f.Host.Start(); var ticket = f.Ticket(LocalEnrollmentPurpose.InitialOwner); var completions = 0;
        var loss = new Transport(f.Normal, inner => new LostReply(inner, request => request.RequestUri!.AbsolutePath.EndsWith("/CompleteBootstrap") && ++completions == 1));
        await Reject<RpcException>(() => f.Client(loss).CompleteHandoffAsync(ticket));
        Check(completions == 1 && f.Host.State.Count("SELECT COUNT(*) FROM LocalPrincipals;") == 1 && !f.Handoffs[ticket].Deleted, "Lost completion retried mutation or deleted handoff.");
        Check(await f.Store.LoadAsync() is null && await f.Store.ReadPreparedAsync(f.Host.State.HostId, ticket, ClientCredentialPurpose.Bootstrap) is not null, "Lost reply discarded pending key or claimed durable binding.");
        await Reject<InvalidOperationException>(() => f.Store.ReadPreparedAsync(f.Host.State.HostId, ticket, ClientCredentialPurpose.Enrollment));
        var result = await f.Client().CompleteHandoffAsync(ticket);
        Check(result.IsOwner && f.Handoffs[ticket].Deleted && (await f.Client().GetIdentityAsync()).LocalPrincipalId == result.LocalPrincipalId, "Retry did not authenticate and bind original Owner.");
        Check(f.Activation.Calls == 0, "Lost result was reclassified as service dormancy.");
    }
    public static async Task RotationBindingAndDeletion()
    {
        await using var f = new Fixture(); await f.Host.Start(); var bootstrap = f.Ticket(LocalEnrollmentPurpose.InitialOwner);
        var owner = await f.Client().CompleteHandoffAsync(bootstrap); var old = await f.CurrentPublic();
        var rotate = f.Ticket(LocalEnrollmentPurpose.OwnerRotation); f.Confirm.Fail = true;
        await Reject<IOException>(() => f.Client().CompleteHandoffAsync(rotate));
        Check(!f.Handoffs[rotate].Deleted && old.SequenceEqual(await f.CurrentPublic()), "Failed confirmation deleted handoff or changed durable current key.");
        await Reject<RpcException>(() => f.Client().GetIdentityAsync());
        f.Handoffs[rotate].FailDelete = true;
        await Reject<IOException>(() => f.Client().CompleteHandoffAsync(rotate));
        Check(!old.SequenceEqual(await f.CurrentPublic()) && !f.Handoffs[rotate].Deleted, "Deletion failure lost confirmed rotated key.");
        Check((await f.Client().CompleteHandoffAsync(rotate)) == owner && f.Handoffs[rotate].Deleted, "Completed receipt could not finish deletion on retry.");
        await Reject<InvalidOperationException>(() => f.Store.ReadPreparedAsync(f.Host.State.HostId, bootstrap, ClientCredentialPurpose.Bootstrap));
        Check(f.Host.State.Count("SELECT COUNT(*) FROM LocalPrincipals WHERE State='Active' AND IsOwner=1;") == 1, "Rotation changed Owner cardinality.");
    }
    public static async Task RehomeKeyChoice()
    {
        foreach (var state in new[] { "active", "revoked", "bad-key" })
        {
            await using var f = new Fixture(initialized: true); var target = f.Host.State.Enroll(f.Host.Native);
            await f.Seed(target, state == "bad-key" ? f.Host.State.OwnerKey : f.Host.State.UserKey); var old = await f.CurrentPublic();
            if (state == "revoked") f.Host.State.Repository.RevokePrincipal(f.Host.State.Actor, target);
            var ticket = f.Ticket(LocalEnrollmentPurpose.OwnerRehome); await f.Host.Start();
            if (state == "bad-key")
            {
                await Reject<RpcException>(() => f.Client().CompleteHandoffAsync(ticket));
                Check(await f.Store.ReadPreparedAsync(f.Host.State.HostId, ticket, ClientCredentialPurpose.OwnerRehome) is null && !f.Handoffs[ticket].Deleted,
                    "Failed existing-key proof was reinterpreted as absence or consumed handoff.");
                continue;
            }
            var completions = 0;
            var loss = new Transport(f.Normal, inner => new LostReply(inner, request => request.RequestUri!.AbsolutePath.EndsWith("/CompleteOwnerRehome") && ++completions == 1));
            await Reject<RpcException>(() => f.Client(loss).CompleteHandoffAsync(ticket));
            var prepared = await f.Store.ReadPreparedAsync(f.Host.State.HostId, ticket, ClientCredentialPurpose.OwnerRehome);
            Check(prepared?.KeyUse == (state == "active" ? ClientCredentialKeyUse.ExistingForRehome : ClientCredentialKeyUse.Fresh), "Wrong persisted re-home key choice.");
            if (state == "revoked") await Reject<RpcException>(() => f.Client().GetIdentityAsync()); // current key is stale after committed/lost result
            var result = await f.Client().CompleteHandoffAsync(ticket);
            Check(result.LocalPrincipalId == target && result.IsOwner && f.Handoffs[ticket].Deleted && completions == 1, "Re-home lost-result retry failed.");
            Check(old.SequenceEqual(await f.CurrentPublic()) == (state == "active"), "Re-home failed to preserve/replace the correct key.");
            Check(f.Host.State.Count("SELECT COUNT(*) FROM LocalPrincipals WHERE State='Active' AND IsOwner=1;") == 1, "Re-home created multiple Owners.");
        }
    }
    public static async Task EnrollmentAndAuthority()
    {
        await using var f = new Fixture(initialized: true); await f.Host.Start(); var ticket = Guid.NewGuid(); var code = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var verifier = LocalEnrollmentVerifier.Compute(f.Host.State.Key, f.Host.State.HostId, LocalEnrollmentPurpose.AdditionalPrincipal, ticket, code);
            f.Host.State.Repository.CreateEnrollment(f.Host.State.Actor, ticket, f.Host.Native, verifier, f.Host.State.Time.Now.AddMinutes(15));
            using var output = new StringWriter(); using var error = new StringWriter();
            Check(await LocalSecurityCommands.RunAsync(["complete-enrollment", "--ticket", ticket.ToString("D")], () => f.Client(), new StringReader(Convert.ToBase64String(code)), output, error) == 0, "Ordinary CLI enrollment failed: " + error);
            var identity = await f.Client().GetIdentityAsync(); Check(!identity.IsOwner, "Enrollment granted Owner.");
            await Reject<RpcException>(() => f.Client().CreateEnrollmentAsync("another-user"));
            await Reject<RpcException>(() => f.Client().RevokeAsync(f.Host.State.Owner));
            Check((await f.Client().CompleteEnrollmentAsync(ticket, code)) == identity, "Consumed enrollment could not confirm its key.");
            f.Host.State.Repository.RevokePrincipal(f.Host.State.Actor, identity.LocalPrincipalId);
            await Reject<RpcException>(() => f.Client().GetIdentityAsync());
            await Reject<RpcException>(() => f.Client().CompleteEnrollmentAsync(ticket, code));
            Check(!output.ToString().Contains(Convert.ToBase64String(code)) && error.ToString().Length == 0, "Completion output leaked its code.");
        }
        finally { CryptographicOperations.ZeroMemory(code); }
        await using var owner = new Fixture(); await owner.Host.Start(); await owner.Client().CompleteHandoffAsync(owner.Ticket(LocalEnrollmentPurpose.InitialOwner));
        using var invitation = await owner.Client().CreateEnrollmentAsync("synthetic-other-native"); var secret = invitation.ExportCodeForDelivery();
        try
        {
            using var verifier = LocalEnrollmentVerifier.Compute(owner.Host.State.Key, owner.Host.State.HostId, LocalEnrollmentPurpose.AdditionalPrincipal, invitation.TicketId, secret);
            var other = owner.Host.State.Repository.CompleteEnrollment(invitation.TicketId, "synthetic-other-native", verifier, Convert.ToBase64String(owner.Host.State.UserKey.PublicKey));
            await owner.Client().RevokeAsync(other);
            Check(owner.Host.State.Text("SELECT State FROM LocalPrincipals WHERE OsPrincipalRef='synthetic-other-native';") == "Revoked", "Owner RPC removal did not persist tombstone.");
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    public static async Task ActivationAndErrors()
    {
        await using var f = new Fixture(); await f.Host.Start();
        var wrong = new WindowsLocalHostHttpTransportFactory(f.Host.Trust(new string('0', 64)), f.Host.Pipe);
        await Reject<RpcException>(() => f.Client(wrong).GetIdentityAsync()); Check(f.Activation.Calls == 0 && f.Host.Delivered == 0, "Wrong pin activated Host or delivered RPC.");
        var generic = new Transport(f.Normal, inner => { inner.Dispose(); return new FailureHandler(new RpcException(new(StatusCode.Unavailable, "SENSITIVE"))); });
        await Reject<RpcException>(() => f.Client(generic).GetIdentityAsync()); Check(f.Activation.Calls == 0, "Generic unavailable status triggered activation.");
        await using var dormant = new Fixture(); var attempts = 0; dormant.Activation.Start = dormant.Host.Start;
        var unavailable = new Transport(dormant.Normal, inner =>
        { if (attempts++ != 0) return inner; inner.Dispose(); return new FailureHandler(new LocalHostEndpointUnavailableException("synthetic absent pipe")); });
        await Reject<AuthenticationException>(() => dormant.Client(unavailable).GetIdentityAsync());
        Check(dormant.Activation.Calls == 1 && attempts == 2 && dormant.Host.State.Count("SELECT COUNT(*) FROM LocalPrincipals;") == 0, "Activation inferred enrollment or failed to retry negotiation.");
        using var output = new StringWriter(); using var error = new StringWriter(); var factories = 0;
        LocalSecurityClient InvalidFactory() { factories++; throw new IOException("SENSITIVE"); }
        foreach (var command in new[] { new[] { "complete-handoff", "--ticket", "bad-id" }, new[] { "complete-enrollment", "--ticket", Guid.NewGuid().ToString("D") }, new[] { "set-owner" } })
            Check(await LocalSecurityCommands.RunAsync(command, InvalidFactory, new StringReader(new string('A', 10000)), output, error) == 1, "Malformed CLI input accepted.");
        Check(factories == 0, "Malformed command opened a client.");
        Check(await LocalSecurityCommands.RunAsync(["identity"], InvalidFactory, TextReader.Null, output, error) == 1 && !error.ToString().Contains("SENSITIVE"), "CLI leaked exception detail.");
    }
    public static async Task ConnectionIdentity()
    {
        await using var f = new Fixture(); await f.Host.Start();
        await Reject<AuthenticationException>(() => f.Client().ConnectAsync());
        var ticket = f.Ticket(LocalEnrollmentPurpose.InitialOwner); var owner = await f.Client().CompleteHandoffAsync(ticket);
        var result = await f.Client().ConnectAsync();
        Check(result.HostId == f.Host.State.HostId && result.Identity == owner, "Connect did not return the authenticated semantic Host/principal pair.");
        var wrong = new WindowsLocalHostHttpTransportFactory(f.Host.Trust(new string('0', 64)), f.Host.Pipe);
        await Reject<RpcException>(() => f.Client(wrong).ConnectAsync());
        Check(f.Activation.Calls == 0, "Connect classified authentication failure as service dormancy.");
    }
    public static async Task NegotiationAndProofBoundaries()
    {
        await using var f = new Fixture();
        foreach (var kind in new[] { "host", "major", "feature", "protocol" })
        {
            var reply = new LocalHandshakeReply { Host = new() { HostId = f.Host.State.HostId.ToString("D") }, Handshake = new() { Protocol = new() { Major = 1, Minor = 1 } } };
            reply.Handshake.Capabilities.Add(FeatureCapability.LocalPrincipalSecurity);
            switch (kind)
            {
                case "host": reply.Host.HostId = Guid.NewGuid().ToString("D"); break;
                case "major": reply.Handshake.Protocol.Major = 2; break;
                case "feature": reply.Handshake.Capabilities.Clear(); break;
                case "protocol": reply.Handshake.Protocol = null; break;
            }
            var transport = new Transport(f.Normal, inner => { inner.Dispose(); return new HandshakeHandler(reply); });
            if (kind == "host") await Reject<InvalidDataException>(() => f.Client(transport).GetIdentityAsync());
            else if (kind == "major") await Reject<PalworldServerManager.Contracts.ProtocolCompatibilityException>(() => f.Client(transport).GetIdentityAsync());
            else if (kind == "feature") await Reject<InvalidOperationException>(() => f.Client(transport).GetIdentityAsync());
            else await Reject<ArgumentException>(() => f.Client(transport).GetIdentityAsync());
        }
        Check(f.Activation.Calls == 0 && await f.Store.LoadAsync() is null, "Rejected negotiation activated or bound credentials.");
        await f.Host.Start(); var ticket = f.Ticket(LocalEnrollmentPurpose.InitialOwner);
        var interfere = new Transport(f.Normal, inner => new LostReply(inner, request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/CompleteBootstrap"))
                f.Host.State.Sql("UPDATE LocalPrincipals SET PublicVerificationKey='" + Convert.ToBase64String(f.Host.State.UserKey.PublicKey) + "' WHERE IsOwner=1;");
            return false;
        }));
        await Reject<RpcException>(() => f.Client(interfere).CompleteHandoffAsync(ticket));
        Check(await f.Store.LoadAsync() is null && !f.Handoffs[ticket].Deleted &&
            await f.Store.ReadPreparedAsync(f.Host.State.HostId, ticket, ClientCredentialPurpose.Bootstrap) is not null,
            "An ID-only result bound a key that failed authentication or deleted its handoff.");
    }
}
