using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Net.Client;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Host;

internal sealed record PeerPairingCompletion(PeerBindingResult Local, PeerPairingResult Remote);
internal sealed class PeerPairingRpcClient(PeerPairingRpcRuntime runtime, IPeerHttpTransportFactory transport)
{
    internal async Task<PeerPairingCompletion> PairAsync(Uri address, Guid invitation, RedactedSecret code, CancellationToken ct = default)
    {
        if (invitation == Guid.Empty || !address.IsAbsoluteUri || address.Scheme != "https" || address.UserInfo.Length != 0 ||
            address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0) throw new ArgumentException("A reachable pairing HTTPS address is required.");
        using var admission = runtime.Enter();
        var attemptId = Guid.NewGuid(); var stored = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            ct = deadline.Token;
            // The separate first-contact service has no pinned trust yet. Code-confirmed binding
            // must match this actual TLS peer before any trust is persisted.
            using var connection = transport.Create(_ => true);
            using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = connection.Handler, HttpVersion = HttpVersion.Version20, HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                MaxSendMessageSize = PeerPairingRpcService.MaximumMessageBytes, MaxReceiveMessageSize = PeerPairingRpcService.MaximumMessageBytes
            });
            using var call = new PeerPairingProtocol.PeerPairingProtocolClient(channel).Pair(cancellationToken: ct);
            await call.RequestStream.WriteAsync(new() { Start = new() { Handshake = PeerPairingRpcRuntime.Hello(), InvitationId = invitation.ToString("D") } }, ct).ConfigureAwait(false);
            var challenge = (await PeerPairingRpcService.Read(call.ResponseStream, PeerPairingFrame.FrameOneofCase.Challenge, ct).ConfigureAwait(false)).Challenge;
            PeerPairingRpcService.Negotiate(challenge.Handshake);
            if (challenge.Nonce.Length != 32 || challenge.Share.Length != 65 || connection.Identity.LocalFingerprint != runtime.LocalFingerprint) throw new AuthenticationException("Pairing challenge refused.");
            var bytes = code.CopyBytes(); IPairingKeyExchange exchange;
            try { exchange = runtime.Factory.Start(PairingRole.Initiator, bytes, challenge.Nonce.ToByteArray(), ct); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            using (exchange)
            {
                var confirmation = exchange.ReceivePeerMessage(challenge.Share.ToByteArray(), ct);
                await call.RequestStream.WriteAsync(new() { Share = ByteString.CopyFrom(exchange.InitialMessage) }, ct).ConfigureAwait(false);
                await call.RequestStream.WriteAsync(new() { Confirmation = ByteString.CopyFrom(confirmation) }, ct).ConfigureAwait(false);
                var peerConfirmation = await PeerPairingRpcService.Read(call.ResponseStream, PeerPairingFrame.FrameOneofCase.Confirmation, ct).ConfigureAwait(false);
                if (peerConfirmation.Confirmation.Length != 32) throw new ArgumentException(); exchange.ConfirmPeer(peerConfirmation.Confirmation.ToByteArray(), ct);
                var binding = exchange.CreateIdentityBinding(runtime.HostId, runtime.PublicCredential, ct);
                var received = await PeerPairingRpcService.Read(call.ResponseStream, PeerPairingFrame.FrameOneofCase.Binding, ct).ConfigureAwait(false);
                if (received.Binding.Length is 0 or > 1200) throw new ArgumentException();
                var peer = exchange.VerifyIdentityBinding(received.Binding.ToByteArray(), ct);
                ct.ThrowIfCancellationRequested(); var local = runtime.Store(peer, connection.Identity);
                stored = true;
                await call.RequestStream.WriteAsync(new() { Binding = ByteString.CopyFrom(binding) }, ct).ConfigureAwait(false);
                await call.RequestStream.CompleteAsync().ConfigureAwait(false);
                var result = (await PeerPairingRpcService.Read(call.ResponseStream, PeerPairingFrame.FrameOneofCase.Result, ct).ConfigureAwait(false)).Result;
                if (result is not (PeerPairingResult.PeerBound or PeerPairingResult.Resumed or PeerPairingResult.Reconfirmed or PeerPairingResult.ReplacementRequired or PeerPairingResult.RecoveryRequired))
                    throw new AuthenticationException("Pairing result refused.");
                if (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false)) throw new ArgumentException("Unexpected trailing pairing frame.");
                return new(local, result);
            }
        }
        finally { if (!stored) runtime.Failed(attemptId); }
    }
}
