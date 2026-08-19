using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

public sealed class DiagnosticBundleService
{
    private const int MaxManagerLogs = 20;
    private const int MaxServerLogs = 12;
    private const long MaxFullLogBytes = 8L * 1024 * 1024;
    private const long TailBytesForLargeLog = 4L * 1024 * 1024;

    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public DiagnosticBundleService(AppPaths paths, IAppLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<string> CreateAsync(string outputFile, ServerProfile? profile = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputFile)) throw new ArgumentException("An output file is required.", nameof(outputFile));
        var fullOutput = Path.GetFullPath(outputFile);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        if (File.Exists(fullOutput)) File.Delete(fullOutput);

        using var operation = _logger.BeginOperation("BuildDiagnosticBundle", profile?.Id, profile?.Name);
        _logger.Info($"Creating diagnostic bundle at '{fullOutput}'.");

        await using var fileStream = new FileStream(fullOutput, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);

        cancellationToken.ThrowIfCancellationRequested();
        await AddTextAsync(zip, "diagnostics/summary.txt", BuildSummary(profile), cancellationToken);
        await AddTextAsync(zip, "diagnostics/README.txt", BuildReadme(), cancellationToken);

        foreach (var log in Directory.EnumerateFiles(_paths.LogsRoot, "manager-*.log", SearchOption.TopDirectoryOnly)
                     .Select(path => new FileInfo(path))
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Take(MaxManagerLogs))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AddSanitizedLogAsync(zip, log, $"manager-logs/{log.Name}", cancellationToken);
        }

        if (profile is not null)
        {
            await AddTextAsync(zip, "server/profile.json", JsonSerializer.Serialize(CreateSafeProfile(profile), _json), cancellationToken);
            await AddTextAsync(zip, "server/processes.txt", BuildProcessReport(profile), cancellationToken);

            var correlatedServerLog = Path.Combine(_paths.LogsRoot, "servers", $"server-{profile.Id:D}.log");
            if (File.Exists(correlatedServerLog))
                await AddSanitizedLogAsync(zip, new FileInfo(correlatedServerLog), "server/manager-correlated.log", cancellationToken);

            if (File.Exists(profile.SettingsPath))
            {
                try
                {
                    var settings = await File.ReadAllTextAsync(profile.SettingsPath, cancellationToken);
                    await AddTextAsync(zip, "server/PalWorldSettings.sanitized.ini", SanitizeSettings(settings), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Warning("Could not add sanitized PalWorldSettings.ini to diagnostic bundle: " + ex.Message);
                    await AddTextAsync(zip, "server/settings-error.txt", ex.ToString(), cancellationToken);
                }
            }

            var serverLogs = Path.Combine(profile.SavedPath, "Logs");
            if (Directory.Exists(serverLogs))
            {
                foreach (var log in Directory.EnumerateFiles(serverLogs, "*", SearchOption.TopDirectoryOnly)
                             .Select(path => new FileInfo(path))
                             .Where(file => IsLikelyTextLog(file.Extension))
                             .OrderByDescending(file => file.LastWriteTimeUtc)
                             .Take(MaxServerLogs))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await AddSanitizedLogAsync(zip, log, $"server/logs/{log.Name}", cancellationToken);
                }
            }
        }

        _logger.Info("Diagnostic bundle created successfully. No world save (.sav) files are included.");
        return fullOutput;
    }

    public static string SanitizeSettings(string text)
    {
        try
        {
            var document = PalworldConfigParser.Parse(text);
            foreach (var secret in new[] { "AdminPassword", "ServerPassword" })
            {
                if (document.Get(secret) is not null) document.Set(secret, PalworldConfigParser.Quote("***REDACTED***"));
            }
            return document.Serialize();
        }
        catch
        {
            return RedactText(text);
        }
    }

    private string BuildSummary(ServerProfile? profile)
    {
        var entry = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? entry.GetName().Version?.ToString()
                      ?? "unknown";
        var sb = new StringBuilder();
        sb.AppendLine("Palworld Server Manager Diagnostic Bundle");
        sb.AppendLine($"GeneratedUtc: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"ManagerSessionId: {_logger.SessionId}");
        sb.AppendLine($"ApplicationVersion: {version}");
        sb.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"OSArchitecture: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"64BitOS: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"64BitProcess: {Environment.Is64BitProcess}");
        sb.AppendLine($"ProcessorCount: {Environment.ProcessorCount}");
        sb.AppendLine();
        if (profile is null)
        {
            sb.AppendLine("SelectedServer: none (application-only diagnostic bundle)");
        }
        else
        {
            sb.AppendLine($"SelectedServerName: {profile.Name}");
            sb.AppendLine($"SelectedServerId: {profile.Id:D}");
            sb.AppendLine($"GamePort: {profile.GamePort}");
            sb.AppendLine($"RestApiPort: {profile.RestApiPort}");
            sb.AppendLine($"RunningAtExport: {ProcessInspection.IsPalServerRunningFrom(profile.InstallPath)}");
            sb.AppendLine($"InstallPath: {SanitizePath(profile.InstallPath)}");
            sb.AppendLine($"ImportedFrom: {SanitizePath(profile.ImportedFrom)}");
        }
        sb.AppendLine();
        sb.AppendLine("Privacy/Safety: AdminPassword and ServerPassword are redacted. World save files are never included.");
        return sb.ToString();
    }

    private static string BuildReadme() =>
        "This ZIP is intended for Palworld Server Manager troubleshooting.\r\n" +
        "It contains manager logs, environment metadata, and (when a server was selected) recent Palworld server logs and a sanitized settings snapshot.\r\n" +
        "It does NOT contain .sav world/player files. AdminPassword and ServerPassword are redacted.\r\n" +
        "Large log files are included as a tail excerpt so the bundle remains practical to share.\r\n";

    private object CreateSafeProfile(ServerProfile profile) => new
    {
        profile.Id,
        profile.Name,
        InstallPath = SanitizePath(profile.InstallPath),
        profile.GamePort,
        profile.RestApiPort,
        profile.CreatedUtc,
        ImportedFrom = SanitizePath(profile.ImportedFrom),
        profile.ImportedUtc,
        AdditionalLaunchArguments = RedactLaunchArguments(profile.AdditionalLaunchArguments)
    };

    private static string BuildProcessReport(ServerProfile profile)
    {
        var sb = new StringBuilder();
        var processes = ProcessInspection.FindPalServerProcesses(profile.InstallPath);
        try
        {
            if (processes.Count == 0)
            {
                sb.AppendLine("No PalServer processes were detected for this managed installation.");
                return sb.ToString();
            }

            foreach (var process in processes)
            {
                try
                {
                    string? path = null;
                    try { path = process.MainModule?.FileName; } catch { }
                    sb.AppendLine($"PID={process.Id} Name={process.ProcessName} Path={SanitizePath(path)} Started={SafeStartTime(process)}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"PID={process.Id} <inspection failed: {ex.Message}>");
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return sb.ToString();
    }

    private async Task AddSanitizedLogAsync(ZipArchive zip, FileInfo file, string archivePath, CancellationToken cancellationToken)
    {
        try
        {
            string text;
            string targetPath = archivePath.Replace('\\', '/');
            if (file.Length <= MaxFullLogBytes)
            {
                text = await File.ReadAllTextAsync(file.FullName, cancellationToken);
            }
            else
            {
                text = await ReadTailTextAsync(file.FullName, TailBytesForLargeLog, cancellationToken);
                targetPath = Path.ChangeExtension(targetPath, null) + ".tail.txt";
                text = $"[Original log size: {file.Length} bytes. Only the final {TailBytesForLargeLog} bytes are included.]\r\n" + text;
            }

            text = RedactText(text);
            await AddTextAsync(zip, targetPath, text, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not add log '{file.FullName}' to diagnostics: {ex.Message}");
        }
    }

    private static async Task<string> ReadTailTextAsync(string path, long bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = Math.Max(0, stream.Length - bytes);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task AddTextAsync(ZipArchive zip, string archivePath, string text, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(archivePath.Replace('\\', '/'), CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
    }

    private static bool IsLikelyTextLog(string extension)
        => extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".out", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".json", StringComparison.OrdinalIgnoreCase);

    private static string RedactText(string text)
    {
        var result = text;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            result = result.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

        // Covers common INI/log and command-line renderings without rewriting arbitrary data.
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(?i)(AdminPassword\s*=\s*)(""[^""]*""|[^,\r\n)]*)",
            "$1\"***REDACTED***\"");
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(?i)(ServerPassword\s*=\s*)(""[^""]*""|[^,\r\n)]*)",
            "$1\"***REDACTED***\"");
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(?i)(password|passwd|token|secret|apikey|api_key)(\s*[=:]\s*|\s+)(""[^""]*""|\S+)",
            "$1$2***REDACTED***");
        return result;
    }

    private static string RedactLaunchArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(
            arguments,
            @"(?i)(password|passwd|token|secret|apikey|api_key)(\s*[=:]\s*|\s+)(""[^""]*""|\S+)",
            "$1$2***REDACTED***");
    }

    private static string SanitizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) && path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
            return "%USERPROFILE%" + path[userProfile.Length..];
        return path;
    }

    private static string SafeStartTime(System.Diagnostics.Process process)
    {
        try { return process.StartTime.ToUniversalTime().ToString("O"); }
        catch { return "unknown"; }
    }
}
