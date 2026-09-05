using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

// #19/#42 required standalone transport spike. TEST ONLY: no Host authority, tickets or RPCs.
// Uses a fresh test certificate and unique pipe, never the installed product endpoint.
// Windows Schannel requires a native key container; this test does not select production storage.
public sealed class LocalIpcSpike : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly X509Certificate2 _certificate;
    private readonly string _nativeKeyName;
    private readonly CngProvider _nativeProvider;
    public string PipeName { get; }
    public string PublicPin { get; }
    public ConcurrentBag<string> ObservedSids { get; } = [];
    private LocalIpcSpike(WebApplication application, X509Certificate2 certificate, string pipeName)
    {
        _application = application; _certificate = certificate; PipeName = pipeName;
        PublicPin = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        using var signingKey = certificate.GetECDsaPrivateKey();
        var cng = signingKey as ECDsaCng ?? throw new Exception("Probe needs a tracked Windows CNG key.");
        _nativeKeyName = cng.Key.KeyName ?? throw new Exception("Probe key container was not named.");
        _nativeProvider = cng.Key.Provider!;
    }

    public static async Task<LocalIpcSpike> StartAsync(SecurityIdentifier group)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var pipeName = "PSMAstraIpcProbe" + Guid.NewGuid().ToString("N");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var pfx = generated.Export(X509ContentType.Pfx);
        X509Certificate2 certificate;
        try { certificate = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.DefaultKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders(); // Request content, keys and bearer values must not enter logs.
        builder.WebHost.UseNamedPipes(options =>
        {
            options.CurrentUserOnly = false;
            var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }.Distinct())
                acl.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new PipeAccessRule(group, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            options.PipeSecurity = acl;
        });
        builder.WebHost.ConfigureKestrel(options => options.ListenNamedPipe(pipeName, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseHttps(certificate, https => https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13);
        }));
        var application = builder.Build();
        var spike = new LocalIpcSpike(application, certificate, pipeName);
        application.MapPost("/probe", async context =>
        {
            Check(context.Request.IsHttps && context.Request.Protocol == "HTTP/2", "Probe lacked HTTP/2 TLS.");
            var feature = context.Features.Get<IConnectionNamedPipeFeature>() ?? throw new Exception("Native named-pipe feature unavailable.");
            string? sid = null;
            feature.NamedPipe.RunAsClient(() =>
            {
                using var peer = WindowsIdentity.GetCurrent(true);
                sid = peer?.User?.Value;
            }); // Identification only; no filesystem or authority effects while impersonating.
            Check(!string.IsNullOrWhiteSpace(sid), "Native peer SID unavailable.");
            spike.ObservedSids.Add(sid!);
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(sid!);
        });
        try { await application.StartAsync(); return spike; }
        catch { await spike.DisposeAsync(); throw; }
    }

    public static async Task<string> RequestAsync(string pipeName, string pin)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null &&
                    CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), Convert.FromHexString(pin))
            },
            ConnectCallback = async (_, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                try { await pipe.ConnectAsync(ct); return pipe; }
                catch { pipe.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost/probe")
        {
            Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("SYNTHETIC-BOOTSTRAP-NOT-A-REAL-SECRET")
        };
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
    public static async Task RejectWrongPinAsync(string pipeName)
    {
        try { await RequestAsync(pipeName, new string('0', 64)); }
        catch (HttpRequestException ex) when (HasCause<AuthenticationException>(ex)) { return; }
        throw new Exception("Untrusted local TLS endpoint was not rejected as authentication failure.");
    }
    private static bool HasCause<T>(Exception exception) where T : Exception
        => exception is T || exception.InnerException is { } inner && HasCause<T>(inner);

    public static async Task RejectTransportAsync(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await pipe.ConnectAsync(timeout.Token); }
        catch (UnauthorizedAccessException) { return; }
        throw new Exception("Nonmember connected to the restricted pipe.");
    }
    public static async Task LocalProof()
    {
        using var identity = WindowsIdentity.GetCurrent();
        await using var spike = await StartAsync(identity.User!);
        Check(await RequestAsync(spike.PipeName, spike.PublicPin) == identity.User!.Value, "Native SID mismatched actual caller.");
        await RejectWrongPinAsync(spike.PipeName);
        Check(spike.ObservedSids.Count == 1, "Sensitive request reached untrusted endpoint handler.");
    }
    public async ValueTask DisposeAsync()
    {
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await _application.StopAsync(timeout.Token); }
        finally
        {
            try { await _application.DisposeAsync(); }
            finally
            {
                _certificate.Dispose();
                if (CngKey.Exists(_nativeKeyName, _nativeProvider))
                { using var key = CngKey.Open(_nativeKeyName, _nativeProvider); key.Delete(); }
                Check(!CngKey.Exists(_nativeKeyName, _nativeProvider), "Probe native key cleanup failed.");
            }
        }
    }
}
