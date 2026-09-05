using System.Security.AccessControl;
using System.Security.Principal;

namespace PalworldServerManager.Platform.Windows;

/// <summary>Elevated provisioning under the stopped-Host machine lease. Grants only creation
/// of new machine-key files to the registered virtual service; never access to other keys.</summary>
public static class WindowsNativeTlsProvisioning
{
    public static bool EnsureCreatePermission(SecurityIdentifier serviceSid)
    {
        var directory = DirectoryFor(serviceSid);
        var security = directory.GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(rule => rule.IdentityReference == serviceSid).ToArray();
        if (rules.Length != 0)
        {
            if (rules.Length != 1 || !Exact(rules[0])) throw new UnauthorizedAccessException("Unexpected native directory service permissions require privileged repair.");
            return false;
        }
        security.AddAccessRule(Rule(serviceSid));
        directory.SetAccessControl(security);
        return true;
    }
    // Caller removes only a grant it installed, after stopping the Host and retiring its caches.
    public static void RemoveCreatePermission(SecurityIdentifier serviceSid)
    {
        var directory = DirectoryFor(serviceSid); var security = directory.GetAccessControl();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            if (rule.IdentityReference == serviceSid && Exact(rule)) security.RemoveAccessRuleSpecific(rule);
        directory.SetAccessControl(security);
    }
    private static bool Exact(FileSystemAccessRule rule) => !rule.IsInherited && rule.AccessControlType == AccessControlType.Allow &&
        rule.InheritanceFlags == InheritanceFlags.None && rule.PropagationFlags == PropagationFlags.None &&
        (rule.FileSystemRights & ~FileSystemRights.Synchronize) == FileSystemRights.CreateFiles;
    private static FileSystemAccessRule Rule(SecurityIdentifier sid) => new(sid, FileSystemRights.CreateFiles,
        InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow);
    private static DirectoryInfo DirectoryFor(SecurityIdentifier sid)
    {
        ArgumentNullException.ThrowIfNull(sid);
        if (!sid.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal) || sid.Value.Split('-').Length != 9)
            throw new ArgumentException("A registered virtual-service SID is required.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Native TLS provisioning requires elevation.");
        var directory = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto", "Keys"));
        for (DirectoryInfo? ancestor = directory; ancestor is not null; ancestor = ancestor.Parent)
            if (!ancestor.Exists || (ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Native key path is unsafe.");
        var acl = directory.GetAccessControl(); var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !acl.AreAccessRulesProtected || (!owner.IsWellKnown(WellKnownSidType.LocalSystemSid) && !owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
            throw new UnauthorizedAccessException("Native key directory ownership/protection is unsafe.");
        return directory;
    }
}
