using System.Text.Json;
using PalworldServerManager.Core.Infrastructure;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

public sealed class ProfileRegistry
{
    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public ProfileRegistry(AppPaths paths, IAppLogger logger)
    {
        _paths = paths;
        _logger = logger;
        _paths.EnsureCreated();
    }

    public async Task<List<ServerProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.ProfilesFile))
        {
            _logger.Debug($"Server registry does not exist yet at '{_paths.ProfilesFile}'.");
            return [];
        }
        try
        {
            await using var stream = File.OpenRead(_paths.ProfilesFile);
            var profiles = await JsonSerializer.DeserializeAsync<List<ServerProfile>>(stream, _json, cancellationToken) ?? [];
            _logger.Debug($"Loaded server registry with {profiles.Count} profile(s).");
            return profiles;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to load server registry.", ex);
            throw;
        }
    }

    public async Task SaveAsync(IEnumerable<ServerProfile> profiles, CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var temp = _paths.ProfilesFile + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, profiles, _json, cancellationToken);
        File.Move(temp, _paths.ProfilesFile, true);
        _logger.Debug($"Saved server registry to '{_paths.ProfilesFile}'.");
    }

    public async Task AddAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        var profiles = await LoadAsync(cancellationToken);
        if (profiles.Any(p => p.Id == profile.Id)) throw new InvalidOperationException("Server profile already exists.");
        profiles.Add(profile);
        await SaveAsync(profiles, cancellationToken);
        _logger.Info($"Registered managed server '{profile.Name}' id={profile.Id:D}.");
    }

    public static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static bool SamePath(string left, string right)
        => string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
}
