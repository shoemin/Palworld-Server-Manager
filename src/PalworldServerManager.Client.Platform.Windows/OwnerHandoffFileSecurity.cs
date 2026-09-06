using System.Security.AccessControl;
using System.Security.Principal;

namespace PalworldServerManager.Client.Platform.Windows;

// Independently enforced by the ordinary-client assembly too; no cross-boundary dependency.
internal static class OwnerHandoffFileSecurity
{
    internal static readonly SecurityIdentifier Admin = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    internal static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier Installer = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private const FileSystemRights Maintenance = FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.ChangePermissions | FileSystemRights.Delete;
    internal static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || path.IndexOf(':', 2) >= 0)
            throw new ArgumentException("An absolute local handoff directory is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    private static void Forms(FileSystemSecurity security)
    {
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (raw.DiscretionaryAcl is null) throw new UnauthorizedAccessException("Handoff requires a DACL.");
        foreach (GenericAce ace in raw.DiscretionaryAcl)
            if (ace is not CommonAce common || common.IsCallback || common.AceQualifier is not AceQualifier.AccessAllowed and not AceQualifier.AccessDenied)
                throw new UnauthorizedAccessException("Unsupported handoff ACL form.");
    }
    internal static void Ancestors(DirectoryInfo? directory)
    {
        for (; directory is not null; directory = directory.Parent)
        {
            var attributes = File.GetAttributes(directory.FullName);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse handoff ancestor.");
            var security = directory.GetAccessControl(); Forms(security);
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner != Admin && owner != System && owner != Installer) throw new UnauthorizedAccessException("Untrusted handoff ancestor owner.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (sid == Admin || sid == System || sid == Installer) continue;
                const FileSystemRights replace = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                if ((rule.FileSystemRights & replace) != 0) throw new UnauthorizedAccessException("Unprivileged replacement of handoff path is possible.");
            }
        }
    }
    private static Dictionary<SecurityIdentifier, FileSystemRights> Policy(SecurityIdentifier? recipient, bool file) => new()
    {
        [Admin] = file ? Maintenance : FileSystemRights.FullControl,
        [System] = file ? Maintenance : FileSystemRights.FullControl,
        [recipient ?? Everyone] = file ? FileSystemRights.Read | FileSystemRights.Delete : FileSystemRights.ReadAndExecute
    };
    internal static void Exact(FileSystemSecurity security, SecurityIdentifier? recipient, bool file)
    {
        Forms(security);
        if (!security.AreAccessRulesProtected || !Admin.Equals(security.GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("Unsafe handoff ownership or inheritance.");
        var expected = Policy(recipient, file); var seen = new HashSet<SecurityIdentifier>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (!expected.TryGetValue(sid, out var rights) || !seen.Add(sid) || rule.AccessControlType != AccessControlType.Allow || rule.IsInherited ||
                rule.InheritanceFlags != InheritanceFlags.None || rule.PropagationFlags != PropagationFlags.None ||
                (rule.FileSystemRights & ~FileSystemRights.Synchronize) != (rights & ~FileSystemRights.Synchronize))
                throw new UnauthorizedAccessException("Unsafe handoff permissions.");
        }
        if (seen.Count != expected.Count) throw new UnauthorizedAccessException("Missing handoff permissions.");
    }
    internal static void Directory(string path, SecurityIdentifier? recipient)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse handoff directory.");
        Exact(new DirectoryInfo(path).GetAccessControl(), recipient, false);
    }
    internal static void ValidateFile(string path, SecurityIdentifier recipient)
    {
        if ((global::System.IO.File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("Unsafe handoff file.");
        Exact(new FileInfo(path).GetAccessControl(), recipient, true);
    }
}
