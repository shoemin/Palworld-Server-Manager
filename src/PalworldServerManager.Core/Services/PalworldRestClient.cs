using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

public sealed class PalworldRestClient
{
    private readonly HttpClient _http;
    private readonly IAppLogger? _logger;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

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

    public async Task<PalworldServerInfo> GetServerInfoAsync(int port, string adminPassword, CancellationToken cancellationToken = default)
        => await GetJsonAsync<PalworldServerInfo>(port, adminPassword, "/info", cancellationToken);

    public async Task<PalworldServerMetrics> GetMetricsAsync(int port, string adminPassword, CancellationToken cancellationToken = default)
        => await GetJsonAsync<PalworldServerMetrics>(port, adminPassword, "/metrics", cancellationToken);

    public async Task<IReadOnlyList<PalworldPlayer>> GetPlayersAsync(int port, string adminPassword, CancellationToken cancellationToken = default)
    {
        var response = await GetJsonAsync<PalworldPlayersResponse>(port, adminPassword, "/players", cancellationToken);
        return response.Players;
    }

    public async Task<IReadOnlyList<DashboardSetting>> GetSettingsAsync(int port, string adminPassword, CancellationToken cancellationToken = default)
    {
        var raw = await SendAsync(HttpMethod.Get, port, adminPassword, "/settings", null, cancellationToken);
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Palworld REST /settings returned a non-object JSON payload.");

        var settings = new List<DashboardSetting>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            settings.Add(new DashboardSetting
            {
                Key = property.Name,
                Value = IsSensitiveSettingKey(property.Name) ? "***REDACTED***" : FormatJsonValue(property.Value)
            });
        }

        return settings.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<T> GetJsonAsync<T>(int port, string adminPassword, string path, CancellationToken cancellationToken)
    {
        var raw = await SendAsync(HttpMethod.Get, port, adminPassword, path, null, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<T>(raw, _json)
                ?? throw new InvalidDataException($"Palworld REST {path} returned an empty JSON payload.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Palworld REST {path} returned invalid JSON.", ex);
        }
    }

    private async Task<string> SendAsync(HttpMethod method, int port, string adminPassword, string path, object? body, CancellationToken cancellationToken)
    {
        var endpoint = $"http://127.0.0.1:{port}/v1/api{path}";
        _logger?.Debug($"Palworld REST request: {method.Method} {endpoint}");
        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(method, endpoint);
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{adminPassword}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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



    private static bool IsSensitiveSettingKey(string key)
        => key.Contains("password", StringComparison.OrdinalIgnoreCase)
           || key.Contains("token", StringComparison.OrdinalIgnoreCase)
           || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
           || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
           || key.Replace(" ", "", StringComparison.Ordinal).Contains("apikey", StringComparison.OrdinalIgnoreCase);

    private static string FormatJsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Null => string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        _ => value.GetRawText()
    };
}
