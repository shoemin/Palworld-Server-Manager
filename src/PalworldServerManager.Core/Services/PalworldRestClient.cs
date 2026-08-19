using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PalworldServerManager.Core.Infrastructure;

namespace PalworldServerManager.Core.Services;

public sealed class PalworldRestClient
{
    private readonly HttpClient _http;
    private readonly IAppLogger? _logger;

    public PalworldRestClient(IAppLogger? logger = null, HttpClient? httpClient = null)
    {
        _logger = logger;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task SaveAsync(int port, string adminPassword, CancellationToken cancellationToken = default)
        => await SendAsync(HttpMethod.Post, port, adminPassword, "/save", null, cancellationToken);

    public async Task ShutdownAsync(int port, string adminPassword, int waitSeconds, string message, CancellationToken cancellationToken = default)
        => await SendAsync(HttpMethod.Post, port, adminPassword, "/shutdown", new { waittime = waitSeconds, message }, cancellationToken);

    public async Task<string> GetInfoAsync(int port, string adminPassword, CancellationToken cancellationToken = default)
        => await SendAsync(HttpMethod.Get, port, adminPassword, "/info", null, cancellationToken);

    private async Task<string> SendAsync(HttpMethod method, int port, string adminPassword, string path, object? body, CancellationToken cancellationToken)
    {
        var endpoint = $"http://127.0.0.1:{port}/v1/api{path}";
        _logger?.Debug($"Palworld REST request: {method.Method} {endpoint}");
        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(method, endpoint);
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{adminPassword}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            _logger?.Debug($"Palworld REST response: {method.Method} {path} status={(int)response.StatusCode} elapsed={stopwatch.Elapsed.TotalMilliseconds:F0}ms bodyLength={content.Length}");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Palworld REST API returned {(int)response.StatusCode} {response.ReasonPhrase}: {content}");
            return content;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.Error($"Palworld REST request failed: {method.Method} {path} port={port} elapsed={stopwatch.Elapsed.TotalMilliseconds:F0}ms", ex);
            throw;
        }
    }
}
