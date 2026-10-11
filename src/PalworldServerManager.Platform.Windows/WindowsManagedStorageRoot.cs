using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

public sealed class WindowsManagedStorageRoot : IManagedStorageRoot
{
    private static readonly SecurityIdentifier Admin = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private readonly SecurityIdentifier service;
    public string Path { get; }
    public WindowsManagedStorageRoot(string path, SecurityIdentifier serviceSid)
    {
        ArgumentNullException.ThrowIfNull(serviceSid);
        if (!serviceSid.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal) || serviceSid.Value.Split('-').Length != 9)
            throw new ArgumentException("An exact virtual service SID is required.");
        service = serviceSid; Path = Normalize(path);
    }
    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length is < 4 or > 32700 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            !System.IO.Path.IsPathFullyQualified(path) || path.IndexOf(':', 2) >= 0)
            throw new ArgumentException("An absolute local managed storage path is required.");
        var normalized = System.IO.Path.TrimEndingDirectorySeparator(path);
        // Shipped paths use canonical components, never DOS short-name or dot aliases.
        foreach (var part in normalized[3..].Split('\\'))
        {
            if (part.Length is < 1 or > 255 || part[^1] is '.' or ' ' || part.Any(c => c < 32 || c is '~' or '<' or '>' or ':' or '"' or '/' or '|' or '?' or '*'))
                throw new ArgumentException("Canonical managed storage components are required.");
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'))
                throw new ArgumentException("Device names are not storage components.");
        }
        normalized = System.IO.Path.GetFullPath(normalized);
        if (normalized == System.IO.Path.GetPathRoot(normalized)) throw new ArgumentException("A dedicated storage directory is required.");
        return normalized;
    }
    public void RequireDisjoint(params string[] protectedRoots)
    {
        ArgumentNullException.ThrowIfNull(protectedRoots);
        foreach (var root in protectedRoots)
        {
            var other = Normalize(root);
            if (Path.Equals(other, StringComparison.OrdinalIgnoreCase) || Path.StartsWith(other + "\\", StringComparison.OrdinalIgnoreCase) ||
                other.StartsWith(Path + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Managed storage overlaps protected Host resources.");
        }
    }
    internal DirectorySecurity NewSecurity()
    {
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(Admin);
        foreach (var sid in new[] { service, Admin, SystemSid })
            security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
    internal void ValidateSecurity(DirectorySecurity security)
    {
        var expected = new HashSet<SecurityIdentifier> { service, Admin, SystemSid };
        if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !expected.Contains(owner))
            throw new UnauthorizedAccessException("Unsafe managed storage ownership or inheritance.");
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (raw.DiscretionaryAcl is null || raw.DiscretionaryAcl.Count != 3) throw new UnauthorizedAccessException("Unsafe managed storage DACL.");
        foreach (GenericAce ace in raw.DiscretionaryAcl)
            if (ace is not CommonAce common || common.IsCallback || common.AceQualifier != AceQualifier.AccessAllowed ||
                common.AceFlags != (AceFlags.ContainerInherit | AceFlags.ObjectInherit))
                throw new UnauthorizedAccessException("Unsupported managed storage ACE.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (!expected.Remove((SecurityIdentifier)rule.IdentityReference) || rule.IsInherited || rule.AccessControlType != AccessControlType.Allow ||
                rule.InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) || rule.PropagationFlags != PropagationFlags.None ||
                rule.FileSystemRights != FileSystemRights.FullControl) throw new UnauthorizedAccessException("Unsafe managed storage permissions.");
        if (expected.Count != 0) throw new UnauthorizedAccessException("Missing managed storage authority.");
    }
    public void Validate(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); RequireLocalDrive(); var directory = new DirectoryInfo(Path);
        OwnerHandoffFileSecurity.Ancestors(directory.Parent);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Managed storage root is unavailable or redirected.");
        ValidateSecurity(directory.GetAccessControl()); ct.ThrowIfCancellationRequested();
    }
    // Called only by elevated offline composition after stopped-service/exclusivity gates.
    // Never repairs or recursively takes over existing data, nor grants parent replacement.
    public void Provision(CancellationToken ct = default)
    {
        WindowsHostPlatform.RequireOfflineElevation(ct);
        RequireLocalDrive();
        var directory = new DirectoryInfo(Path); OwnerHandoffFileSecurity.Ancestors(directory.Parent);
        if (!directory.Exists && !File.Exists(Path)) directory.Create(NewSecurity());
        Validate(ct);
    }
    private void RequireLocalDrive()
    {
        if (GetDriveTypeW(System.IO.Path.GetPathRoot(Path)!) is not (2 or 3 or 5 or 6))
            throw new IOException("Managed storage requires a local drive.");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint GetDriveTypeW(string root);
}
