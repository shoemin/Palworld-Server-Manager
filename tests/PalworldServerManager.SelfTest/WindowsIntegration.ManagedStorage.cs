using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static partial class WindowsIntegration
{
    private static string PrepareManagedStorageFixture(string root, SecurityIdentifier service, string host, string publicRoot)
    {
        var parentBefore = new DirectoryInfo(root).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        var hostBefore = new DirectoryInfo(host).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        var path = Path.Combine(root, "ManagedServers"); var storage = new WindowsManagedStorageRoot(path, service);
        storage.RequireDisjoint(host, publicRoot, Path.Combine(root, "OwnerHandoffs")); storage.Provision();
        File.WriteAllText(Path.Combine(path, "preserved-setup.txt"), "SYNTHETIC-EXISTING");
        var before = new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        storage.Provision(); storage.Validate();
        Check(new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access) == before &&
            File.ReadAllText(Path.Combine(path, "preserved-setup.txt")) == "SYNTHETIC-EXISTING", "Storage reprovision changed existing state.");

        var unsafeRoot = new WindowsManagedStorageRoot(Path.Combine(root, "UnsafeManaged"), service);
        var unsafeAcl = unsafeRoot.NewSecurity();
        unsafeAcl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        new DirectoryInfo(unsafeRoot.Path).Create(unsafeAcl);
        var unsafeBefore = new DirectoryInfo(unsafeRoot.Path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        try { unsafeRoot.Provision(); throw new Exception("Unsafe existing storage adopted."); } catch (UnauthorizedAccessException) { }
        Check(new DirectoryInfo(unsafeRoot.Path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access) == unsafeBefore, "Unsafe storage permissions were rewritten.");
        var filePath = Path.Combine(root, "StorageFile"); File.WriteAllText(filePath, "UNCHANGED");
        try { new WindowsManagedStorageRoot(filePath, service).Provision(); throw new Exception("File adopted as storage root."); } catch (IOException) { }
        Check(File.ReadAllText(filePath) == "UNCHANGED", "Existing file changed.");
        var missingParent = Path.Combine(root, "MissingStorageParent");
        try { new WindowsManagedStorageRoot(Path.Combine(missingParent, "Servers"), service).Provision(); throw new Exception("Missing storage parent silently created."); } catch (IOException) { }
        Check(!Directory.Exists(missingParent), "Missing parent created.");
        var link = Path.Combine(root, "StorageLink"); Directory.CreateSymbolicLink(link, host);
        try { try { new WindowsManagedStorageRoot(link, service).Provision(); throw new Exception("Storage followed a reparse root."); } catch (IOException) { } }
        finally { Directory.Delete(link); }
        Check(new DirectoryInfo(root).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access) == parentBefore &&
            new DirectoryInfo(host).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access) == hostBefore,
            "Storage preparation changed parent/private Host permissions.");
        Console.WriteLine("PASS integration: managed storage protected setup/idempotence, unsafe existing/file/reparse/missing-parent refusal, unchanged parent and private Host ACLs.");
        return path;
    }
    internal static void QualifyManagedStorage(string root, SecurityIdentifier service)
    {
        new WindowsManagedStorageRoot(root, service).Validate();
        var canary = Path.Combine(root, "service-canary.txt");
        if (!File.Exists(canary))
        {
            using var file = new FileStream(canary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(Encoding.UTF8.GetBytes("SYNTHETIC-SERVICE-STORAGE")); file.Flush(true);
        }
        Check(Encoding.UTF8.GetString(new WindowsBoundedFileReader().Read(root, ["service-canary.txt"], 100)) == "SYNTHETIC-SERVICE-STORAGE",
            "Service storage write/read failed or changed across restart.");
    }
    private static void ManagedStorageDenied(string serviceName, string canary, string root)
    {
        var service = (SecurityIdentifier)new NTAccount("NT SERVICE", serviceName).Translate(typeof(SecurityIdentifier));
        try { new WindowsManagedStorageRoot(root, service).Provision(); throw new Exception("Nonadmin provisioned managed storage."); }
        catch (UnauthorizedAccessException) { }
        foreach (var action in new Action[] { () => { using var file = File.OpenRead(canary); }, () => File.WriteAllText(canary, "DENIED"),
            () => File.Delete(canary), () => Directory.CreateDirectory(Path.Combine(root, "unauthorized")) })
        { try { action(); throw new Exception("Nonadmin accessed managed storage."); } catch (UnauthorizedAccessException) { } }
    }
}
