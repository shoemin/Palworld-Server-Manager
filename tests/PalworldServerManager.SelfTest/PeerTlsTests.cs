using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PalworldServerManager.Host;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class PeerTlsTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Peer TLS assertion failed."); }
    private static void Reject(Action action)
    { try { action(); } catch (AuthenticationException) { return; } throw new Exception("Expected peer authentication refusal."); }
    private sealed class Certificate : IDisposable
    {
        internal readonly X509Certificate2 Value = LocalIpcSpike.CreateTestCertificate();
        private readonly string name;
        private readonly CngProvider provider;
        internal Certificate()
        { using var key = (ECDsaCng)Value.GetECDsaPrivateKey()!; name = key.Key.KeyName!; provider = key.Key.Provider!; }
        public void Dispose()
        {
            Value.Dispose();
            if (CngKey.Exists(name, provider)) { using var key = CngKey.Open(name, provider); key.Delete(); }
            Check(!CngKey.Exists(name, provider));
        }
    }
    private static async Task Exchange(X509Certificate2 server, X509Certificate2 client,
        Func<string, bool> serverAccepts, Func<string, bool> clientAccepts, bool success,
        Action<SslClientAuthenticationOptions>? tamperClient = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        using var outgoing = new TcpClient(); await outgoing.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using var incoming = await accept;
        var serverTask = WindowsPeerTls.AuthenticateServerAsync(incoming.GetStream(), server, serverAccepts, deadline.Token);
        var clientTask = ConnectClient();
        async Task<SslStream> ConnectClient()
        {
            if (tamperClient is null) return await WindowsPeerTls.AuthenticateClientAsync(outgoing.GetStream(), client, clientAccepts, deadline.Token);
            var options = WindowsPeerTls.ClientOptions(client, clientAccepts); tamperClient(options);
            var stream = new SslStream(outgoing.GetStream());
            try { await stream.AuthenticateAsClientAsync(options, deadline.Token); return stream; }
            catch { await stream.DisposeAsync(); throw; }
        }
        try
        {
            try { await Task.WhenAll(serverTask, clientTask); }
            catch (Exception ex) when (!success && ex is AuthenticationException or IOException) { return; }
            Check(success); var serverStream = await serverTask; var clientStream = await clientTask;
            Check(serverStream.IsMutuallyAuthenticated && clientStream.IsMutuallyAuthenticated);
            Check(serverStream.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2);
            using var seenByServer = new X509Certificate2(serverStream.RemoteCertificate!);
            using var seenByClient = new X509Certificate2(clientStream.RemoteCertificate!);
            Check(!seenByServer.HasPrivateKey && !seenByClient.HasPrivateKey);
            Check(WindowsPeerTls.PublicFingerprint(seenByServer) == WindowsPeerTls.PublicFingerprint(client));
            await clientStream.WriteAsync(new byte[] { 7, 8, 9 }, deadline.Token);
            var received = new byte[3]; await serverStream.ReadExactlyAsync(received, deadline.Token);
            Check(received.SequenceEqual(new byte[] { 7, 8, 9 }));
        }
        finally
        {
            if (serverTask.IsCompletedSuccessfully) await serverTask.Result.DisposeAsync();
            if (clientTask.IsCompletedSuccessfully) await clientTask.Result.DisposeAsync();
        }
    }
    public static async Task MutualProof()
    {
        using var server = new Certificate(); using var client = new Certificate();
        var sp = WindowsPeerTls.PublicFingerprint(server.Value); var cp = WindowsPeerTls.PublicFingerprint(client.Value);
        await Exchange(server.Value, client.Value, pin => pin == cp, pin => pin == sp, true);
        Check(!WindowsPeerTls.ClientOptions(client.Value, _ => true).AllowTlsResume);
        Check(!WindowsPeerTls.ServerOptions(server.Value, _ => true).AllowRenegotiation);
    }
    public static async Task RefusalsAndCleanup()
    {
        using var server = new Certificate(); using var client = new Certificate();
        var sp = WindowsPeerTls.PublicFingerprint(server.Value); var cp = WindowsPeerTls.PublicFingerprint(client.Value);
        await Exchange(server.Value, client.Value, _ => false, pin => pin == sp, false);
        await Exchange(server.Value, client.Value, pin => pin == cp, _ => false, false);
        await Exchange(server.Value, client.Value, _ => throw new Exception("Pin resolver failed"), pin => pin == sp, false);
        await Exchange(server.Value, client.Value, pin => pin == cp, pin => pin == sp, false, options => options.ClientCertificates!.Clear());
        await Exchange(server.Value, client.Value, pin => pin == cp, pin => pin == sp, false, options => options.ApplicationProtocols = []);
        using var publicOnly = new X509Certificate2(client.Value.RawData);
        Reject(() => WindowsPeerTls.ClientOptions(publicOnly, _ => true));
        using var expiredKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var expired = new CertificateRequest("CN=expired", expiredKey, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1));
        Reject(() => WindowsPeerTls.ServerOptions(expired, _ => true));
        using var wrongCurveKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var wrongCurve = new CertificateRequest("CN=wrong-curve", wrongCurveKey, HashAlgorithmName.SHA384)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        Reject(() => WindowsPeerTls.ClientOptions(wrongCurve, _ => true));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var ownedStream = new MemoryStream();
        try { await WindowsPeerTls.AuthenticateServerAsync(ownedStream, server.Value, _ => true, cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        Check(!ownedStream.CanRead);
    }
    public static Task TrustPurposes()
    {
        using var f = new PeerTrustTests.Fixture();
        var local = new string('A', 64); var peer = new string('B', 64); var pending = new string('C', 64);
        f.Repository.RecordVerifiedBinding(f.PeerId, peer, local);
        var auth = new PeerTransportAuthentication(f.Repository, f.Time);
        Check(auth.Authenticate(f.PeerId, peer, PeerTrafficPurpose.PairingFinalization).TrustState == "PeerBound");
        Reject(() => auth.Authenticate(f.PeerId, peer, PeerTrafficPurpose.OrdinaryManagement));
        Reject(() => auth.Authenticate(f.PeerId, peer, (PeerTrafficPurpose)0));
        Reject(() => auth.Authenticate(Guid.NewGuid(), peer, PeerTrafficPurpose.PairingFinalization));
        Reject(() => auth.Authenticate(f.PeerId, pending, PeerTrafficPurpose.PairingFinalization));
        f.Time.Now += TimeSpan.FromMinutes(30);
        Reject(() => auth.Authenticate(f.PeerId, peer, PeerTrafficPurpose.PairingFinalization));
        // Fixture represents a previously completed activation/staging, not a production bypass.
        f.Execute($"UPDATE TrustedManagers SET State='Active',PendingTrustedPublicKeyFingerprint='{pending}',PendingReconfirmationRequired=1 WHERE PeerHostId='{f.PeerId:D}';");
        Check(auth.Authenticate(f.PeerId, peer, PeerTrafficPurpose.OrdinaryManagement).TrustState == "Active");
        Check(auth.Authenticate(f.PeerId, pending, PeerTrafficPurpose.OrdinaryManagement).UsesPendingCredential);
        Check(f.Count("HostCapabilityGrants") == 0); // Transport authentication manufactured no authority.
        f.Execute($"UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='{f.PeerId:D}';");
        Reject(() => auth.Authenticate(f.PeerId, peer, PeerTrafficPurpose.OrdinaryManagement));
        Reject(() => auth.Authenticate(f.PeerId, pending, PeerTrafficPurpose.PairingFinalization));
        f.Execute($"UPDATE TrustedManagers SET State='Revoked',PeerRecoveryRequired=0,CurrentTrustedPublicKeyFingerprint=NULL,PendingTrustedPublicKeyFingerprint=NULL,PendingReconfirmationRequired=0 WHERE PeerHostId='{f.PeerId:D}';");
        Reject(() => auth.Authenticate(f.PeerId, peer, PeerTrafficPurpose.OrdinaryManagement));
        return Task.CompletedTask;
    }
}
