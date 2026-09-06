using System.Security.Cryptography;
using System.Security.Principal;
using PalworldServerManager.Client.Platform.Contracts;

namespace PalworldServerManager.Client.Platform.Windows;

// Accepts public Host/ticket identifiers, never another user's SID or a secret-bearing path.
public sealed class WindowsOwnerHandoffReader : IOwnerBootstrapHandoffReader
{
    private readonly string _root;
    private readonly Guid _host, _ticket;
    public WindowsOwnerHandoffReader(string handoffDirectory, Guid hostId, Guid ticketId)
    {
        _root = OwnerHandoffFileSecurity.Normalize(handoffDirectory);
        if (hostId == Guid.Empty || ticketId == Guid.Empty) throw new ArgumentException("Host and ticket identities required.");
        _host = hostId; _ticket = ticketId;
    }
    private (string Path, SecurityIdentifier Recipient) Locate()
    {
        using var identity = WindowsIdentity.GetCurrent(); var recipient = identity.User ?? throw new UnauthorizedAccessException("Current user identity required.");
        OwnerHandoffFileSecurity.Ancestors(new DirectoryInfo(_root).Parent);
        OwnerHandoffFileSecurity.Directory(_root, null);
        var directory = Path.Combine(_root, recipient.Value); OwnerHandoffFileSecurity.Directory(directory, recipient);
        var path = Path.Combine(directory, _ticket.ToString("N") + ".bin");
        OwnerHandoffFileSecurity.ValidateFile(path, recipient); return (path, recipient);
    }
    public async Task<byte[]?> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); byte[]? bytes = null;
        try
        {
            var (path, recipient) = Locate();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            OwnerHandoffFileSecurity.Exact(stream.GetAccessControl(), recipient, true);
            if (stream.Length != 73) throw new InvalidDataException("Invalid Owner handoff size.");
            bytes = new byte[73]; await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            using var parsed = OwnerHandoff.Parse(bytes, _host, _ticket);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); throw; }
    }
    // Caller invokes only after successful Host consumption and durable client binding.
    // The recipient can delete but cannot create/replace entries in its protected directory.
    public Task DeleteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try { var (path, _) = Locate(); File.Delete(path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return Task.CompletedTask;
    }
}
