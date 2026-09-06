using System.Security.Authentication;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.Host;

public sealed class PeerPairingRpcService(PeerPairingRpcRuntime runtime) : PeerPairingProtocol.PeerPairingProtocolBase
{
    public const int MaximumMessageBytes = 4 * 1024;
    internal static async Task<PeerPairingFrame> Read(IAsyncStreamReader<PeerPairingFrame> stream, PeerPairingFrame.FrameOneofCase expected, CancellationToken ct)
    {
        if (!await stream.MoveNext(ct).ConfigureAwait(false) || stream.Current.FrameCase != expected) throw new ArgumentException("Invalid pairing frame order.");
        return stream.Current;
    }
    internal static void Negotiate(Handshake? hello)
    {
        if (hello is null || hello.Capabilities.Count > 64 || hello.ProductVersion.Length > 256) throw new ArgumentException();
        NegotiatedProtocol.Negotiate(PeerPairingRpcRuntime.Hello(), hello).Require(FeatureCapability.PeerPairing);
    }
    internal static PeerPairingResult Wire(PeerBindingDisposition disposition) => disposition switch
    {
        PeerBindingDisposition.PeerBoundCreated => PeerPairingResult.PeerBound,
        PeerBindingDisposition.ResumePeerBound => PeerPairingResult.Resumed,
        PeerBindingDisposition.ActiveReconfirmed => PeerPairingResult.Reconfirmed,
        PeerBindingDisposition.ReplacementRequired => PeerPairingResult.ReplacementRequired,
        PeerBindingDisposition.RecoveryRequired => PeerPairingResult.RecoveryRequired,
        _ => throw new InvalidOperationException()
    };
    public override async Task Pair(IAsyncStreamReader<PeerPairingFrame> input, IServerStreamWriter<PeerPairingFrame> output, ServerCallContext context)
    {
        PairingAttemptCoordinator.Attempt? attempt = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        try
        {
            var http = context.GetHttpContext();
            if (!http.Request.IsHttps || http.Request.Protocol != "HTTP/2") throw new AuthenticationException();
            var connection = http.Features.Get<PeerPairingConnection>() ?? throw new AuthenticationException(); connection.Begin();
            var start = (await Read(input, PeerPairingFrame.FrameOneofCase.Start, ct).ConfigureAwait(false)).Start;
            Negotiate(start.Handshake);
            attempt = runtime.Attempts.Begin(PeerSecurityRpcService.Id(start.InvitationId), connection.Source, ct);
            await output.WriteAsync(new() { Challenge = new() { Handshake = PeerPairingRpcRuntime.Hello(), Nonce = ByteString.CopyFrom(attempt.SessionNonce), Share = ByteString.CopyFrom(attempt.InitialMessage) } }, ct).ConfigureAwait(false);
            var share = await Read(input, PeerPairingFrame.FrameOneofCase.Share, ct).ConfigureAwait(false);
            if (share.Share.Length != 65) throw new ArgumentException(); attempt.ReceivePeerMessage(share.Share.ToByteArray(), ct);
            var confirmation = await Read(input, PeerPairingFrame.FrameOneofCase.Confirmation, ct).ConfigureAwait(false);
            if (confirmation.Confirmation.Length != 32) throw new ArgumentException();
            var response = attempt.ConfirmPeer(confirmation.Confirmation.ToByteArray(), ct);
            var binding = attempt.CreateIdentityBinding(runtime.HostId, runtime.PublicCredential, ct);
            await output.WriteAsync(new() { Confirmation = ByteString.CopyFrom(response) }, ct).ConfigureAwait(false);
            await output.WriteAsync(new() { Binding = ByteString.CopyFrom(binding) }, ct).ConfigureAwait(false);
            var peerBinding = await Read(input, PeerPairingFrame.FrameOneofCase.Binding, ct).ConfigureAwait(false);
            if (peerBinding.Binding.Length is 0 or > 1200) throw new ArgumentException();
            var peer = attempt.VerifyIdentityBinding(peerBinding.Binding.ToByteArray(), ct);
            if (await input.MoveNext(ct).ConfigureAwait(false)) throw new ArgumentException("Unexpected trailing pairing frame.");
            ct.ThrowIfCancellationRequested(); var stored = runtime.Store(peer, connection.Identity);
            await output.WriteAsync(new() { Result = Wire(stored.Disposition) }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw new RpcException(new(StatusCode.Cancelled, "Pairing canceled.")); }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.ResourceExhausted or StatusCode.Cancelled or StatusCode.DeadlineExceeded)
        { throw new RpcException(new(ex.StatusCode, "Pairing transport refused.")); }
        catch (Exception ex) when (ex is AuthenticationException or CryptographicException)
        { throw new RpcException(new(StatusCode.Unauthenticated, "Pairing proof refused.")); }
        catch (ArgumentException) { throw new RpcException(new(StatusCode.InvalidArgument, "Invalid pairing request.")); }
        catch (Exception ex) when (ex is InvalidOperationException or ProtocolCompatibilityException)
        { throw new RpcException(new(StatusCode.FailedPrecondition, "Pairing precondition failed.")); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { throw new RpcException(new(StatusCode.Internal, "Pairing failed.")); }
        finally { attempt?.Disconnect(); }
    }
}
