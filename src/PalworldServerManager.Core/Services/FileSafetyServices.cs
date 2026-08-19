using System.Security.Cryptography;
using PalworldServerManager.Core.Models;

namespace PalworldServerManager.Core.Services;

public static class FileCopyService
{
    public static void CopyDirectory(string source, string destination, bool overwrite = true)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }
}

public static class DirectoryHashService
{
    public static async Task<Dictionary<string, PortableFileHash>> HashTreeAsync(string root, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, PortableFileHash>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return result;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            result[relative] = new PortableFileHash
            {
                Path = relative,
                Sha256 = Convert.ToHexString(hash),
                Length = stream.Length
            };
        }
        return result;
    }

    public static bool Equivalent(IReadOnlyDictionary<string, PortableFileHash> a, IReadOnlyDictionary<string, PortableFileHash> b, out string difference)
    {
        if (a.Count != b.Count)
        {
            difference = $"File count changed from {a.Count} to {b.Count}.";
            return false;
        }

        foreach (var pair in a)
        {
            if (!b.TryGetValue(pair.Key, out var other))
            {
                difference = $"File disappeared: {pair.Key}";
                return false;
            }
            if (!string.Equals(pair.Value.Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase) || pair.Value.Length != other.Length)
            {
                difference = $"File content changed: {pair.Key}";
                return false;
            }
        }

        difference = string.Empty;
        return true;
    }
}
