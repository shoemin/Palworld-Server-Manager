using System.Buffers.Binary;
using Google.Protobuf;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.SelfTest;

// Alters a real response only after the inner transport completed mutual TLS and the
// real server finished its reply. Never fabricates possession proof or connection identity.
internal sealed class PeerReplyFaultTransport<T>(IPeerHttpTransportFactory inner, string method, MessageParser<T> parser, Action<T> alter) : IPeerHttpTransportFactory where T : class, IMessage<T>
{
    internal int Altered;
    public IPeerHttpTransport Create(Func<string, bool> acceptsServerPin, Action<PeerTlsConnectionIdentity>? observed = null)
        => new Connection(inner.Create(acceptsServerPin, observed), this, method, parser, alter);
    private sealed class Connection : IPeerHttpTransport
    {
        private readonly IPeerHttpTransport inner;
        public PeerTlsConnectionIdentity Identity => inner.Identity;
        public HttpMessageHandler Handler { get; }
        internal Connection(IPeerHttpTransport inner, PeerReplyFaultTransport<T> owner, string method, MessageParser<T> parser, Action<T> alter)
        { this.inner = inner; Handler = new HandlerImpl(inner.Handler, owner, method, parser, alter); }
        public void Dispose() { Handler.Dispose(); inner.Dispose(); }
    }
    private sealed class HandlerImpl(HttpMessageHandler inner, PeerReplyFaultTransport<T> owner, string method, MessageParser<T> parser, Action<T> alter) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker invoker = new(inner, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await invoker.SendAsync(request, ct);
            if (!request.RequestUri!.AbsolutePath.EndsWith("/" + method, StringComparison.Ordinal)) return response;
            try
            {
                var content = response.Content; var bytes = await content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < 5 || bytes[0] != 0 || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4)) != bytes.Length - 5)
                    throw new InvalidDataException("Fixture expected one uncompressed gRPC response.");
                var reply = parser.ParseFrom(bytes, 5, bytes.Length - 5);
                Interlocked.Increment(ref owner.Altered); alter(reply);
                var payload = reply.ToByteArray(); var frame = new byte[payload.Length + 5];
                BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length); payload.CopyTo(frame, 5);
                var replacement = new ByteArrayContent(frame);
                foreach (var header in content.Headers)
                    if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content = replacement; content.Dispose(); return response;
            }
            catch { response.Dispose(); throw; }
        }
        protected override void Dispose(bool disposing) { if (disposing) invoker.Dispose(); base.Dispose(disposing); }
    }
}
