using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

public sealed class WindowsLocalHostTrustPublisher : ILocalHostTrustPublisher
{
    public const string DescriptorFileName = "local-host-trust.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly DirectoryInfo _root;
    private readonly PublicTrustFileSecurity _security;
    private readonly SemaphoreSlim _gate;
    public WindowsLocalHostTrustPublisher(string publicDirectory, SecurityIdentifier serviceSid)
    {
        _root = new(PublicTrustFileSecurity.Normalize(publicDirectory));
        _security = new(serviceSid); _gate = Gates.GetOrAdd(_root.FullName, _ => new(1, 1));
    }
    // Elevated installer/offline composition only, under its machine lease. Never repairs/adopts
    // an existing permissive directory; public read must not create a publication-write path.
    public static void Provision(string publicDirectory, SecurityIdentifier serviceSid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Public trust provisioning requires elevation.");
        var directory = new DirectoryInfo(PublicTrustFileSecurity.Normalize(publicDirectory));
        var security = new PublicTrustFileSecurity(serviceSid); security.Ancestors(directory.Parent);
        if (!directory.Exists) directory.Create(security.NewDirectory());
        security.Root(directory);
    }
    public async Task PublishAsync(LocalHostTrustPublication publication, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var bytes = publication.ToJson();
        await _gate.WaitAsync(ct);
        string? ownedTemporary = null;
        try
        {
            _security.Root(_root);
            var target = Path.Combine(_root.FullName, DescriptorFileName);
            bool targetExists;
            try { _ = File.GetAttributes(target); targetExists = true; }
            catch (FileNotFoundException) { targetExists = false; }
            if (targetExists) ValidateFile(target);
            foreach (var temporary in _root.EnumerateFiles("trust-*.tmp"))
            {
                if (!Guid.TryParseExact(temporary.Name[6..^4], "N", out _)) continue;
                ValidateFile(temporary.FullName); temporary.Delete();
            }
            var temp = new FileInfo(Path.Combine(_root.FullName, "trust-" + Guid.NewGuid().ToString("N") + ".tmp"));
            using (var stream = temp.Create(FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough, _security.NewFile()))
            {
                ownedTemporary = temp.FullName;
                await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            // Same-directory replacement; write-through also waits for the move to reach disk.
            if (!MoveFileEx(temp.FullName, target, 1 | 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            ownedTemporary = null;
            ValidateFile(target);
        }
        finally
        {
            try { if (ownedTemporary is not null) File.Delete(ownedTemporary); }
            finally { _gate.Release(); }
        }
    }
    private void ValidateFile(string path)
    {
        var file = new FileInfo(path);
        if ((file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("Unsafe public trust file.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        _security.Exact(stream.GetAccessControl(), false);
    }
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string target, uint flags);
}
