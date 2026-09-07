using System.Buffers.Binary;
using System.Net;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.SelfTest;

// Read fault AFTER the real challenge, confirmation and identity binding frames.
// TLS and native pairing are unchanged; no cryptographic result is synthesized.
internal sealed class PairingLostResultTransport(IPeerHttpTransportFactory inner) : IPeerHttpTransportFactory
{
    internal bool ResultReadFailed;
    public IPeerHttpTransport Create(Func<string, bool> acceptsServerPin, Action<PeerTlsConnectionIdentity>? observed = null)
        => new Connection(inner.Create(acceptsServerPin, observed), this);
    private sealed class Connection : IPeerHttpTransport
    {
        private readonly IPeerHttpTransport inner;
        public HttpMessageHandler Handler { get; }
        public PeerTlsConnectionIdentity Identity => inner.Identity;
        internal Connection(IPeerHttpTransport inner, PairingLostResultTransport owner) { this.inner = inner; Handler = new HandlerWithFault(inner.Handler, owner); }
        public void Dispose() { Handler.Dispose(); inner.Dispose(); }
    }
    private sealed class HandlerWithFault(HttpMessageHandler inner, PairingLostResultTransport owner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct); response.Content = new Content(response.Content, owner); return response;
        }
    }
    private sealed class Content : HttpContent
    {
        private readonly HttpContent original; private readonly PairingLostResultTransport owner;
        internal Content(HttpContent original, PairingLostResultTransport owner)
        { this.original = original; this.owner = owner; foreach (var header in original.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value); }
        protected override async Task<Stream> CreateContentReadStreamAsync() => new CutStream(await original.ReadAsStreamAsync(), owner);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => new CutStream(await original.ReadAsStreamAsync(ct), owner);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) original.Dispose(); base.Dispose(disposing); }
    }
    private sealed class CutStream(Stream inner, PairingLostResultTransport owner) : Stream
    {
        private readonly byte[] header = new byte[5]; private int headerCount, remaining, frames;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.IsEmpty) return 0;
            if (frames == 3) { owner.ResultReadFailed = true; throw new IOException("Fixture pairing result read lost."); }
            var count = await inner.ReadAsync(buffer[..1], ct); if (count == 0) return 0;
            if (headerCount < 5)
            {
                header[headerCount++] = buffer.Span[0];
                if (headerCount == 5)
                {
                    remaining = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                    if (header[0] != 0 || remaining is < 0 or > 4096) throw new InvalidDataException("Unexpected fixture framing.");
                    if (remaining == 0) { frames++; headerCount = 0; }
                }
            }
            else if (--remaining == 0) { frames++; headerCount = 0; }
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
