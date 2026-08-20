using System.IO.Compression;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;
using PalworldServerManager.Core.Services;
using PalworldServerManager.SelfTest;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("Config parser handles quoted commas and nested lists", TestConfigParser),
    ("Config round-trip preserves unknown settings", TestUnknownRoundTrip),
    ("Directory copy leaves source byte-for-byte unchanged", TestNonDestructiveCopy),
    ("Profile registry round-trips", TestProfileRegistry),
    ("Manual discovery recognizes a legacy server", TestDiscovery),
    ("Structured logger records correlated operations", TestStructuredLogging),
    ("SteamCMD code 7 is classified for interactive recovery", TestSteamCmdRecoveryClassification),
    ("Server lifetime result prefers shipping-process exit code", TestServerLifetimeExitResult),
    ("Diagnostic bundle redacts secrets and excludes saves", TestDiagnosticBundle),
    ("Palworld REST models parse representative JSON", RestTests.TestRestModelsParseRepresentativeJson),
    ("Palworld REST models tolerate missing/partial JSON fields", RestTests.TestRestModelsToleratePartialJson),
    ("Palworld REST settings redact secret-shaped keys", RestTests.TestRestSettingsRedaction),
    ("Palworld REST client never logs the admin password", RestTests.TestRestSecretsNeverLogged),
    ("Pairing code is six digits and one-use", LanTests.TestPairingCodeIsSixDigitsAndOneUse),
    ("Pairing wrong code does not consume the real code", LanTests.TestPairingWrongCodeDoesNotConsumeTheRealCode),
    ("Pairing failed attempts are bounded and lock out the code", LanTests.TestPairingFailedAttemptsAreBoundedAndLockOutTheCode),
    ("LAN is disabled by default for a new Manager state", LanTests.TestLanDisabledByDefaultForANewState),
    ("Trusted-peer token is hashed at rest and revocable", LanTests.TestTrustedPeerTokenIsHashedAtRestAndAuthorizesOnlyUntilRevoked),
    ("Remote pairing credential persists across a Manager restart", LanTests.TestRemoteCredentialPersistsAcrossReload),
    ("LAN discovery advertisement carries no secrets", LanTests.TestDiscoveryAdvertisementCarriesNoSecrets),
    ("LAN discovery filters unknown protocol/version/self advertisements", LanTests.TestDiscoveryFiltersUnknownProtocolAndSelfAdvertisements),
    ("LAN API rejects unauthenticated and wrong-token requests", LanTests.TestLanHostRejectsUnauthenticatedAndWrongTokenRequests),
    ("LAN pairing grants authorized access and rejects a wrong code", LanTests.TestLanPairingGrantsAuthorizedAccessAndRejectsWrongCode),
    ("LAN transfer offer rejects malformed metadata", LanTests.TestLanTransferOfferRejectsMalformedMetadata),
    ("LAN transfer completes and verifies whole-file SHA-256", LanTests.TestLanTransferCompletesAndVerifiesWholeFileHash),
    ("LAN transfer hash mismatch is rejected and leaves no partial file", LanTests.TestLanTransferHashMismatchIsRejectedAndLeavesNoPartialFile)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL  {test.Name}");
        Console.WriteLine(ex);
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Count - failures}/{tests.Count} self-tests passed.");
return failures == 0 ? 0 : 1;

static Task TestConfigParser()
{
    const string text = "[/Script/Pal.PalGameWorldSettings]\r\nOptionSettings=(ServerName=\"Friends, Pals & Chaos\",DenyTechnologyList=(\"PALBOX\",\"RepairBench\"),CrossplayPlatforms=(Steam,Xbox,PS5,Mac),ExpRate=1.500000,UnknownFutureSetting=\"a,b,c\")\r\n";
    var doc = PalworldConfigParser.Parse(text);
    Equal("\"Friends, Pals & Chaos\"", doc.Get("ServerName"));
    Equal("(\"PALBOX\",\"RepairBench\")", doc.Get("DenyTechnologyList"));
    Equal("(Steam,Xbox,PS5,Mac)", doc.Get("CrossplayPlatforms"));
    Equal("\"a,b,c\"", doc.Get("UnknownFutureSetting"));
    return Task.CompletedTask;
}

static Task TestUnknownRoundTrip()
{
    const string text = "; retained comment\n[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(Known=True,FutureThing=(One,Two),StringValue=\"hello, world\")\n; trailing comment\n";
    var doc = PalworldConfigParser.Parse(text);
    doc.Set("Known", "False");
    var serialized = doc.Serialize();
    True(serialized.Contains("; retained comment"), "prefix comment not retained");
    True(serialized.Contains("; trailing comment"), "suffix comment not retained");
    var reparsed = PalworldConfigParser.Parse(serialized);
    Equal("False", reparsed.Get("Known"));
    Equal("(One,Two)", reparsed.Get("FutureThing"));
    Equal("\"hello, world\"", reparsed.Get("StringValue"));
    return Task.CompletedTask;
}

static async Task TestNonDestructiveCopy()
{
    var root = Path.Combine(Path.GetTempPath(), "psm-selftest-" + Guid.NewGuid().ToString("N"));
    var source = Path.Combine(root, "source");
    var dest = Path.Combine(root, "dest");
    try
    {
        Directory.CreateDirectory(Path.Combine(source, "SaveGames", "0", "ABC"));
        await File.WriteAllTextAsync(Path.Combine(source, "SaveGames", "0", "ABC", "Level.sav"), "world-data");
        await File.WriteAllTextAsync(Path.Combine(source, "PalWorldSettings.ini"), "settings");
        var before = await DirectoryHashService.HashTreeAsync(source);
        FileCopyService.CopyDirectory(source, dest);
        var after = await DirectoryHashService.HashTreeAsync(source);
        True(DirectoryHashService.Equivalent(before, after, out var difference), difference);
        var copied = await DirectoryHashService.HashTreeAsync(dest);
        True(DirectoryHashService.Equivalent(before, copied, out difference), "copy mismatch: " + difference);
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}

static async Task TestProfileRegistry()
{
    var root = Path.Combine(Path.GetTempPath(), "psm-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var paths = new AppPaths(root);
        var logger = new FileLogger(paths);
        var registry = new ProfileRegistry(paths, logger);
        var profile = new ServerProfile { Name = "Test Server", InstallPath = Path.Combine(root, "server") };
        await registry.AddAsync(profile);
        var loaded = await registry.LoadAsync();
        Equal(1, loaded.Count);
        Equal(profile.Id, loaded[0].Id);
        Equal("Test Server", loaded[0].Name);
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}

static async Task TestDiscovery()
{
    var root = Path.Combine(Path.GetTempPath(), "psm-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var legacy = Path.Combine(root, "legacy", "PalServer");
        Directory.CreateDirectory(Path.Combine(legacy, "Pal", "Saved", "Config", "WindowsServer"));
        Directory.CreateDirectory(Path.Combine(legacy, "Pal", "Saved", "SaveGames", "0", "ABC"));
        await File.WriteAllTextAsync(Path.Combine(legacy, "PalServer.exe"), "placeholder");
        await File.WriteAllTextAsync(Path.Combine(legacy, "DefaultPalWorldSettings.ini"), "[/Script/Pal.PalGameWorldSettings]\nOptionSettings=()\n");
        await File.WriteAllTextAsync(Path.Combine(legacy, "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini"), "[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(ServerName=\"Imported Test\")\n");
        await File.WriteAllTextAsync(Path.Combine(legacy, "Pal", "Saved", "SaveGames", "0", "ABC", "Level.sav"), "data");

        var paths = new AppPaths(Path.Combine(root, "manager"));
        var logger = new FileLogger(paths);
        var registry = new ProfileRegistry(paths, logger);
        var locator = new SteamLocator(paths, logger);
        var discovery = new ServerDiscoveryService(locator, registry);
        var candidate = discovery.Analyze(legacy, await registry.LoadAsync());
        Equal(ExistingServerClassification.ValidExistingServer, candidate.Classification);
        Equal("Imported Test", candidate.DisplayName);
        True(candidate.HasSaveData, "save not detected");
        True(candidate.HasSettings, "settings not detected");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}

static async Task TestStructuredLogging()
{
    var root = Path.Combine(Path.GetTempPath(), "psm-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var paths = new AppPaths(root);
        var logger = new FileLogger(paths);
        var serverId = Guid.NewGuid();
        using (logger.BeginOperation("SelfTestOperation", serverId, "Logging Test"))
        {
            logger.Info("inside operation");
            logger.Warning("warning sample");
            logger.Error("error sample", new InvalidOperationException("synthetic failure"));
        }

        var text = await File.ReadAllTextAsync(logger.CurrentLogFile);
        True(text.Contains($"session={logger.SessionId}"), "session id missing from log");
        True(text.Contains("BEGIN operation 'SelfTestOperation'"), "operation begin missing");
        True(text.Contains("END operation 'SelfTestOperation'"), "operation end missing");
        True(text.Contains(serverId.ToString("D")), "server id missing from operation context");
        True(text.Contains("synthetic failure"), "exception details missing");
        var perServerLog = Path.Combine(paths.LogsRoot, "servers", $"server-{serverId:D}.log");
        True(File.Exists(perServerLog), "per-server correlated log was not created");
        var perServerText = await File.ReadAllTextAsync(perServerLog);
        True(perServerText.Contains("SelfTestOperation"), "per-server log is missing correlated operation content");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}


static Task TestSteamCmdRecoveryClassification()
{
    var code7 = new SteamCmdException(7);
    var code8 = new SteamCmdException(8);
    Equal(7, code7.ExitCode);
    True(code7.SuggestSteamClientRecovery, "exit code 7 should suggest Steam client recovery");
    True(!code8.SuggestSteamClientRecovery, "unrelated exit codes should not be mislabeled as the field-tested code-7 recovery case");
    return Task.CompletedTask;
}


static Task TestServerLifetimeExitResult()
{
    var result = new ServerProcessLifetimeEndedEventArgs
    {
        ServerId = Guid.NewGuid(),
        ServerName = "Lifetime Test",
        ExpectedStop = false,
        ProcessExits =
        [
            new ServerProcessExitInfo(100, "PalServer", 0),
            new ServerProcessExitInfo(101, "PalServer-Win64-Shipping-Cmd", 42)
        ],
        Message = "synthetic lifetime result"
    };

    True(result.HasNonZeroExitCode, "non-zero shipping exit should classify the lifetime as an error");
    Equal(42, result.PrimaryExitCode);
    return Task.CompletedTask;
}

static async Task TestDiagnosticBundle()
{
    var root = Path.Combine(Path.GetTempPath(), "psm-selftest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var paths = new AppPaths(Path.Combine(root, "manager"));
        var logger = new FileLogger(paths);
        var profile = new ServerProfile
        {
            Name = "Diagnostic Test",
            InstallPath = Path.Combine(root, "server", "PalServer")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(profile.SettingsPath)!);
        Directory.CreateDirectory(Path.Combine(profile.SavedPath, "Logs"));
        Directory.CreateDirectory(Path.Combine(profile.SavedPath, "SaveGames", "0", "ABC"));
        await File.WriteAllTextAsync(profile.SettingsPath,
            "[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(ServerName=\"Diag\",AdminPassword=\"super-secret-admin\",ServerPassword=\"super-secret-server\",ExpRate=1.0)\n");
        await File.WriteAllTextAsync(Path.Combine(profile.SavedPath, "Logs", "PalServer.json"), "{\"event\":\"server log sample\"}\n");
        await File.WriteAllTextAsync(Path.Combine(profile.SavedPath, "SaveGames", "0", "ABC", "Level.sav"), "must never be exported in diagnostics");
        logger.Info("diagnostic manager log sample");

        var diagnostics = new DiagnosticBundleService(paths, logger);
        var output = Path.Combine(root, "diagnostics.zip");
        await diagnostics.CreateAsync(output, profile);

        using var zip = ZipFile.OpenRead(output);
        True(zip.Entries.Any(x => x.FullName == "server/PalWorldSettings.sanitized.ini"), "sanitized settings missing");
        True(zip.Entries.Any(x => x.FullName.StartsWith("manager-logs/")), "manager logs missing");
        True(zip.Entries.Any(x => x.FullName == "server/logs/PalServer.json"), "JSON server log missing");
        True(!zip.Entries.Any(x => x.FullName.EndsWith(".sav", StringComparison.OrdinalIgnoreCase)), "diagnostic bundle contains a save file");

        var settingsEntry = zip.GetEntry("server/PalWorldSettings.sanitized.ini")!;
        using var reader = new StreamReader(settingsEntry.Open());
        var settings = await reader.ReadToEndAsync();
        True(!settings.Contains("super-secret-admin"), "admin password leaked into diagnostic bundle");
        True(!settings.Contains("super-secret-server"), "server password leaked into diagnostic bundle");
        True(settings.Contains("***REDACTED***"), "redaction marker missing");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}

static void True(bool condition, string message = "assertion failed")
{
    if (!condition) throw new Exception(message);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', got '{actual}'.");
}
