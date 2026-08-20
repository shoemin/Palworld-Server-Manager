using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Lan;

public sealed class ManagerLanClient
{
    private static readonly TimeSpan ShortCallTimeout = TimeSpan.FromSeconds(15);

    private readonly LanStateStore _state;
    private readonly IAppLogger _logger;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public ManagerLanClient(LanStateStore state, IAppLogger logger, HttpClient? httpClient = null)
    {
        _state = state;
        _logger = logger;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task PairAsync(LanPeer peer, string code, CancellationToken cancellationToken = default)
    {
        // Create the reciprocal token first so one successful pairing authorizes both Managers.
        var reciprocalToken = _state.AddInboundTrust(peer.InstanceId, peer.MachineName);
        try
        {
            using var response = await SendWithTimeoutAsync(
                ct => _http.PostAsJsonAsync(peer.BaseUri + "/api/v1/pair", new PairRequest
                {
                    InstanceId = _state.InstanceId,
                    MachineName = Environment.MachineName,
                    Code = code,
                    ReciprocalToken = reciprocalToken
                }, ct),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Pairing failed: {(int)response.StatusCode} {response.ReasonPhrase}");

            var pair = await response.Content.ReadFromJsonAsync<PairResponse>(_json, cancellationToken)
                ?? throw new InvalidDataException("Remote Manager returned an invalid pairing response.");

            if (pair.InstanceId != peer.InstanceId)
                throw new InvalidDataException("Remote Manager instance ID changed during pairing.");

            if (string.IsNullOrWhiteSpace(pair.Token))
                throw new InvalidDataException("Remote Manager returned an empty pairing credential.");

            _state.SaveRemoteCredential(peer.InstanceId, peer.MachineName, pair.Token);
            peer.IsPaired = true;
            _logger.Info($"Paired with LAN Manager '{peer.MachineName}' at {peer.Address}:{peer.ApiPort}.");
        }
        catch
        {
            // A failed pairing must not leave a half-trusted peer behind.
            _state.RemovePeerTrust(peer.InstanceId);
            throw;
        }
    }

    public async Task<IReadOnlyList<RemoteServerSummary>> GetServersAsync(LanPeer peer, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithTimeoutAsync(
            ct => _http.SendAsync(CreateAuthorized(peer, HttpMethod.Get, "/api/v1/servers"), ct),
            cancellationToken);
        await EnsureSuccessAsync(response, "Get remote server list", cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<RemoteServerSummary>>(_json, cancellationToken) ?? [];
    }

    public async Task<DashboardSnapshot> GetDashboardAsync(LanPeer peer, Guid profileId, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithTimeoutAsync(
            ct => _http.SendAsync(CreateAuthorized(peer, HttpMethod.Get, $"/api/v1/dashboard/{profileId:D}"), ct),
            cancellationToken);
        await EnsureSuccessAsync(response, "Get remote dashboard", cancellationToken);
        return await response.Content.ReadFromJsonAsync<DashboardSnapshot>(_json, cancellationToken)
            ?? throw new InvalidDataException("Remote dashboard response was empty.");
    }

    public async Task SendPackageAsync(
        LanPeer peer,
        string serverName,
        string packagePath,
        IProgress<string>? status = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(packagePath);
        if (!info.Exists) throw new FileNotFoundException("Portable package not found.", packagePath);

        status?.Report("Calculating transfer SHA-256...");
        await using var hashStream = info.OpenRead();
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));

        status?.Report("Sending transfer offer...");
        using var offerRequest = CreateAuthorized(peer, HttpMethod.Post, "/api/v1/transfers/offers");
        offerRequest.Content = JsonContent.Create(new TransferOfferRequest
        {
            SourceInstanceId = _state.InstanceId,
            SourceMachine = Environment.MachineName,
            ServerName = serverName,
            SizeBytes = info.Length,
            Sha256 = hash
        });

        using var offerResponse = await _http.SendAsync(offerRequest, cancellationToken);
        await EnsureSuccessAsync(offerResponse, "Create transfer offer", cancellationToken);
        var offer = await offerResponse.Content.ReadFromJsonAsync<TransferOfferResponse>(_json, cancellationToken)
            ?? throw new InvalidDataException("Remote Manager returned an invalid transfer offer response.");

        status?.Report($"Waiting for {peer.MachineName} to accept the transfer...");
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transferStatus = await GetTransferStatusAsync(peer, offer.OfferId, cancellationToken);
            switch (transferStatus.Status)
            {
                case LanTransferStatus.Accepted:
                    goto accepted;
                case LanTransferStatus.Rejected:
                    throw new InvalidOperationException("The destination rejected the server transfer.");
                case LanTransferStatus.Failed:
                    throw new InvalidOperationException("The destination reported a transfer failure: " + transferStatus.Error);
            }
            await Task.Delay(750, cancellationToken);
        }
        throw new TimeoutException("Timed out waiting for the destination to accept the transfer.");

    accepted:
        status?.Report("Transferring .palserver package...");
        await using var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        using var upload = CreateAuthorized(peer, HttpMethod.Post, $"/api/v1/transfers/{offer.OfferId:D}/content");
        upload.Content = new ProgressStreamContent(file, info.Length, progress);
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        upload.Content.Headers.ContentLength = info.Length;

        using var uploadResponse = await _http.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(uploadResponse, "Upload portable package", cancellationToken);
        progress?.Report(1.0);
        status?.Report("Transfer complete and verified by destination.");
        _logger.Info($"LAN package transfer completed. Server='{serverName}' Destination='{peer.MachineName}' Bytes={info.Length} SHA256={hash}");
    }

    private async Task<TransferStatusResponse> GetTransferStatusAsync(LanPeer peer, Guid offerId, CancellationToken cancellationToken)
    {
        using var response = await SendWithTimeoutAsync(
            ct => _http.SendAsync(CreateAuthorized(peer, HttpMethod.Get, $"/api/v1/transfers/{offerId:D}/status"), ct),
            cancellationToken);
        await EnsureSuccessAsync(response, "Get transfer status", cancellationToken);
        return await response.Content.ReadFromJsonAsync<TransferStatusResponse>(_json, cancellationToken)
            ?? throw new InvalidDataException("Remote Manager returned an invalid transfer status.");
    }

    private HttpRequestMessage CreateAuthorized(LanPeer peer, HttpMethod method, string path)
    {
        var token = _state.GetRemoteToken(peer.InstanceId)
            ?? throw new InvalidOperationException($"'{peer.MachineName}' is not paired with this Manager.");
        var request = new HttpRequestMessage(method, peer.BaseUri + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>Bounds an ordinary (non-transfer) LAN API call so an unreachable peer cannot hang the caller forever; large transfers intentionally bypass this and rely on caller-supplied cancellation instead.</summary>
    private static async Task<HttpResponseMessage> SendWithTimeoutAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ShortCallTimeout);
        try
        {
            return await send(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The remote Manager did not respond within {ShortCallTimeout.TotalSeconds:F0} seconds.");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
    }

    private sealed class ProgressStreamContent : HttpContent
    {
        private readonly Stream _source;
        private readonly long _length;
        private readonly IProgress<double>? _progress;

        public ProgressStreamContent(Stream source, long length, IProgress<double>? progress)
        {
            _source = source;
            _length = length;
            _progress = progress;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => CopyToAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => CopyToAsync(stream, cancellationToken);

        private new async Task CopyToAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[1024 * 1024];
            long sent = 0;
            int read;
            while ((read = await _source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sent += read;
                _progress?.Report(_length == 0 ? 1.0 : (double)sent / _length);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
