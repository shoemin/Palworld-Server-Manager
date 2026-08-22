using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

public sealed class PortablePackageService
{
    private const string ManifestName = "manifest.json";
    private readonly AppPaths _paths;
    private readonly ServerProcessService _processes;
    private readonly SteamCmdService _steamCmd;
    private readonly ProfileRegistry _registry;
    private readonly IAppLogger _logger;
    private readonly ICriticalOperationTracker _operations;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public PortablePackageService(AppPaths paths, ServerProcessService processes, SteamCmdService steamCmd, ProfileRegistry registry, IAppLogger logger, ICriticalOperationTracker? operations = null)
    {
        _paths = paths;
        _processes = processes;
        _steamCmd = steamCmd;
        _registry = registry;
        _logger = logger;
        _operations = operations ?? new CriticalOperationTracker();
    }

    public async Task ExportAsync(ServerProfile profile, string outputFile, CancellationToken cancellationToken = default)
    {
        using var operationLease = _operations.Begin(CriticalOperationKind.PackageExport, profile.Name);
        _logger.Info($"Portable export requested for '{profile.Name}' to '{outputFile}'.");
        if (_processes.IsRunning(profile))
        {
            var stopped = await _processes.StopAsync(profile, force: false, cancellationToken);
            if (!stopped.Success) throw new InvalidOperationException("Export requires a stopped server. " + stopped.Message);
        }

        var payloadFiles = EnumeratePayload(profile).ToList();
        _logger.Info($"Portable export payload enumerated: {payloadFiles.Count} file(s).");
        var manifest = new PortableServerManifest
        {
            ServerName = profile.Name,
            ExportedUtc = DateTime.UtcNow,
            GamePort = profile.GamePort,
            RestApiPort = profile.RestApiPort
        };

        foreach (var item in payloadFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            manifest.Files.Add(new PortableFileHash { Path = item.ArchivePath, Sha256 = Convert.ToHexString(hash), Length = stream.Length });
        }

        var temp = outputFile + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry(ManifestName, CompressionLevel.Optimal);
            await using (var manifestStream = manifestEntry.Open())
                await JsonSerializer.SerializeAsync(manifestStream, manifest, _json, cancellationToken);

            foreach (var item in payloadFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                archive.CreateEntryFromFile(item.Source, "payload/" + item.ArchivePath, CompressionLevel.Optimal);
            }
        }
        File.Move(temp, outputFile, true);
        _logger.Info($"Exported '{profile.Name}' to {outputFile}. ManifestFiles={manifest.Files.Count} PackageBytes={new FileInfo(outputFile).Length}.");
    }

    public async Task<ServerProfile> ImportAsync(string packageFile, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        using var operationLease = _operations.Begin(CriticalOperationKind.PackageImport, packageFile);
        _logger.Info($"Portable package import requested from '{packageFile}'.");
        if (!File.Exists(packageFile)) throw new FileNotFoundException("Export package not found.", packageFile);
        var temp = Path.Combine(Path.GetTempPath(), "PalworldServerManager", "package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            progress?.Report("Validating portable server package...");
            BackupService.SafeExtract(packageFile, temp);
            var manifestPath = Path.Combine(temp, ManifestName);
            if (!File.Exists(manifestPath)) throw new InvalidDataException("Package does not contain manifest.json.");
            var manifest = JsonSerializer.Deserialize<PortableServerManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), _json)
                ?? throw new InvalidDataException("Package manifest is invalid.");
            if (manifest.Format != "PalworldServerManagerExport" || manifest.FormatVersion != 1)
                throw new InvalidDataException($"Unsupported package format/version: {manifest.Format} v{manifest.FormatVersion}.");
            _logger.Info($"Portable manifest loaded: Server='{manifest.ServerName}' Format={manifest.Format} Version={manifest.FormatVersion} Files={manifest.Files.Count} GamePort={manifest.GamePort} RestPort={manifest.RestApiPort}.");

            foreach (var expected in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = Path.Combine(temp, "payload", expected.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file)) throw new InvalidDataException("Package is missing: " + expected.Path);
                await using var stream = File.OpenRead(file);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (stream.Length != expected.Length || !hash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Package hash verification failed for: " + expected.Path);
            }

            _logger.Info($"Portable package hash verification PASS for {manifest.Files.Count} file(s).");

            var id = Guid.NewGuid();
            var destination = Path.Combine(_paths.ServersRoot, id.ToString("D"), "PalServer");
            var profile = new ServerProfile
            {
                Id = id,
                Name = manifest.ServerName,
                InstallPath = destination,
                GamePort = manifest.GamePort,
                RestApiPort = manifest.RestApiPort,
                CreatedUtc = DateTime.UtcNow,
                ImportedFrom = Path.GetFullPath(packageFile),
                ImportedUtc = DateTime.UtcNow
            };

            try
            {
                progress?.Report("Installing a fresh Palworld server runtime...");
                await _steamCmd.InstallOrUpdatePalworldAsync(destination, progress, cancellationToken);
                var payload = Path.Combine(temp, "payload");
                FileCopyService.CopyDirectory(Path.Combine(payload, "Pal", "Saved"), profile.SavedPath);
                FileCopyService.CopyDirectory(Path.Combine(payload, "Mods"), profile.ModsPath);
                await _registry.AddAsync(profile, cancellationToken);
                _logger.Info($"Imported portable package '{packageFile}' as '{profile.Name}'.");
                return profile;
            }
            catch (Exception ex)
            {
                _logger.Error($"Portable package import failed. Cleaning incomplete destination '{destination}'.", ex);
                try
                {
                    var profileRoot = Directory.GetParent(destination)?.FullName;
                    if (profileRoot is not null && Directory.Exists(profileRoot)) Directory.Delete(profileRoot, true);
                }
                catch { }
                throw;
            }
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }

    private static IEnumerable<(string Source, string ArchivePath)> EnumeratePayload(ServerProfile profile)
    {
        foreach (var pair in EnumerateTree(profile.SavedPath, "Pal/Saved")) yield return pair;
        foreach (var pair in EnumerateTree(profile.ModsPath, "Mods")) yield return pair;
    }

    private static IEnumerable<(string Source, string ArchivePath)> EnumerateTree(string root, string prefix)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            yield return (file, $"{prefix}/{relative}");
        }
    }
}
