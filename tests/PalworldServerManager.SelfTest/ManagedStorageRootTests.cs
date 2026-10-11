using System.Security.AccessControl;
using System.Security.Principal;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class ManagedStorageRootTests
{
    internal static readonly SecurityIdentifier Service = new("S-1-5-80-1-2-3-4-5");
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier Admin = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static WindowsManagedStorageRoot Policy() => new(@"C:\PSM-fixture\Servers", Service);
    private static void Check(bool ok) { if (!ok) throw new Exception("Managed storage policy assertion failed."); }
    private static void Refuses<T>(Action action) where T : Exception
    { try { action(); throw new Exception("Unsafe managed root accepted."); } catch (T) { } }
    internal static Task ExactPolicyAndOwners()
    {
        var policy = Policy(); var security = policy.NewSecurity(); policy.ValidateSecurity(security);
        foreach (var owner in new[] { Service, Admin, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
        { security.SetOwner(owner); policy.ValidateSecurity(security); }
        Check(security.AreAccessRulesProtected && security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Count == 3);
        return Task.CompletedTask;
    }
    internal static Task UnsafePolicyForms()
    {
        var policy = Policy();
        foreach (var change in new Action<DirectorySecurity>[]
        {
            acl => acl.SetOwner(Everyone),
            acl => acl.SetAccessRuleProtection(false, true),
            acl => acl.AddAccessRule(new(Everyone, FileSystemRights.Read, AccessControlType.Allow)),
            acl => acl.AddAccessRule(new(Service, FileSystemRights.Read, AccessControlType.Deny)),
            acl => acl.PurgeAccessRules(Service),
            acl => acl.SetAccessRule(new(Service, FileSystemRights.Read, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow)),
            acl => acl.SetAccessRule(new(Service, FileSystemRights.FullControl, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow)),
            acl => acl.SetAccessRule(new(Service, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow)),
            acl => acl.SetSecurityDescriptorSddlForm("O:BAG:BAD:PNO_ACCESS_CONTROL")
        })
        {
            var security = policy.NewSecurity(); change(security);
            Refuses<UnauthorizedAccessException>(() => policy.ValidateSecurity(security));
        }
        return Task.CompletedTask;
    }
    internal static Task ExactServiceAndDisjointPaths()
    {
        foreach (var sid in new[] { Everyone, Admin, new SecurityIdentifier("S-1-5-80-0"), new SecurityIdentifier("S-1-5-21-1-2-3-1001") })
            Refuses<ArgumentException>(() => new WindowsManagedStorageRoot(@"C:\PSM-fixture\Servers", sid));
        foreach (var path in new[] { "relative", @"\\host\share", @"\\?\C:\path", @"C:\file:stream", @"C:\", @"C:\PSM\Host.", @"C:\PSM\Host ", @"C:\PSM\ALIAS~1", @"C:\PSM\..\Servers", @"C:\NUL\Servers", "C:/PSM/Servers" })
            Refuses<ArgumentException>(() => new WindowsManagedStorageRoot(path, Service));
        var policy = Policy(); policy.RequireDisjoint(@"C:\PSM-fixture\Host", @"C:\PSM-fixture\PublicTrust", @"C:\PSM-fixture\OwnerHandoffs");
        foreach (var path in new[] { @"C:\PSM-fixture\SERVERS", @"C:\PSM-fixture", @"C:\PSM-fixture\Servers\child", @"C:\PSM-fixture\Other\..\Servers" })
            Refuses<ArgumentException>(() => policy.RequireDisjoint(path));
        policy.RequireDisjoint(@"C:\PSM-fixture\Servers-other");
        return Task.CompletedTask;
    }
    internal static Task CancellationPrecedesIo()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Refuses<OperationCanceledException>(() => Policy().Validate(cancel.Token));
        Refuses<OperationCanceledException>(() => Policy().Provision(cancel.Token));
        return Task.CompletedTask;
    }
}
