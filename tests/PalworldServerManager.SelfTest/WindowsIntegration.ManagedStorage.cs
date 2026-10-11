using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static partial class WindowsIntegration
{
    private static readonly Guid SettingsProfile = Guid.Parse("a70d86d6-cf51-49bb-aa5c-f243117c839e");
    private static readonly Guid MissingActiveProfile = Guid.Parse("996e54a7-8f8a-4eee-8e82-aa37e49cc166");
    private static string SettingsFile(string install) => Path.Combine(install, "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini");
    private static string PrepareManagedStorageFixture(string root, SecurityIdentifier service, string host, string publicRoot)
    {
        var parentBefore = new DirectoryInfo(root).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        var hostBefore = new DirectoryInfo(host).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        var path = Path.Combine(root, "ManagedServers"); var storage = new WindowsManagedStorageRoot(path, service);
        storage.RequireDisjoint(host, publicRoot, Path.Combine(root, "OwnerHandoffs")); storage.Provision();
        File.WriteAllText(Path.Combine(path, "preserved-setup.txt"), "SYNTHETIC-EXISTING");
        var install = Path.Combine(path, SettingsProfile.ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile(install))!);
        File.WriteAllText(SettingsFile(install), "OptionSettings=(ServerName=\"SYNTHETIC-ACTIVE\",ServerPassword=\"SYNTHETIC-SECRET\")", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(install, "DefaultPalWorldSettings.ini"), "OptionSettings=(ServerName=\"SYNTHETIC-DEFAULT\")");
        var missing = Path.Combine(path, MissingActiveProfile.ToString("N")); Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, "DefaultPalWorldSettings.ini"), "SYNTHETIC-MUST-NOT-COPY");
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
        var reader = new WindowsManagedServerSettingsReader(new WindowsManagedStorageRoot(root, service));
        var install = Path.Combine(root, SettingsProfile.ToString("N"));
        var activePath = SettingsFile(install); var defaultsPath = Path.Combine(install, "DefaultPalWorldSettings.ini");
        var activeBefore = File.ReadAllBytes(activePath); var defaultsBefore = File.ReadAllBytes(defaultsPath);
        var activeTime = File.GetLastWriteTimeUtc(activePath); var defaultsTime = File.GetLastWriteTimeUtc(defaultsPath);
        using (var files = reader.Read(SettingsProfile, install))
            Check(files.Active.Span.SequenceEqual(activeBefore) && files.InstallationDefaults!.Value.Span.SequenceEqual(defaultsBefore), "Managed settings service bytes differ.");
        var missing = Path.Combine(root, MissingActiveProfile.ToString("N"));
        try { using var files = reader.Read(MissingActiveProfile, missing); throw new Exception("Read created missing active settings."); }
        catch (BoundedFileReadException ex) when (ex.Failure == FileReadFailure.NotFound) { }
        try { using var files = reader.Read(SettingsProfile, missing); throw new Exception("Managed settings profile/path mismatch accepted."); }
        catch (BoundedFileReadException ex) when (ex.Failure == FileReadFailure.InvalidPath) { }
        Check(!File.Exists(SettingsFile(missing)) && File.ReadAllText(Path.Combine(missing, "DefaultPalWorldSettings.ini")) == "SYNTHETIC-MUST-NOT-COPY" &&
            File.ReadAllBytes(activePath).SequenceEqual(activeBefore) && File.ReadAllBytes(defaultsPath).SequenceEqual(defaultsBefore) &&
            File.GetLastWriteTimeUtc(activePath) == activeTime && File.GetLastWriteTimeUtc(defaultsPath) == defaultsTime, "Settings read mutated files.");
        Console.WriteLine("PASS integration: exact managed settings service reads, profile/path mismatch and missing-active refusal; original bytes/timestamps unchanged.");
    }
    private static void ManagedStorageDenied(string serviceName, string canary, string root)
    {
        var service = (SecurityIdentifier)new NTAccount("NT SERVICE", serviceName).Translate(typeof(SecurityIdentifier));
        try { new WindowsManagedStorageRoot(root, service).Provision(); throw new Exception("Nonadmin provisioned managed storage."); }
        catch (UnauthorizedAccessException) { }
        foreach (var action in new Action[] { () => { using var file = File.OpenRead(canary); }, () => File.WriteAllText(canary, "DENIED"),
            () => File.Delete(canary), () => Directory.CreateDirectory(Path.Combine(root, "unauthorized")) })
        { try { action(); throw new Exception("Nonadmin accessed managed storage."); } catch (UnauthorizedAccessException) { } }
        var install = Path.Combine(root, SettingsProfile.ToString("N"));
        try { using var file = File.OpenRead(SettingsFile(install)); throw new Exception("Nonadmin read raw settings."); }
        catch (UnauthorizedAccessException) { }
        try { using var files = new WindowsManagedServerSettingsReader(new WindowsManagedStorageRoot(root, service)).Read(SettingsProfile, install); throw new Exception("Nonadmin invoked protected settings adapter."); }
        catch (BoundedFileReadException ex) when (ex.Failure == FileReadFailure.AccessDenied) { }
    }
}
