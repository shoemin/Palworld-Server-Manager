using System.Text.Json;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

/// <summary>
/// Persists a short-lived hint about which managed server processes were observed running
/// just before the Manager exits to apply a self-update, so the newly-started Manager can
/// reattach with higher confidence. This is a hint only: the reconciler must still verify
/// process identity before trusting it, and a normal Manager restart/crash must be able to
/// reattach even when no handoff file exists at all.
/// </summary>
public sealed class RuntimeHandoffService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private readonly string _file;
    private readonly IAppLogger _logger;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public RuntimeHandoffService(AppPaths paths, IAppLogger logger)
    {
        Directory.CreateDirectory(paths.RuntimeRoot);
        _file = Path.Combine(paths.RuntimeRoot, "update-handoff.json");
        _logger = logger;
    }

    public async Task WriteAsync(RuntimeHandoffDocument document, CancellationToken cancellationToken = default)
    {
        var temp = _file + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, document, _json, cancellationToken);
        File.Move(temp, _file, true);
        _logger.Info($"Runtime handoff written. HandoffId={document.HandoffId:D} Servers={document.Servers.Count} From={document.OldManagerVersion} To={document.TargetManagerVersion}.");
    }

    /// <summary>
    /// Discards a handoff file without consuming it, for rolling back an update apply that wrote
    /// the handoff but did not go on to successfully launch the external updater. Safe/idempotent
    /// when no file exists; never touches server or profile data; never throws. Without this, a
    /// failed apply attempt could leave a handoff behind that a later, unrelated normal Manager
    /// restart might wrongly consume within the staleness window.
    /// </summary>
    public Task DeleteAsync()
    {
        try
        {
            if (File.Exists(_file))
            {
                File.Delete(_file);
                _logger.Info("Runtime handoff discarded: the update apply that wrote it did not complete.");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not discard the runtime handoff after a failed update apply; it will still expire on its own in a few minutes. {ex.Message}");
        }
        return Task.CompletedTask;
    }

    /// <summary>Reads and deletes the handoff file (one-shot). Returns null if missing, malformed, or stale.</summary>
    public async Task<RuntimeHandoffDocument?> ConsumeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_file)) return null;

        RuntimeHandoffDocument? document = null;
        try
        {
            await using var stream = File.OpenRead(_file);
            document = await JsonSerializer.DeserializeAsync<RuntimeHandoffDocument>(stream, _json, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Runtime handoff file could not be read and will be discarded: {ex.Message}");
        }
        finally
        {
            try { File.Delete(_file); } catch { }
        }

        if (document is null) return null;

        if (document.FormatVersion != 1)
        {
            _logger.Warning($"Runtime handoff format version {document.FormatVersion} is not supported and was discarded.");
            return null;
        }

        var age = DateTime.UtcNow - document.CreatedUtc;
        if (age < TimeSpan.Zero || age > StaleAfter)
        {
            _logger.Warning($"Runtime handoff HandoffId={document.HandoffId:D} is stale (age={age.TotalSeconds:F0}s) and was discarded rather than trusted.");
            return null;
        }

        _logger.Info($"Runtime handoff consumed. HandoffId={document.HandoffId:D} Servers={document.Servers.Count} age={age.TotalSeconds:F0}s.");
        return document;
    }
}
