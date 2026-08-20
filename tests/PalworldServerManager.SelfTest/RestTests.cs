using System.Net;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Services;

namespace PalworldServerManager.SelfTest;

internal static class RestTests
{
    public static async Task TestRestModelsParseRepresentativeJson()
    {
        const string info = """{"version":"v1.9.0","servername":"Friends & Pals","description":"desc","worldguid":"abc-123"}""";
        const string metrics = """{"serverfps":60,"currentplayernum":3,"serverframetime":12.5,"maxplayernum":32,"uptime":3600,"basecampnum":5,"days":10}""";
        const string players = """{"players":[{"name":"Alice","accountName":"alice#1","playerId":"1","userId":"steam_1","ip":"10.0.0.5","ping":22.5,"location_x":100.0,"location_y":200.0,"level":30,"building_count":12}]}""";

        var handler = new StubHandler(path => path switch
        {
            "/v1/api/info" => info,
            "/v1/api/metrics" => metrics,
            "/v1/api/players" => players,
            _ => throw new InvalidOperationException("Unexpected path: " + path)
        });
        var client = new PalworldRestClient(null, new HttpClient(handler));

        var parsedInfo = await client.GetServerInfoAsync(8212, "admin-secret");
        Equal("v1.9.0", parsedInfo.Version);
        Equal("Friends & Pals", parsedInfo.ServerName);
        Equal("abc-123", parsedInfo.WorldGuid);

        var parsedMetrics = await client.GetMetricsAsync(8212, "admin-secret");
        Equal(60, parsedMetrics.ServerFps);
        Equal(3, parsedMetrics.CurrentPlayerNum);
        Equal(10, parsedMetrics.Days);

        var parsedPlayers = await client.GetPlayersAsync(8212, "admin-secret");
        Equal(1, parsedPlayers.Count);
        Equal("Alice", parsedPlayers[0].Name);
        Equal(30, parsedPlayers[0].Level);
    }

    public static async Task TestRestModelsToleratePartialJson()
    {
        // A future/older Palworld build may omit fields the manager does not yet know about.
        // Missing fields must default rather than throwing.
        const string info = """{"version":"v2.0.0"}""";
        const string metrics = "{}";
        const string players = "{}";

        var handler = new StubHandler(path => path switch
        {
            "/v1/api/info" => info,
            "/v1/api/metrics" => metrics,
            "/v1/api/players" => players,
            _ => throw new InvalidOperationException("Unexpected path: " + path)
        });
        var client = new PalworldRestClient(null, new HttpClient(handler));

        var parsedInfo = await client.GetServerInfoAsync(8212, "admin-secret");
        Equal("v2.0.0", parsedInfo.Version);
        Equal(string.Empty, parsedInfo.WorldGuid);

        var parsedMetrics = await client.GetMetricsAsync(8212, "admin-secret");
        Equal(0, parsedMetrics.ServerFps);

        var parsedPlayers = await client.GetPlayersAsync(8212, "admin-secret");
        Equal(0, parsedPlayers.Count);
    }

    public static async Task TestRestSettingsRedaction()
    {
        const string settings = """
            {
              "ServerName": "My Server",
              "AdminPassword": "top-secret-admin",
              "ServerPassword": "top-secret-server",
              "RCONEnabled": true,
              "SomeApiKey": "abcdef",
              "OAuthCredential": "xyz",
              "SessionToken": "qrs",
              "ExpRate": 1.5
            }
            """;

        var handler = new StubHandler(_ => settings);
        var client = new PalworldRestClient(null, new HttpClient(handler));

        var parsed = (await client.GetSettingsAsync(8212, "admin-secret")).ToDictionary(x => x.Key, x => x.Value);

        Equal("***REDACTED***", parsed["AdminPassword"]);
        Equal("***REDACTED***", parsed["ServerPassword"]);
        Equal("***REDACTED***", parsed["SomeApiKey"]);
        Equal("***REDACTED***", parsed["OAuthCredential"]);
        Equal("***REDACTED***", parsed["SessionToken"]);
        Equal("My Server", parsed["ServerName"]);
        Equal("True", parsed["RCONEnabled"]);
        Equal("1.5", parsed["ExpRate"]);

        var allValues = string.Join("|", parsed.Values);
        True(!allValues.Contains("top-secret"), "a secret value leaked past redaction");
    }

    public static async Task TestRestSecretsNeverLogged()
    {
        var log = new CapturingLogger();
        var handler = new StubHandler(_ => """{"version":"v1.0.0"}""");
        var client = new PalworldRestClient(log, new HttpClient(handler));

        const string adminPassword = "super-secret-admin-password-xyz";
        await client.GetServerInfoAsync(8212, adminPassword);

        var combined = string.Join("\n", log.Lines);
        True(!combined.Contains(adminPassword), "admin password leaked into REST client logs");
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

    private sealed class StubHandler(Func<string, string> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = responder(request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class CapturingLogger : IAppLogger
    {
        public List<string> Lines { get; } = [];
        public string SessionId => "selftest-session";
        public string CurrentLogFile => string.Empty;
        public string LogsDirectory => string.Empty;
        public void Debug(string message) => Lines.Add(message);
        public void Info(string message) => Lines.Add(message);
        public void Warning(string message) => Lines.Add(message);
        public void Error(string message, Exception? ex = null) => Lines.Add(message + ex);
        public IDisposable BeginOperation(string operationName, Guid? serverId = null, string? serverName = null) => new NoopScope();
        private sealed class NoopScope : IDisposable { public void Dispose() { } }
    }
}
