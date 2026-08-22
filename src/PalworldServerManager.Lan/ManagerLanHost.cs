using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Services;

namespace PalworldServerManager.Lan;

public sealed class ManagerLanHost : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly ProfileRegistry _registry;
    private readonly DashboardService _dashboard;
    private readonly ServerProcessService _processes;
    private readonly LanStateStore _state;
    private readonly PairingService _pairing;
    private readonly IAppLogger _logger;
    private readonly ICriticalOperationTracker _operations;
    private readonly ConcurrentDictionary<Guid, LanTransferOffer> _offers = new();
    private WebApplication? _app;

    public ManagerLanHost(
        AppPaths paths,
        ProfileRegistry registry,
        DashboardService dashboard,
        ServerProcessService processes,
        LanStateStore state,
        PairingService pairing,
        IAppLogger logger,
        ICriticalOperationTracker? operations = null)
    {
        _paths = paths;
        _registry = registry;
        _dashboard = dashboard;
        _processes = processes;
        _state = state;
        _pairing = pairing;
        _logger = logger;
        _operations = operations ?? new CriticalOperationTracker();
    }

    public event EventHandler? OffersChanged;
    public bool IsRunning => _app is not null;

    public IReadOnlyList<LanTransferOffer> GetOffers()
    {
        CleanupOffers();
        return _offers.Values.OrderByDescending(x => x.CreatedUtc).ToList();
    }

    public bool AcceptOffer(Guid offerId)
    {
        if (!_offers.TryGetValue(offerId, out var offer) || offer.Status != LanTransferStatus.Pending) return false;

        try
        {
            Directory.CreateDirectory(_paths.IncomingRoot);
            var root = Path.GetPathRoot(Path.GetFullPath(_paths.IncomingRoot));
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                const long reserveBytes = 256L * 1024 * 1024;
                if (drive.AvailableFreeSpace < offer.SizeBytes + reserveBytes)
                {
                    offer.Status = LanTransferStatus.Failed;
                    offer.Error = $"Not enough free disk space to receive this package. Required package bytes={offer.SizeBytes:N0}.";
                    OffersChanged?.Invoke(this, EventArgs.Empty);
                    _logger.Warning($"Rejected LAN transfer offer {offerId} because free disk space is insufficient.");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            offer.Status = LanTransferStatus.Failed;
            offer.Error = "Could not verify destination disk space: " + ex.Message;
            OffersChanged?.Invoke(this, EventArgs.Empty);
            _logger.Warning($"Could not accept LAN transfer offer {offerId}: {ex.Message}");
            return false;
        }

        offer.Status = LanTransferStatus.Accepted;
        OffersChanged?.Invoke(this, EventArgs.Empty);
        _logger.Info($"Accepted LAN transfer offer {offerId} for '{offer.ServerName}' from '{offer.SourceMachine}'.");
        return true;
    }

    public bool RejectOffer(Guid offerId)
    {
        if (!_offers.TryGetValue(offerId, out var offer) || offer.Status != LanTransferStatus.Pending) return false;
        offer.Status = LanTransferStatus.Rejected;
        OffersChanged?.Invoke(this, EventArgs.Empty);
        _logger.Info($"Rejected LAN transfer offer {offerId} for '{offer.ServerName}' from '{offer.SourceMachine}'.");
        return true;
    }

    public async Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();

        app.MapGet("/api/v1/identity", () => Results.Json(new
        {
            protocol = LanProtocol.ProtocolName,
            protocolVersion = LanProtocol.ProtocolVersion,
            instanceId = _state.InstanceId,
            machineName = Environment.MachineName
        }));

        app.MapPost("/api/v1/pair", async (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync<PairRequest>(cancellationToken: context.RequestAborted);
            if (request is null || !_pairing.Validate(request.Code))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.ReciprocalToken))
                return Results.BadRequest("Pair request did not include reciprocal authorization.");

            _state.SaveRemoteCredential(request.InstanceId, request.MachineName, request.ReciprocalToken);
            var token = _state.AddInboundTrust(request.InstanceId, request.MachineName);
            _logger.Info($"Mutual LAN pairing accepted for '{request.MachineName}' instance={request.InstanceId}.");
            return Results.Json(new PairResponse
            {
                InstanceId = _state.InstanceId,
                MachineName = Environment.MachineName,
                Token = token
            });
        });

        app.MapGet("/api/v1/servers", async (HttpContext context) =>
        {
            if (!Authorize(context)) return Results.Unauthorized();
            var profiles = await _registry.LoadAsync(context.RequestAborted);
            return Results.Json(profiles.Select(p => new RemoteServerSummary
            {
                Id = p.Id,
                Name = p.Name,
                GamePort = p.GamePort,
                RestApiPort = p.RestApiPort,
                Status = _processes.GetStatusText(p)
            }).ToList());
        });

        app.MapGet("/api/v1/dashboard/{profileId:guid}", async (Guid profileId, HttpContext context) =>
        {
            if (!Authorize(context)) return Results.Unauthorized();
            var profile = (await _registry.LoadAsync(context.RequestAborted)).FirstOrDefault(x => x.Id == profileId);
            if (profile is null) return Results.NotFound();
            var snapshot = await _dashboard.GetSnapshotAsync(profile, context.RequestAborted);
            snapshot.SourceMachine = Environment.MachineName;
            return Results.Json(snapshot);
        });

        app.MapPost("/api/v1/transfers/offers", async (HttpContext context) =>
        {
            if (!Authorize(context)) return Results.Unauthorized();
            var request = await context.Request.ReadFromJsonAsync<TransferOfferRequest>(cancellationToken: context.RequestAborted);
            if (request is null
                || request.SizeBytes <= 0
                || request.SizeBytes > 100L * 1024 * 1024 * 1024
                || request.Sha256.Length != 64
                || !request.Sha256.All(Uri.IsHexDigit))
                return Results.BadRequest("Invalid transfer offer.");

            var offer = new LanTransferOffer
            {
                OfferId = Guid.NewGuid(),
                SourceInstanceId = request.SourceInstanceId,
                SourceMachine = request.SourceMachine,
                ServerName = request.ServerName,
                SizeBytes = request.SizeBytes,
                Sha256 = request.Sha256.ToUpperInvariant(),
                Status = LanTransferStatus.Pending,
                CreatedUtc = DateTime.UtcNow
            };
            _offers[offer.OfferId] = offer;
            OffersChanged?.Invoke(this, EventArgs.Empty);
            _logger.Info($"Received LAN transfer offer {offer.OfferId}: Server='{offer.ServerName}' Bytes={offer.SizeBytes} From='{offer.SourceMachine}'.");
            return Results.Json(new TransferOfferResponse { OfferId = offer.OfferId, Status = offer.Status });
        });

        app.MapGet("/api/v1/transfers/{offerId:guid}/status", (Guid offerId, HttpContext context) =>
        {
            if (!Authorize(context)) return Results.Unauthorized();
            if (!_offers.TryGetValue(offerId, out var offer)) return Results.NotFound();
            return Results.Json(new TransferStatusResponse { OfferId = offer.OfferId, Status = offer.Status, Error = offer.Error });
        });

        app.MapPost("/api/v1/transfers/{offerId:guid}/content", async (Guid offerId, HttpContext context) =>
        {
            if (!Authorize(context)) return Results.Unauthorized();
            if (!_offers.TryGetValue(offerId, out var offer)) return Results.NotFound();
            if (offer.Status != LanTransferStatus.Accepted) return Results.Conflict("Transfer has not been accepted.");
            if (context.Request.ContentLength is long length && length != offer.SizeBytes)
                return Results.BadRequest("Content-Length does not match transfer offer.");

            Directory.CreateDirectory(_paths.IncomingRoot);
            var safeName = SanitizeFileName(offer.ServerName);
            var finalPath = Path.Combine(_paths.IncomingRoot, $"{safeName}_{DateTime.Now:yyyyMMdd-HHmmss}_{offerId.ToString("N")[..8]}.palserver");
            var partialPath = finalPath + ".partial";

            try
            {
                using var operationLease = _operations.Begin(CriticalOperationKind.LanTransferReceive, offer.ServerName);
                offer.Status = LanTransferStatus.Receiving;
                OffersChanged?.Invoke(this, EventArgs.Empty);

                await using (var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                    await context.Request.Body.CopyToAsync(output, 1024 * 1024, context.RequestAborted);

                var info = new FileInfo(partialPath);
                if (info.Length != offer.SizeBytes)
                    throw new InvalidDataException($"Received {info.Length} bytes; expected {offer.SizeBytes}.");

                string hash;
                await using (var stream = File.OpenRead(partialPath))
                    hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, context.RequestAborted));
                if (!hash.Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Received package SHA-256 does not match the transfer offer.");

                File.Move(partialPath, finalPath, true);
                offer.ReceivedPath = finalPath;
                offer.Status = LanTransferStatus.Received;
                offer.Error = null;
                OffersChanged?.Invoke(this, EventArgs.Empty);
                _logger.Info($"LAN transfer received and verified. Offer={offerId} Path='{finalPath}' Bytes={info.Length} SHA256={hash}");
                return Results.Ok();
            }
            catch (Exception ex)
            {
                offer.Status = LanTransferStatus.Failed;
                offer.Error = ex.Message;
                OffersChanged?.Invoke(this, EventArgs.Empty);
                _logger.Error($"LAN transfer receive failed. Offer={offerId}", ex);
                try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { }
                return Results.Problem(ex.Message);
            }
        });

        try
        {
            await app.StartAsync(cancellationToken);
            _app = app;
            _logger.Info($"Manager LAN API started on TCP port {port}. LAN-only use is intended; Internet exposure is unsupported.");
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private bool Authorize(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        return _state.IsAuthorizedToken(header["Bearer ".Length..].Trim());
    }

    private void CleanupOffers()
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var pair in _offers)
            if (pair.Value.CreatedUtc < cutoff) _offers.TryRemove(pair.Key, out _);
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        value = string.IsNullOrWhiteSpace(value) ? "PalworldServer" : value.Trim();
        return value.Length <= 80 ? value : value[..80];
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        try { await _app.StopAsync(); } catch { }
        await _app.DisposeAsync();
        _app = null;
    }
}
