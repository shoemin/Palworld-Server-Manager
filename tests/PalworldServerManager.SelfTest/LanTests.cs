using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Services;
using PalworldServerManager.Lan;

namespace PalworldServerManager.SelfTest;

internal static class LanTests
{
    // ---- Pairing code lifecycle -------------------------------------------------

    public static Task TestPairingCodeIsSixDigitsAndOneUse()
    {
        var pairing = new PairingService();
        var (code, expiresUtc) = pairing.GenerateCode();

        Equal(6, code.Length);
        True(code.All(char.IsDigit), "pairing code must be numeric");
        True(expiresUtc > DateTime.UtcNow.AddMinutes(4) && expiresUtc <= DateTime.UtcNow.AddMinutes(5), "pairing code should expire ~5 minutes from now");

        True(pairing.Validate(code), "correct code should validate");
        True(!pairing.Validate(code), "pairing codes must be one-use");
        return Task.CompletedTask;
    }

    public static Task TestPairingWrongCodeDoesNotConsumeTheRealCode()
    {
        var pairing = new PairingService();
        var (code, _) = pairing.GenerateCode();
        var wrong = code == "000000" ? "111111" : "000000";

        True(!pairing.Validate(wrong), "wrong code must not validate");
        True(pairing.Validate(code), "a single wrong attempt must not burn the real code");
        return Task.CompletedTask;
    }

    public static Task TestPairingFailedAttemptsAreBoundedAndLockOutTheCode()
    {
        var pairing = new PairingService();
        var (code, _) = pairing.GenerateCode();
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 10; i++)
            pairing.Validate(wrong);

        True(!pairing.Validate(code), "code must be invalidated after the bounded failed-attempt limit is reached");
        return Task.CompletedTask;
    }

    // ---- Trusted-peer / token storage ---------------------------------------------

    public static async Task TestLanDisabledByDefaultForANewState()
    {
        await WithTempPaths(paths =>
        {
            var state = new LanStateStore(paths);
            True(!state.Enabled, "LAN must be disabled by default for a freshly created Manager state");
            Equal(LanProtocol.DefaultApiPort, state.ApiPort);
            Equal(LanProtocol.DefaultDiscoveryPort, state.DiscoveryPort);
            return Task.CompletedTask;
        });
    }

    public static async Task TestTrustedPeerTokenIsHashedAtRestAndAuthorizesOnlyUntilRevoked()
    {
        await WithTempPaths(async paths =>
        {
            var state = new LanStateStore(paths);
            var peerId = Guid.NewGuid();
            var token = state.AddInboundTrust(peerId, "REMOTE-PC");

            True(state.IsAuthorizedToken(token), "freshly issued inbound token must authorize");
            True(!state.IsAuthorizedToken("not-the-real-token"), "an unrelated token must not authorize");
            True(!state.IsAuthorizedToken(""), "an empty token must not authorize");

            var raw = await File.ReadAllTextAsync(Path.Combine(paths.LanRoot, "lan-state.json"));
            True(!raw.Contains(token, StringComparison.Ordinal), "the inbound bearer token must not be persisted in plaintext");

            state.RemovePeerTrust(peerId);
            True(!state.IsAuthorizedToken(token), "an unpaired peer's token must no longer authorize");
        });
    }

    public static async Task TestRemoteCredentialPersistsAcrossReload()
    {
        await WithTempPaths(paths =>
        {
            var peerId = Guid.NewGuid();
            var state = new LanStateStore(paths);
            state.SaveRemoteCredential(peerId, "REMOTE-PC", "outbound-token-abc");
            True(state.IsRemotePaired(peerId), "peer should be marked paired after saving a remote credential");

            var reloaded = new LanStateStore(paths);
            Equal("outbound-token-abc", reloaded.GetRemoteToken(peerId));
            True(reloaded.IsRemotePaired(peerId), "remote credential must survive a Manager restart");
            return Task.CompletedTask;
        });
    }

    // ---- Discovery protocol ---------------------------------------------------------

    public static Task TestDiscoveryAdvertisementCarriesNoSecrets()
    {
        var ad = new LanAdvertisement
        {
            InstanceId = Guid.NewGuid(),
            MachineName = "FRANKY",
            ApiPort = LanProtocol.DefaultApiPort,
            ManagerVersion = "0.3.0"
        };
        var json = JsonSerializer.Serialize(ad);
        True(!ContainsSensitiveWord(json), "LAN discovery advertisement must never contain secret-shaped fields: " + json);
        return Task.CompletedTask;
    }

    public static Task TestDiscoveryFiltersUnknownProtocolAndSelfAdvertisements()
    {
        var selfId = Guid.NewGuid();
        var other = Guid.NewGuid();

        True(!PeerDiscoveryService.IsAcceptableAdvertisement(null, selfId), "a null/undeserializable advertisement must be rejected");
        True(!PeerDiscoveryService.IsAcceptableAdvertisement(new LanAdvertisement { Protocol = "SomethingElse", InstanceId = other }, selfId), "an advertisement with the wrong protocol name must be rejected");
        True(!PeerDiscoveryService.IsAcceptableAdvertisement(new LanAdvertisement { ProtocolVersion = LanProtocol.ProtocolVersion + 1, InstanceId = other }, selfId), "an advertisement with an incompatible protocol version must be rejected");
        True(!PeerDiscoveryService.IsAcceptableAdvertisement(new LanAdvertisement { InstanceId = selfId }, selfId), "a Manager must not discover itself as a peer");
        True(PeerDiscoveryService.IsAcceptableAdvertisement(new LanAdvertisement { InstanceId = other }, selfId), "a well-formed advertisement from another instance must be accepted");
        return Task.CompletedTask;
    }

    // ---- End-to-end LAN API over loopback --------------------------------------------

    public static async Task TestLanHostRejectsUnauthenticatedAndWrongTokenRequests()
    {
        await WithHost(async (host, port, _, _, _) =>
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            using var identity = await http.GetAsync("/api/v1/identity");
            True(identity.IsSuccessStatusCode, "the identity endpoint must be reachable without authorization to support discovery/hello");
            var identityBody = await identity.Content.ReadAsStringAsync();
            True(!ContainsSensitiveWord(identityBody), "identity endpoint leaked a secret-shaped field: " + identityBody);

            using var noAuth = await http.GetAsync("/api/v1/servers");
            Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);

            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "totally-invalid-token");
            using var wrongAuth = await http.GetAsync("/api/v1/servers");
            Equal(HttpStatusCode.Unauthorized, wrongAuth.StatusCode);
        });
    }

    public static async Task TestLanPairingGrantsAuthorizedAccessAndRejectsWrongCode()
    {
        await WithHost(async (host, port, paths, state, pairing) =>
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            var (code, _) = pairing.GenerateCode();
            var wrongCode = code == "000000" ? "111111" : "000000";

            using var badPair = await http.PostAsJsonAsync("/api/v1/pair", new PairRequest
            {
                InstanceId = Guid.NewGuid(),
                MachineName = "ATTACKER-PC",
                Code = wrongCode,
                ReciprocalToken = "irrelevant"
            });
            Equal(HttpStatusCode.Unauthorized, badPair.StatusCode);

            using var goodPair = await http.PostAsJsonAsync("/api/v1/pair", new PairRequest
            {
                InstanceId = Guid.NewGuid(),
                MachineName = "REMOTE-PC",
                Code = code,
                ReciprocalToken = "reciprocal-token-abc"
            });
            True(goodPair.IsSuccessStatusCode, "pairing with the correct one-time code must succeed");
            var pairResponse = await goodPair.Content.ReadFromJsonAsync<PairResponse>();
            True(pairResponse is not null && !string.IsNullOrWhiteSpace(pairResponse.Token), "a successful pairing must return a bearer token");

            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pairResponse!.Token);
            using var servers = await http.GetAsync("/api/v1/servers");
            True(servers.IsSuccessStatusCode, "an authorized request after pairing must succeed");

            // The code is one-use; a second pairing attempt with the same code must now fail.
            using var reuse = await http.PostAsJsonAsync("/api/v1/pair", new PairRequest
            {
                InstanceId = Guid.NewGuid(),
                MachineName = "REMOTE-PC-2",
                Code = code,
                ReciprocalToken = "another-token"
            });
            Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        });
    }

    public static async Task TestLanTransferOfferRejectsMalformedMetadata()
    {
        await WithHost(async (host, port, paths, state, pairing) =>
        {
            using var http = AuthorizedClient(port, state);

            using var badHash = await http.PostAsJsonAsync("/api/v1/transfers/offers", new TransferOfferRequest
            {
                SourceInstanceId = Guid.NewGuid(),
                SourceMachine = "SENDER-PC",
                ServerName = "Test Server",
                SizeBytes = 1024,
                Sha256 = "not-a-valid-hash"
            });
            Equal(HttpStatusCode.BadRequest, badHash.StatusCode);

            using var badSize = await http.PostAsJsonAsync("/api/v1/transfers/offers", new TransferOfferRequest
            {
                SourceInstanceId = Guid.NewGuid(),
                SourceMachine = "SENDER-PC",
                ServerName = "Test Server",
                SizeBytes = 0,
                Sha256 = new string('a', 64)
            });
            Equal(HttpStatusCode.BadRequest, badSize.StatusCode);
        });
    }

    public static async Task TestLanTransferCompletesAndVerifiesWholeFileHash()
    {
        await WithHost(async (host, port, paths, state, pairing) =>
        {
            using var http = AuthorizedClient(port, state);
            var payload = new byte[1024 * 200];
            RandomNumberGenerator.Fill(payload);
            var hash = Convert.ToHexString(SHA256.HashData(payload));

            using var offerResponse = await http.PostAsJsonAsync("/api/v1/transfers/offers", new TransferOfferRequest
            {
                SourceInstanceId = Guid.NewGuid(),
                SourceMachine = "SENDER-PC",
                ServerName = "Send Test Server",
                SizeBytes = payload.Length,
                Sha256 = hash
            });
            True(offerResponse.IsSuccessStatusCode, "a well-formed transfer offer must be accepted");
            var offer = await offerResponse.Content.ReadFromJsonAsync<TransferOfferResponse>();
            True(offer is not null, "offer response must deserialize");
            Equal(LanTransferStatus.Pending, offer!.Status);

            True(host.AcceptOffer(offer.OfferId), "the receiver must be able to accept a pending offer");

            using var uploadContent = new ByteArrayContent(payload);
            using var uploadResponse = await http.PostAsync($"/api/v1/transfers/{offer.OfferId:D}/content", uploadContent);
            True(uploadResponse.IsSuccessStatusCode, "a byte-for-byte matching upload must succeed: " + await uploadResponse.Content.ReadAsStringAsync());

            using var status = await http.GetAsync($"/api/v1/transfers/{offer.OfferId:D}/status");
            var statusBody = await status.Content.ReadFromJsonAsync<TransferStatusResponse>();
            Equal(LanTransferStatus.Received, statusBody!.Status);

            var finalized = host.GetOffers().Single(x => x.OfferId == offer.OfferId);
            True(finalized.ReceivedPath is not null && File.Exists(finalized.ReceivedPath), "the finalized .palserver file must exist");
            True(!finalized.ReceivedPath!.EndsWith(".partial", StringComparison.OrdinalIgnoreCase), "a finalized transfer must not still be a .partial file");
            var receivedBytes = await File.ReadAllBytesAsync(finalized.ReceivedPath);
            True(receivedBytes.SequenceEqual(payload), "received file bytes must exactly match the sent payload");
            True(!Directory.EnumerateFiles(paths.IncomingRoot, "*.partial").Any(), "no .partial file should remain after a successful transfer");
        });
    }

    public static async Task TestLanTransferHashMismatchIsRejectedAndLeavesNoPartialFile()
    {
        await WithHost(async (host, port, paths, state, pairing) =>
        {
            using var http = AuthorizedClient(port, state);
            var realPayload = new byte[1024 * 50];
            RandomNumberGenerator.Fill(realPayload);
            var claimedHash = Convert.ToHexString(SHA256.HashData(realPayload));

            using var offerResponse = await http.PostAsJsonAsync("/api/v1/transfers/offers", new TransferOfferRequest
            {
                SourceInstanceId = Guid.NewGuid(),
                SourceMachine = "SENDER-PC",
                ServerName = "Corrupt Test Server",
                SizeBytes = realPayload.Length,
                Sha256 = claimedHash
            });
            var offer = await offerResponse.Content.ReadFromJsonAsync<TransferOfferResponse>();
            True(host.AcceptOffer(offer!.OfferId), "offer must be acceptable before upload");

            var corrupted = (byte[])realPayload.Clone();
            corrupted[0] ^= 0xFF;
            using var uploadContent = new ByteArrayContent(corrupted);
            using var uploadResponse = await http.PostAsync($"/api/v1/transfers/{offer.OfferId:D}/content", uploadContent);
            True(!uploadResponse.IsSuccessStatusCode, "a payload that does not match the declared SHA-256 must be rejected");

            var finalized = host.GetOffers().Single(x => x.OfferId == offer.OfferId);
            Equal(LanTransferStatus.Failed, finalized.Status);
            True(finalized.ReceivedPath is null, "a hash-mismatched transfer must never be finalized as a usable package");
            True(!Directory.EnumerateFiles(paths.IncomingRoot, "*.partial").Any(), "a failed transfer must not leave a .partial file behind");
            True(!Directory.EnumerateFiles(paths.IncomingRoot, "*.palserver").Any(), "a failed transfer must not produce a .palserver file");
        });
    }

    // ---- helpers ------------------------------------------------------------------

    private static HttpClient AuthorizedClient(int port, LanStateStore state)
    {
        var peerId = Guid.NewGuid();
        var token = state.AddInboundTrust(peerId, "SELF-TEST-CLIENT");
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static bool ContainsSensitiveWord(string text)
        => text.Contains("password", StringComparison.OrdinalIgnoreCase)
           || text.Contains("token", StringComparison.OrdinalIgnoreCase)
           || text.Contains("secret", StringComparison.OrdinalIgnoreCase)
           || text.Contains("credential", StringComparison.OrdinalIgnoreCase);

    private static async Task WithTempPaths(Func<AppPaths, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "psm-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureCreated();
            await body(paths);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static async Task WithHost(Func<ManagerLanHost, int, AppPaths, LanStateStore, PairingService, Task> body)
    {
        await WithTempPaths(async paths =>
        {
            var logger = new FileLogger(paths);
            var registry = new ProfileRegistry(paths, logger);
            var settings = new PalworldSettingsService(logger);
            var rest = new PalworldRestClient(logger);
            var processes = new ServerProcessService(settings, rest, logger);
            var dashboard = new DashboardService(paths, settings, rest, processes, logger);
            var state = new LanStateStore(paths);
            var pairing = new PairingService();

            await using var host = new ManagerLanHost(paths, registry, dashboard, processes, state, pairing, logger);
            var port = GetFreeTcpPort();
            await host.StartAsync(port);
            try
            {
                await body(host, port, paths, state, pairing);
            }
            finally
            {
                await host.DisposeAsync();
            }
        });
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected '{expected}', got '{actual}'.");
    }
}
