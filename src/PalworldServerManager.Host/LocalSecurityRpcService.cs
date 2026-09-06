using System.Security.Authentication;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using WireInvitation = PalworldServerManager.Contracts.Wire.LocalEnrollmentInvitation;

namespace PalworldServerManager.Host;

public sealed class LocalSecurityRpcService(LocalSecurityRpcRuntime runtime) : LocalSecurityProtocol.LocalSecurityProtocolBase
{
    public const int MaximumMessageBytes = 16 * 1024;
    private static Guid Id(string text) => Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty ? id : throw new ArgumentException("Invalid identity.");
    private static LocalPrincipalIdentity Identity(AuthenticatedLocalPrincipal identity) => new() { LocalPrincipalId = identity.LocalPrincipalId.ToString("D"), IsOwner = identity.IsOwner };
    private async Task<T> Dispatch<T>(ServerCallContext context, bool negotiation, bool bootstrap, Func<LocalSecurityRpcConnection, Task<T>> action)
    {
        try
        {
            var http = context.GetHttpContext();
            if (!http.Request.IsHttps || http.Request.Protocol != "HTTP/2") throw new AuthenticationException();
            var connection = http.Features.Get<LocalSecurityRpcConnection>() ?? throw new AuthenticationException();
            return await connection.Invoke(runtime.NativePrincipal(http), session =>
            {
                if (!negotiation)
                {
                    if (session.Protocol is null) throw new RpcException(new(StatusCode.FailedPrecondition, "Negotiate this connection first."));
                    session.Protocol.Require(FeatureCapability.LocalPrincipalSecurity);
                    if (!bootstrap && !runtime.IsInitialized()) throw new AuthenticationException();
                }
                return action(session);
            }, context.CancellationToken).ConfigureAwait(false);
        }
        catch (RpcException) { throw; }
        catch (ProtocolCompatibilityException ex)
        { throw new RpcException(new(StatusCode.FailedPrecondition, $"Incompatible protocol majors: Host {ex.LocalMajor}, client {ex.RemoteMajor}.")); }
        catch (OperationCanceledException) { throw new RpcException(new(StatusCode.Cancelled, "Local request canceled.")); }
        catch (AuthenticationException) { throw new RpcException(new(StatusCode.Unauthenticated, "Local authentication or ticket proof refused.")); }
        catch (ArgumentException) { throw new RpcException(new(StatusCode.InvalidArgument, "Invalid local request.")); }
        catch (InvalidOperationException) { throw new RpcException(new(StatusCode.FailedPrecondition, "Local request precondition failed.")); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { throw new RpcException(new(StatusCode.Internal, "Local request failed.")); } // never copy exception/message/input to diagnostics
    }
    public override Task<LocalHandshakeReply> Negotiate(Handshake request, ServerCallContext context) => Dispatch(context, true, false, session =>
    {
        if (session.NegotiationAttempted) throw new RpcException(new(StatusCode.FailedPrecondition, "This connection already attempted negotiation."));
        session.NegotiationAttempted = true;
        if (request.Capabilities.Count > 64 || request.ProductVersion.Length > 256) throw new ArgumentException();
        var hello = new Handshake { Protocol = new() { Major = 1, Minor = 1 }, ProductVersion = "0.5.0-astra" };
        hello.Capabilities.Add(FeatureCapability.LocalPrincipalSecurity);
        var negotiated = NegotiatedProtocol.Negotiate(hello, request);
        hello.Protocol.Minor = negotiated.Minor;
        if (!negotiated.Supports(FeatureCapability.LocalPrincipalSecurity)) hello.Capabilities.Clear();
        var reply = new LocalHandshakeReply { Handshake = hello, Host = new() { HostId = runtime.HostId.ToString("D") }, Initialized = runtime.IsInitialized() };
        session.Protocol = negotiated;
        return Task.FromResult(reply);
    });
    public override Task<LocalChallenge> IssueChallenge(LocalPrincipalRequest request, ServerCallContext context) => Dispatch(context, false, false,
        session => Task.FromResult(new LocalChallenge { Payload = ByteString.CopyFrom(session.Authentication.IssueChallenge(
            Guid.TryParseExact(request.LocalPrincipalId, "D", out var id) ? id : Guid.Empty)) }));
    public override Task<LocalPrincipalIdentity> Authenticate(LocalProof request, ServerCallContext context) => Dispatch(context, false, false,
        session => Task.FromResult(Identity(session.Authentication.Authenticate(request.Signature.Span))));
    public override Task<LocalPrincipalIdentity> GetIdentity(LocalEmpty request, ServerCallContext context) => Dispatch(context, false, false,
        session => Task.FromResult(Identity(session.Authentication.GetCurrentPrincipal())));
    public override Task<WireInvitation> CreateEnrollment(LocalEnrollmentTarget request, ServerCallContext context) => Dispatch(context, false, false, async session =>
    {
        if (request.IntendedOsPrincipal.Length is 0 or > 256) throw new ArgumentException();
        using var invitation = await runtime.Enrollment.CreateEnrollmentAsync(session.Authentication, request.IntendedOsPrincipal, context.CancellationToken).ConfigureAwait(false);
        var code = invitation.Code.CopyBytes();
        try { return new WireInvitation { TicketId = invitation.TicketId.ToString("D"), ExpiresUtc = invitation.ExpiresUtc.ToString("O"), Code = ByteString.CopyFrom(code) }; }
        finally { CryptographicOperations.ZeroMemory(code); }
    });
    public override Task<LocalEmpty> RevokePrincipal(LocalPrincipalRequest request, ServerCallContext context) => Dispatch(context, false, false, session =>
    { runtime.Enrollment.RevokePrincipal(session.Authentication, Id(request.LocalPrincipalId)); return Task.FromResult(new LocalEmpty()); });
    private Task<LocalCredentialResult> Complete(LocalCredentialCompletion request, ServerCallContext context, bool bootstrap,
        Func<Guid, string, ReadOnlyMemory<byte>, string, CancellationToken, Task<Guid>> complete) => Dispatch(context, false, bootstrap, async session =>
    {
        if (request.Secret.Length != 32 || request.PublicKey.Length is 0 or > 256) throw new ArgumentException();
        var id = Id(request.TicketId); var secret = request.Secret.ToByteArray();
        try
        {
            var result = await complete(id, session.Native, secret, Convert.ToBase64String(request.PublicKey.Span), context.CancellationToken).ConfigureAwait(false);
            return new LocalCredentialResult { LocalPrincipalId = result.ToString("D") };
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    });
    public override Task<LocalCredentialResult> CompleteBootstrap(LocalCredentialCompletion request, ServerCallContext context) => Complete(request, context, true, runtime.Enrollment.CompleteBootstrapAsync);
    public override Task<LocalCredentialResult> CompleteEnrollment(LocalCredentialCompletion request, ServerCallContext context) => Complete(request, context, false, runtime.Enrollment.CompleteEnrollmentAsync);
    public override Task<LocalCredentialResult> CompleteOwnerRotation(LocalCredentialCompletion request, ServerCallContext context) => Complete(request, context, false, runtime.Enrollment.CompleteOwnerRotationAsync);
    public override Task<LocalCredentialResult> CompleteOwnerRehome(LocalCredentialCompletion request, ServerCallContext context) => Complete(request, context, false, runtime.Enrollment.CompleteOwnerRehomeAsync);
}
