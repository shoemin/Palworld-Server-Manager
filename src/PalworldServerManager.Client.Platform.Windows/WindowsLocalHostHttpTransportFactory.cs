using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using PalworldServerManager.Client.Platform.Contracts;

namespace PalworldServerManager.Client.Platform.Windows;

public sealed class WindowsLocalHostHttpTransportFactory : ILocalHostHttpTransportFactory
{
    private readonly ILocalHostTrustReader _reader;
    private readonly string _pipeName;
    public WindowsLocalHostHttpTransportFactory(ILocalHostTrustReader reader, string pipeName)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        if (string.IsNullOrEmpty(pipeName) || pipeName.Length > 128 || pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException("A bounded local pipe name is required.");
        _pipeName = pipeName;
    }
    public HttpMessageHandler CreateHandler(Guid expectedHostId)
    {
        if (expectedHostId == Guid.Empty) throw new ArgumentException("Expected Host identity is required.");
        var attempts = new ConditionalWeakTable<HttpRequestMessage, ConnectionAttempt>();
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                AllowRenegotiation = false,
                AllowTlsResume = false,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    // TLS invokes this once for each new connection. No shared mutable pin snapshot.
                    // WindowsLocalHostTrustReader uses ConfigureAwait(false) for this synchronous API.
                    var trust = _reader.ReadAsync().GetAwaiter().GetResult();
                    if (trust.HostId != expectedHostId) throw new LocalHostAuthenticationException("The local Host identity does not match.");
                    return Matches(trust, certificate);
                }
            },
            ConnectCallback = async (context, ct) =>
            {
                ValidateRequest(context.InitialRequestMessage);
                var attempt = attempts.GetOrCreateValue(context.InitialRequestMessage);
                attempt.Connected = false; attempt.TlsComplete = false;
                var beforeConnect = await _reader.ReadAsync(ct).ConfigureAwait(false);
                if (beforeConnect.HostId != expectedHostId) throw new LocalHostAuthenticationException("The local Host identity does not match.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                try { await pipe.ConnectAsync(linked.Token).ConfigureAwait(false); attempt.Connected = true; return pipe; }
                catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                { pipe.Dispose(); throw new LocalHostEndpointUnavailableException("The local Host endpoint is unavailable.", ex); }
                catch { pipe.Dispose(); throw; }
            },
            PlaintextStreamFilter = (context, _) =>
            {
                // Invoked by HttpClient only after its standard TLS handshake has completed.
                attempts.GetOrCreateValue(context.InitialRequestMessage).TlsComplete = true;
                return ValueTask.FromResult(context.PlaintextStream);
            }
        };
        return new AuthenticationErrors(handler, attempts);
    }
    private sealed class ConnectionAttempt { internal volatile bool Connected; internal volatile bool TlsComplete; }
    private static void ValidateRequest(HttpRequestMessage request)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } uri || uri.Scheme != "https" || uri.Host != "localhost" || uri.Port != 443 ||
            request.Version != HttpVersion.Version20 || request.VersionPolicy != HttpVersionPolicy.RequestVersionExact)
            throw new ArgumentException("Local transport requests must use exact HTTP/2 at https://localhost.");
    }
    private sealed class AuthenticationErrors(HttpMessageHandler inner, ConditionalWeakTable<HttpRequestMessage, ConnectionAttempt> attempts) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ValidateRequest(request); // Also enforce this for requests using an existing pooled connection.
            bool InHandshake() => attempts.TryGetValue(request, out var attempt) && attempt.Connected && !attempt.TlsComplete;
            try { return await base.SendAsync(request, ct).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && InHandshake())
            { throw new HttpRequestException("The local TLS endpoint did not complete authentication.", new LocalHostAuthenticationException("Local TLS authentication timed out.", ex)); }
            catch (HttpRequestException ex) when (IsAuthentication(ex) || ex.HttpRequestError == HttpRequestError.SecureConnectionError || InHandshake())
            { throw new HttpRequestException("The local TLS endpoint could not be authenticated.", new LocalHostAuthenticationException("Local Host authentication failed.", ex)); }
        }
        private static bool IsAuthentication(Exception ex) => ex is AuthenticationException || (ex.InnerException is { } inner && IsAuthentication(inner));
    }
    private static bool Matches(LocalHostTrustAnchor trust, X509Certificate? certificate)
    {
        if (certificate is null) return false;
        try
        {
            using var publicCertificate = new X509Certificate2(certificate);
            using var key = publicCertificate.GetECDsaPublicKey();
            return key is not null && key.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7" &&
                trust.AcceptsPublicKey(key.ExportSubjectPublicKeyInfo());
        }
        catch (CryptographicException) { return false; }
    }
}
