using System.Security.AccessControl;
using System.Security.Principal;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static partial class WindowsRestrictedLaunchQualification
{
    // Synthetic, disposable trees only. This does not provision a managed server.
    private sealed class OwnedGameFolders : IDisposable
    {
        private readonly WindowsManagedStorageRoot storage;
        private readonly List<string> created = [];
        private readonly Dictionary<string, string> security = [];
        internal string First { get; }
        internal string Second { get; }
        internal OwnedGameFolders(string root, SecurityIdentifier service, SecurityIdentifier first, SecurityIdentifier second, Action<string>? afterFirst = null)
        {
            storage = new(root, service); storage.Validate();
            First = Path.Combine(storage.Path, Guid.NewGuid().ToString("N"));
            Second = Path.Combine(storage.Path, Guid.NewGuid().ToString("N"));
            try { Create(First, service, first); afterFirst?.Invoke(First); Create(Second, service, second); storage.Validate(); }
            catch { Dispose(); throw; }
        }
        private sealed class PreparationInterrupted : Exception { }
        internal static void QualifyPreparationRollback(string root, SecurityIdentifier service, SecurityIdentifier first, SecurityIdentifier second)
        {
            string? prepared = null; var before = Security(root);
            try
            {
                using var unexpected = new OwnedGameFolders(root, service, first, second, path =>
                { prepared = path; throw new PreparationInterrupted(); });
                throw new Exception("Injected preparation interruption was skipped.");
            }
            catch (PreparationInterrupted) { }
            Check(prepared is not null && !Directory.Exists(prepared) && !File.Exists(prepared) && Security(root) == before,
                "Interrupted preparation left fixture state or changed managed root permissions.");
        }
        internal void RequireRemoved()
            => Check(created.Count == 0 && !Directory.Exists(First) && !Directory.Exists(Second) && !File.Exists(First) && !File.Exists(Second),
                "Owned game folders remained after cleanup.");
        private void Create(string path, SecurityIdentifier service, SecurityIdentifier child)
        {
            if (Path.GetDirectoryName(path) != storage.Path || Directory.Exists(path) || File.Exists(path))
                throw new IOException("Fresh fixture path required.");
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(service);
            foreach (var sid in new[] { service, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new(child, FileSystemRights.ReadAndExecute | FileSystemRights.Write | FileSystemRights.Synchronize, AccessControlType.Allow));
            acl.AddAccessRule(new(child, FileSystemRights.Modify, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
            new DirectoryInfo(path).Create(acl); created.Add(path);
            security.Add(path, Security(path));
            File.WriteAllText(Path.Combine(path, "canary.txt"), "HOST-PREPARED");
        }
        private static string Security(string path) => new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        internal void Verify()
        {
            storage.Validate();
            foreach (var path in created)
                Check(Security(path) == security[path] && File.ReadAllText(Path.Combine(path, "canary.txt")) == "CHILD-WRITTEN" &&
                    Directory.GetFileSystemEntries(path).Length == 1, "Game fixture content, permissions or cleanup mismatch.");
        }
        public void Dispose()
        {
            // Delete only exact fixture-owned regular files and empty directories; never follow links or recursively remove a supplied path.
            try { Cleanup(Second); } finally { Cleanup(First); }
        }
        private void Cleanup(string path)
        {
            if (!created.Contains(path)) return;
            storage.Validate();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture path redirected.");
            var childDirectory = Path.Combine(path, "child-created");
            if (Directory.Exists(childDirectory))
            {
                if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture child directory redirected.");
                DeleteFile(Path.Combine(childDirectory, "new.txt")); DeleteFile(Path.Combine(childDirectory, "moved.txt"));
                Directory.Delete(childDirectory);
            }
            DeleteFile(Path.Combine(path, "canary.txt"));
            Directory.Delete(path); created.Remove(path);
        }
        private static void DeleteFile(string file)
        {
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture file redirected.");
                File.Delete(file);
            }
            catch (FileNotFoundException) { }
        }
    }

    private static bool QualifyGameFiles(string own, string sibling, bool interrupt, ref int stage)
    {
        Check(File.ReadAllText(Path.Combine(own, "canary.txt")) == "HOST-PREPARED", "Own initial file unavailable.");
        stage = 71; File.WriteAllText(Path.Combine(own, "canary.txt"), "CHILD-WRITTEN");
        stage = 72; var directory = Path.Combine(own, "child-created"); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "new.txt"); var moved = Path.Combine(directory, "moved.txt");
        stage = 73; File.WriteAllText(file, "SYNTHETIC-CHILD");
        Check(File.ReadAllText(file) == "SYNTHETIC-CHILD", "Initial child write failed.");
        if (interrupt) return false; // Distinct intentional exit; parent must clean this partial write.
        File.Move(file, moved);
        Check(File.ReadAllText(moved) == "SYNTHETIC-CHILD", "Child file operations failed.");
        stage = 74; File.Delete(moved); Directory.Delete(directory);
        var replacement = new DirectorySecurity(); replacement.SetAccessRuleProtection(true, false);
        replacement.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        stage = 75;
        foreach (var action in new Action[] {
            () => new DirectoryInfo(own).SetAccessControl(replacement),
            () => new DirectoryInfo(sibling).SetAccessControl(replacement),
            () => { using var denied = File.OpenRead(Path.Combine(sibling, "canary.txt")); },
            () => File.WriteAllText(Path.Combine(sibling, "canary.txt"), "DENIED"),
            () => File.Delete(Path.Combine(sibling, "canary.txt")),
            () => Directory.CreateDirectory(Path.Combine(sibling, "denied")) })
        {
            try { action(); } catch (UnauthorizedAccessException) { stage++; continue; }
            throw new Exception("Restricted child accessed sibling or directory permissions.");
        }
        return true;
    }
}
