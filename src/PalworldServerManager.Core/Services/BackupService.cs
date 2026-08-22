using System.IO.Compression;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

public sealed class BackupService
{
    private readonly AppPaths _paths;
    private readonly ServerProcessService _processes;
    private readonly IAppLogger _logger;
    private readonly ICriticalOperationTracker _operations;

    public BackupService(AppPaths paths, ServerProcessService processes, IAppLogger logger, ICriticalOperationTracker? operations = null)
    {
        _paths = paths;
        _processes = processes;
        _logger = logger;
        _operations = operations ?? new CriticalOperationTracker();
    }

    public async Task<string> CreateBackupAsync(ServerProfile profile, string reason = "manual", CancellationToken cancellationToken = default)
    {
        using var operationLease = _operations.Begin(CriticalOperationKind.Backup, profile.Name);
        _logger.Info($"Backup requested for '{profile.Name}' reason='{reason}'.");
        if (_processes.IsRunning(profile))
            throw new InvalidOperationException("Stop the server before creating a filesystem backup. Use the server's built-in backup option for live snapshots.");

        var folder = Path.Combine(_paths.BackupsRoot, profile.Id.ToString("D"));
        Directory.CreateDirectory(folder);
        var safeReason = string.Concat(reason.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var output = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeReason}.zip");

        using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
        AddTree(zip, profile.SavedPath, "Pal/Saved", cancellationToken);
        AddTree(zip, profile.ModsPath, "Mods", cancellationToken);
        _logger.Info($"Created backup for '{profile.Name}': {output} Bytes={new FileInfo(output).Length}");
        await Task.CompletedTask;
        return output;
    }

    public async Task RestoreBackupAsync(ServerProfile profile, string backupFile, CancellationToken cancellationToken = default)
    {
        using var operationLease = _operations.Begin(CriticalOperationKind.Restore, profile.Name);
        _logger.Info($"Backup restore requested for '{profile.Name}' from '{backupFile}'.");
        if (_processes.IsRunning(profile)) throw new InvalidOperationException("Stop the server before restoring a backup.");
        if (!File.Exists(backupFile)) throw new FileNotFoundException("Backup file not found.", backupFile);

        await CreateBackupAsync(profile, "pre-restore", cancellationToken);
        var temp = Path.Combine(Path.GetTempPath(), "PalworldServerManager", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            SafeExtract(backupFile, temp);
            var saved = Path.Combine(temp, "Pal", "Saved");
            var mods = Path.Combine(temp, "Mods");
            if (!Directory.Exists(saved)) throw new InvalidDataException("Backup does not contain Pal/Saved.");

            if (Directory.Exists(profile.SavedPath)) Directory.Delete(profile.SavedPath, true);
            FileCopyService.CopyDirectory(saved, profile.SavedPath);
            if (Directory.Exists(profile.ModsPath)) Directory.Delete(profile.ModsPath, true);
            if (Directory.Exists(mods)) FileCopyService.CopyDirectory(mods, profile.ModsPath);
            _logger.Info($"Restored backup '{backupFile}' to '{profile.Name}'.");
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }

    private static void AddTree(ZipArchive zip, string root, string prefix, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, $"{prefix}/{relative}", CompressionLevel.Optimal);
        }
    }

    internal static void SafeExtract(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Archive contains an unsafe path.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}
