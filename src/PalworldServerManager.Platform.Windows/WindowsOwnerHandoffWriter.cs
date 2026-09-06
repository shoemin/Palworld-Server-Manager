using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using PalworldServerManager.Core.Security;

namespace PalworldServerManager.Platform.Windows;

// Trusted offline composition owns the machine lease and the ticket transaction.
// The raw secret never enters SQLite or ISecureCredentialStore through this adapter.
public sealed class WindowsOwnerHandoffWriter(string handoffDirectory)
{
    private readonly string _root = OwnerHandoffFileSecurity.Normalize(handoffDirectory);
    private static void Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Owner handoff preparation requires elevation.");
    }
    private static void RequireUser(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
        uint nameLength = 0, domainLength = 0;
        LookupAccountSid(null, bytes, null, ref nameLength, null, ref domainLength, out _);
        var name = new StringBuilder(checked((int)nameLength)); var domain = new StringBuilder(checked((int)domainLength));
        if (!LookupAccountSid(null, bytes, name, ref nameLength, domain, ref domainLength, out var type))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (type != 1 || sid == OwnerHandoffFileSecurity.Admin || sid == OwnerHandoffFileSecurity.System)
            throw new ArgumentException("The intended principal must be an exact Windows user account.");
    }
    public static void ValidateRecipient(SecurityIdentifier recipient) { Elevated(); RequireUser(recipient); }
    private string Prepare(SecurityIdentifier recipient)
    {
        Elevated(); RequireUser(recipient);
        OwnerHandoffFileSecurity.Ancestors(new DirectoryInfo(_root).Parent);
        CreateDirectory(_root, null);
        var directory = Path.Combine(_root, recipient.Value); CreateDirectory(directory, recipient);
        return directory;
    }
    private static void CreateDirectory(string path, SecurityIdentifier? recipient)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { new DirectoryInfo(path).Create((DirectorySecurity)OwnerHandoffFileSecurity.New(recipient, false)); }
        catch (DirectoryNotFoundException) { new DirectoryInfo(path).Create((DirectorySecurity)OwnerHandoffFileSecurity.New(recipient, false)); }
        OwnerHandoffFileSecurity.Directory(path, recipient);
    }
    public async Task WriteAsync(Guid hostId, Guid ticketId, LocalEnrollmentPurpose purpose, SecurityIdentifier recipient,
        ReadOnlyMemory<byte> secret, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (hostId == Guid.Empty || ticketId == Guid.Empty || secret.Length != 32 ||
            purpose is not LocalEnrollmentPurpose.InitialOwner and not LocalEnrollmentPurpose.OwnerRotation and not LocalEnrollmentPurpose.OwnerRehome)
            throw new ArgumentException("Invalid Owner handoff identity, purpose or secret size.");
        var directory = Prepare(recipient); var path = Path.Combine(directory, ticketId.ToString("N") + ".bin");
        var bytes = new byte[73]; "PSMOH001"u8.CopyTo(bytes); hostId.TryWriteBytes(bytes.AsSpan(8, 16)); ticketId.TryWriteBytes(bytes.AsSpan(24, 16));
        bytes[40] = checked((byte)purpose); secret.Span.CopyTo(bytes.AsSpan(41));
        var created = false;
        try
        {
            using (var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadPermissions,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough, (FileSecurity)OwnerHandoffFileSecurity.New(recipient, true)))
            {
                created = true; OwnerHandoffFileSecurity.Exact(stream.GetAccessControl(), recipient, true);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false); await stream.FlushAsync(ct).ConfigureAwait(false); stream.Flush(true);
            }
        }
        catch { if (created) File.Delete(path); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    // Cleanup after this caller's ticket transaction fails. Never enumerates or reads secrets.
    public void DeletePrepared(Guid ticketId, SecurityIdentifier recipient)
    {
        if (ticketId == Guid.Empty) throw new ArgumentException("Ticket identity required.");
        var directory = Prepare(recipient); var path = Path.Combine(directory, ticketId.ToString("N") + ".bin");
        try { OwnerHandoffFileSecurity.ValidateFile(path, recipient); File.Delete(path); }
        catch (FileNotFoundException) { }
    }
    [DllImport("advapi32.dll", EntryPoint = "LookupAccountSidW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountSid(string? system, byte[] sid, StringBuilder? name, ref uint nameLength,
        StringBuilder? domain, ref uint domainLength, out int use);
}
