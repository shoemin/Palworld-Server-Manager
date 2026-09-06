using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Client.Security;

public readonly record struct LocalIdentity(Guid LocalPrincipalId, bool IsOwner);
public readonly record struct LocalConnectionInfo(Guid HostId, LocalIdentity Identity);
public sealed class ClientActivationException(HostActivationStatus status) : Exception("Local Host activation failed.")
{ public HostActivationStatus Status { get; } = status; }

public sealed class EnrollmentInvitation(Guid ticket, string expiresUtc, byte[] code) : IDisposable
{
    public Guid TicketId { get; } = ticket;
    public string ExpiresUtc { get; } = expiresUtc;
    private bool _disposed;
    public byte[] ExportCodeForDelivery() { ObjectDisposedException.ThrowIf(_disposed, this); return code.ToArray(); }
    public override string ToString() => "[REDACTED enrollment invitation]";
    public void Dispose() { CryptographicOperations.ZeroMemory(code); _disposed = true; }
}

// Shared by the ordinary CLI and Avalonia. Only client platform seams and wire contracts;
// no Host/Core/persistence assembly or machine private credential is reachable here.
public sealed class LocalSecurityClient(ILocalHostTrustReader trust, ILocalHostHttpTransportFactory transport,
    IHostActivation activation, ILocalPrincipalCredentialStore credentials, ILocalPrincipalCredentialCeremonyStore ceremonies,
    ILocalPrincipalChallengeSigner signer, Func<Guid, Guid, IOwnerBootstrapHandoffReader> handoffs)
{
    public const int MaximumMessageBytes = 16 * 1024;
    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw new InvalidDataException("Invalid local identity.");
    private static void Required(Guid id) { if (id == Guid.Empty) throw new ArgumentException("An identity is required."); }
    public static T? FindCause<T>(Exception failure) where T : Exception
    {
        for (Exception? item = failure; item is not null; item = item is RpcException rpc && rpc.Status.DebugException is { } debug ? debug : item.InnerException)
            if (item is T found) return found;
        return null;
    }
    private sealed class Session(Guid hostId, HttpMessageHandler handler) : IDisposable
    {
        public Guid HostId { get; } = hostId;
        public bool Initialized { get; set; }
        private readonly GrpcChannel _channel = GrpcChannel.ForAddress("https://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler, DisposeHttpClient = true, HttpVersion = HttpVersion.Version20,
            HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact, ThrowOperationCanceledOnCancellation = true,
            MaxReceiveMessageSize = MaximumMessageBytes, MaxSendMessageSize = MaximumMessageBytes
        });
        private static Marshaller<T> Codec<T>() where T : class, IMessage<T>, new() => Marshallers.Create<T>(
            value => value.ToByteArray(), bytes => { var value = new T(); value.MergeFrom(bytes); return value; });
        public async Task<TResponse> Call<TRequest, TResponse>(string method, TRequest request, CancellationToken ct)
            where TRequest : class, IMessage<TRequest>, new() where TResponse : class, IMessage<TResponse>, new()
        {
            using var call = _channel.CreateCallInvoker().AsyncUnaryCall(new Method<TRequest, TResponse>(MethodType.Unary,
                "palworld.manager.v1.LocalSecurityProtocol", method, Codec<TRequest>(), Codec<TResponse>()), null,
                new CallOptions(deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: ct), request);
            return await call.ResponseAsync.ConfigureAwait(false);
        }
        public void Dispose() => _channel.Dispose();
    }
    private async Task<Session> Open(CancellationToken ct)
    {
        var anchor = await trust.ReadAsync(ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var session = new Session(anchor.HostId, transport.CreateHandler(anchor.HostId));
            try
            {
                var hello = new Handshake { Protocol = new() { Major = 1, Minor = 1 }, ProductVersion = "0.5.0-astra" };
                hello.Capabilities.Add(FeatureCapability.LocalPrincipalSecurity);
                var reply = await session.Call<Handshake, LocalHandshakeReply>("Negotiate", hello, ct).ConfigureAwait(false);
                if (reply.Host is null || Id(reply.Host.HostId) != anchor.HostId || reply.Handshake is null ||
                    reply.Handshake.Capabilities.Count > 64 || reply.Handshake.ProductVersion.Length > 256)
                    throw new InvalidDataException("Invalid local handshake.");
                NegotiatedProtocol.Negotiate(hello, reply.Handshake).Require(FeatureCapability.LocalPrincipalSecurity);
                session.Initialized = reply.Initialized; return session;
            }
            catch (Exception ex)
            {
                session.Dispose();
                // Only a proven absent pipe permits activation. A generic gRPC code, wrong pin,
                // malformed negotiation or failed proof cannot be interpreted as dormant.
                if (FindCause<LocalHostEndpointUnavailableException>(ex) is null ||
                    FindCause<LocalHostAuthenticationException>(ex) is not null || attempt >= 3) throw;
                if (attempt == 0)
                {
                    var result = await activation.RequestStartAsync(ct).ConfigureAwait(false);
                    if (result.Status is not HostActivationStatus.AlreadyRunning and not HostActivationStatus.StartRequested)
                        throw new ClientActivationException(result.Status);
                }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }
    private async Task<LocalIdentity> Prove(Session session, Guid principal, LocalPrincipalKeyPair key, CancellationToken ct, LocalChallenge? challenge = null)
    {
        challenge ??= await session.Call<LocalPrincipalRequest, LocalChallenge>("IssueChallenge", new() { LocalPrincipalId = principal.ToString("D") }, ct).ConfigureAwait(false);
        var signature = signer.Sign(new(principal, key), session.HostId, challenge.Payload.Span);
        try
        {
            var reply = await session.Call<LocalProof, LocalPrincipalIdentity>("Authenticate", new() { Signature = ByteString.CopyFrom(signature) }, ct).ConfigureAwait(false);
            if (Id(reply.LocalPrincipalId) != principal) throw new AuthenticationException("Principal confirmation mismatch.");
            return new(principal, reply.IsOwner);
        }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }
    private async Task<LocalIdentity> AuthenticateCurrent(Session session, CancellationToken ct)
    {
        var current = await credentials.LoadAsync(ct).ConfigureAwait(false) ?? throw new AuthenticationException("Local enrollment is required.");
        try { return await Prove(session, current.LocalPrincipalId, current.KeyPair, ct).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(current.KeyPair.PrivateKey); }
    }
    public async Task<LocalIdentity> GetIdentityAsync(CancellationToken ct = default)
    { using var session = await Open(ct).ConfigureAwait(false); return await AuthenticateCurrent(session, ct).ConfigureAwait(false); }
    public async Task<LocalConnectionInfo> ConnectAsync(CancellationToken ct = default)
    {
        using var session = await Open(ct).ConfigureAwait(false);
        var identity = await AuthenticateCurrent(session, ct).ConfigureAwait(false);
        return new(session.HostId, identity);
    }
    public async Task<EnrollmentInvitation> CreateEnrollmentAsync(string intendedOsPrincipal, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(intendedOsPrincipal) || intendedOsPrincipal.Length > 256) throw new ArgumentException("An intended OS principal is required.");
        using var session = await Open(ct).ConfigureAwait(false); await AuthenticateCurrent(session, ct).ConfigureAwait(false);
        var reply = await session.Call<LocalEnrollmentTarget, LocalEnrollmentInvitation>("CreateEnrollment", new() { IntendedOsPrincipal = intendedOsPrincipal }, ct).ConfigureAwait(false);
        var ticket = Id(reply.TicketId);
        if (reply.Code.Length != 32 || !DateTimeOffset.TryParse(reply.ExpiresUtc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out _)) throw new InvalidDataException("Invalid enrollment invitation.");
        return new(ticket, reply.ExpiresUtc, reply.Code.ToByteArray());
    }
    public async Task RevokeAsync(Guid principal, CancellationToken ct = default)
    {
        Required(principal); using var session = await Open(ct).ConfigureAwait(false); await AuthenticateCurrent(session, ct).ConfigureAwait(false);
        await session.Call<LocalPrincipalRequest, LocalEmpty>("RevokePrincipal", new() { LocalPrincipalId = principal.ToString("D") }, ct).ConfigureAwait(false);
    }
    public async Task<LocalIdentity> CompleteEnrollmentAsync(Guid ticket, ReadOnlyMemory<byte> code, CancellationToken ct = default)
    {
        Required(ticket); if (code.Length != 32) throw new ArgumentException("Invalid enrollment code.");
        using var session = await Open(ct).ConfigureAwait(false);
        return await Complete(session, ticket, ClientCredentialPurpose.Enrollment, code, ct).ConfigureAwait(false);
    }
    public async Task<LocalIdentity> CompleteHandoffAsync(Guid ticket, CancellationToken ct = default)
    {
        Required(ticket); using var session = await Open(ct).ConfigureAwait(false);
        var reader = handoffs(session.HostId, ticket);
        var bytes = await reader.ReadAsync(ct).ConfigureAwait(false) ?? throw new InvalidDataException("Owner handoff is unavailable.");
        try
        {
            using var handoff = OwnerHandoff.Parse(bytes, session.HostId, ticket);
            var purpose = handoff.Purpose switch
            {
                OwnerHandoffPurpose.Bootstrap => ClientCredentialPurpose.Bootstrap,
                OwnerHandoffPurpose.CredentialRotation => ClientCredentialPurpose.OwnerRotation,
                OwnerHandoffPurpose.Rehome => ClientCredentialPurpose.OwnerRehome,
                _ => throw new InvalidDataException("Unsupported Owner ceremony.")
            };
            var secret = handoff.ExportSecretForTransport();
            try
            {
                var result = await Complete(session, ticket, purpose, secret, ct).ConfigureAwait(false);
                await reader.DeleteAsync(ct).ConfigureAwait(false); // only after proof and durable confirmation
                return result;
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private async Task<LocalIdentity> Complete(Session session, Guid ticket, ClientCredentialPurpose purpose, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        if (purpose != ClientCredentialPurpose.Bootstrap && !session.Initialized) throw new AuthenticationException("Machine bootstrap must complete first.");
        var prepared = await ceremonies.ReadPreparedAsync(session.HostId, ticket, purpose, ct).ConfigureAwait(false);
        var use = prepared?.KeyUse ?? ClientCredentialKeyUse.Fresh;
        if (prepared is null && purpose == ClientCredentialPurpose.OwnerRehome)
        {
            var current = await credentials.LoadAsync(ct).ConfigureAwait(false);
            if (current is not null)
            {
                try
                {
                    LocalChallenge? challenge = null;
                    try { challenge = await session.Call<LocalPrincipalRequest, LocalChallenge>("IssueChallenge", new() { LocalPrincipalId = current.LocalPrincipalId.ToString("D") }, ct).ConfigureAwait(false); }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated) { } // unknown/revoked target, not a failed signature
                    if (challenge is not null)
                    { await Prove(session, current.LocalPrincipalId, current.KeyPair, ct, challenge).ConfigureAwait(false); use = ClientCredentialKeyUse.ExistingForRehome; }
                }
                finally { CryptographicOperations.ZeroMemory(current.KeyPair.PrivateKey); }
            }
        }
        var ceremony = new ClientCredentialCeremony(session.HostId, ticket, purpose, use);
        var key = await ceremonies.PrepareAsync(ceremony, ct).ConfigureAwait(false);
        try
        {
            var method = purpose switch
            {
                ClientCredentialPurpose.Bootstrap => "CompleteBootstrap", ClientCredentialPurpose.Enrollment => "CompleteEnrollment",
                ClientCredentialPurpose.OwnerRotation => "CompleteOwnerRotation", ClientCredentialPurpose.OwnerRehome => "CompleteOwnerRehome",
                _ => throw new ArgumentException("Unsupported ceremony.")
            };
            var reply = await session.Call<LocalCredentialCompletion, LocalCredentialResult>(method, new()
            { TicketId = ticket.ToString("D"), Secret = ByteString.CopyFrom(secret.Span), PublicKey = ByteString.CopyFrom(key.PublicKey) }, ct).ConfigureAwait(false);
            var principal = Id(reply.LocalPrincipalId);
            var result = await Prove(session, principal, key, ct).ConfigureAwait(false);
            if (purpose != ClientCredentialPurpose.Enrollment && !result.IsOwner) throw new AuthenticationException("The completed principal is no longer Owner.");
            await ceremonies.ConfirmAsync(ceremony, principal, key.PublicKey, ct).ConfigureAwait(false);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key.PrivateKey); }
    }
}
