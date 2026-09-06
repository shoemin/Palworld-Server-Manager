using System.Security.Principal;
using PalworldServerManager.Client.Platform.Contracts;

namespace PalworldServerManager.Client.Platform.Windows;

public sealed class WindowsLocalHostTrustReader(string publicDirectory, SecurityIdentifier serviceSid) : ILocalHostTrustReader
{
    private readonly string _rootPath = PublicTrustFileSecurity.Normalize(publicDirectory);
    private readonly PublicTrustFileSecurity _security = new(serviceSid);
    public async Task<LocalHostTrustAnchor> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var root = new DirectoryInfo(_rootPath);
            var rootAttributes = RequiredAttributes(root.FullName, "The Host has not provisioned its public trust directory.");
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe public trust directory.");
            _security.Root(root);
            var path = Path.Combine(root.FullName, "local-host-trust.json");
            var attributes = RequiredAttributes(path, "The Host has not published its local trust descriptor.");
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("Unsafe public trust file.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            _security.Exact(stream.GetAccessControl(), false); // validate the opened object, not a later path lookup
            if (stream.Length is 0 or > 8192) throw new LocalHostAuthenticationException("Invalid public Host trust descriptor size.");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            return LocalHostTrustAnchor.Parse(bytes);
        }
        catch (LocalHostTrustUnavailableException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { throw new LocalHostAuthenticationException("The public Host trust boundary could not be verified.", ex); }
    }
    private static FileAttributes RequiredAttributes(string path, string missingMessage)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { throw new LocalHostTrustUnavailableException(missingMessage); }
        catch (DirectoryNotFoundException) { throw new LocalHostTrustUnavailableException(missingMessage); }
    }

}
