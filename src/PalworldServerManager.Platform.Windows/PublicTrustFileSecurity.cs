using System.Security.AccessControl;
using System.Security.Principal;

namespace PalworldServerManager.Platform.Windows;

// Public-artifact security is independently enforced on both sides of the platform boundary.
// No ordinary-client dependency on Host-side Platform assemblies is introduced.
internal sealed class PublicTrustFileSecurity(SecurityIdentifier serviceSid)
{
    private readonly SecurityIdentifier[] _writers = [serviceSid, new(WellKnownSidType.BuiltinAdministratorsSid, null), new(WellKnownSidType.LocalSystemSid, null)];
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    internal static string Normalize(string root)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\") || root.IndexOf(':', 2) >= 0)
            throw new ArgumentException("An absolute local trust directory is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
    internal void Ancestors(DirectoryInfo? directory)
    {
        for (; directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe public trust path.");
            var security = directory.GetAccessControl();
            ValidateAceForms(security);
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null || (!_writers.Contains(owner) && owner != TrustedInstaller)) throw new UnauthorizedAccessException("Untrusted public-path owner.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (_writers.Contains(sid) || sid == TrustedInstaller) continue;
                const FileSystemRights destructive = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                if ((rule.FileSystemRights & destructive) != 0) throw new UnauthorizedAccessException("An unprivileged identity can replace the public trust path.");
            }
        }
    }
    internal void Root(DirectoryInfo directory)
    {
        directory.Refresh();
        Ancestors(directory.Parent);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe public trust directory.");
        Exact(directory.GetAccessControl(), directory: true);
    }
    private static void ValidateAceForms(FileSystemSecurity security)
    {
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (raw.DiscretionaryAcl is null) throw new UnauthorizedAccessException("Public trust requires a DACL.");
        foreach (GenericAce ace in raw.DiscretionaryAcl)
            if (ace is not CommonAce common || common.IsCallback ||
                common.AceQualifier is not AceQualifier.AccessAllowed and not AceQualifier.AccessDenied)
                throw new UnauthorizedAccessException("Unsupported public trust ACL entry.");
    }
    internal void Exact(FileSystemSecurity security, bool directory)
    {
        ValidateAceForms(security);
        if (!security.AreAccessRulesProtected || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !_writers.Contains(owner))
            throw new UnauthorizedAccessException("Public trust ownership/protection is unsafe.");
        var seen = new HashSet<SecurityIdentifier>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            var expected = _writers.Contains(sid) ? FileSystemRights.FullControl :
                sid == Everyone ? (directory ? FileSystemRights.ReadAndExecute : FileSystemRights.Read) : 0;
            if (expected == 0 || rule.AccessControlType != AccessControlType.Allow || rule.IsInherited ||
                rule.PropagationFlags != PropagationFlags.None ||
                rule.InheritanceFlags != (directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None) ||
                (rule.FileSystemRights & ~FileSystemRights.Synchronize) != (expected & ~FileSystemRights.Synchronize))
                throw new UnauthorizedAccessException("Public trust permissions are unsafe.");
            seen.Add(sid);
        }
        if (_writers.Any(sid => !seen.Contains(sid)) || !seen.Contains(Everyone)) throw new UnauthorizedAccessException("Public trust lacks required access.");
    }
    internal DirectorySecurity NewDirectory()
    {
        var security = new DirectorySecurity(); Fill(security, true, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)); return security;
    }
    internal FileSecurity NewFile()
    {
        using var current = WindowsIdentity.GetCurrent();
        var owner = current.User == serviceSid || current.User!.IsWellKnown(WellKnownSidType.LocalSystemSid)
            ? current.User! : new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new FileSecurity(); Fill(security, false, owner); return security;
    }
    private void Fill(FileSystemSecurity security, bool directory, SecurityIdentifier owner)
    {
        security.SetAccessRuleProtection(true, false); security.SetOwner(owner);
        var inheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var sid in _writers.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Everyone, directory ? FileSystemRights.ReadAndExecute : FileSystemRights.Read, inheritance, PropagationFlags.None, AccessControlType.Allow));
    }
}
