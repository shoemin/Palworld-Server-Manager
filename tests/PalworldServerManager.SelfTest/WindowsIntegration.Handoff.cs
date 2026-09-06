using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static partial class WindowsIntegration
{
    private static async Task HandoffProbe(string action, string ids, string path, string root)
    {
        var parts = ids.Split(':'); var host = Guid.Parse(parts[0]); var ticket = Guid.Parse(parts[1]);
        var reader = new WindowsOwnerHandoffReader(root, host, ticket);
        if (action == "handoff-denied")
        {
            try { File.ReadAllBytes(path); throw new Exception("Other user read intended-user handoff."); } catch (UnauthorizedAccessException) { }
            Check(await reader.ReadAsync() is null, "Reader crossed into another user's handoff.");
            return;
        }
        if (action == "handoff-unsafe")
        { await SecureStoreTests.Reject<UnauthorizedAccessException>(() => reader.ReadAsync()); return; }
        if (action == "handoff-malformed")
        { await SecureStoreTests.Reject<InvalidDataException>(() => reader.ReadAsync()); return; }
        if (action == "handoff-reparse")
        { await SecureStoreTests.Reject<IOException>(() => reader.ReadAsync()); return; }
        var bytes = await reader.ReadAsync() ?? throw new Exception("Recipient could not read its handoff.");
        try
        {
            using var value = OwnerHandoff.Parse(bytes, host, ticket); var secret = value.ExportSecretForTransport();
            try { Check(secret.All(b => b == 0xA5), "Handoff content changed."); }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        foreach (var attempt in new Action[] { () => File.WriteAllBytes(path, [1]), () => File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, "replacement.bin"), [1]) })
        { try { attempt(); throw new Exception("Recipient can modify or replace handoff content."); } catch (UnauthorizedAccessException) { } }
        using var identity = WindowsIdentity.GetCurrent();
        await SecureStoreTests.Reject<UnauthorizedAccessException>(() => new WindowsOwnerHandoffWriter(root).WriteAsync(host, Guid.NewGuid(), LocalEnrollmentPurpose.InitialOwner, identity.User!, new byte[32]));
        if (action == "handoff-peek") return;
        await reader.DeleteAsync(); await reader.DeleteAsync(); Check(await reader.ReadAsync() is null, "Consumed handoff was not deleted.");
    }
    private static async Task HandoffSuite(Guid host, string baseRoot, string executable, string userA, string userB, string password, string sidA, string shared)
    {
        var root = Path.Combine(baseRoot, "OwnerHandoffs"); var writer = new WindowsOwnerHandoffWriter(root); var recipient = new SecurityIdentifier(sidA);
        var secret = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        try
        {
            foreach (var purpose in new[] { LocalEnrollmentPurpose.InitialOwner, LocalEnrollmentPurpose.OwnerRotation, LocalEnrollmentPurpose.OwnerRehome })
            {
                var ticket = Guid.NewGuid(); var ids = host.ToString("D") + ":" + ticket.ToString("D");
                var path = Path.Combine(root, sidA, ticket.ToString("N") + ".bin");
                await writer.WriteAsync(host, ticket, purpose, recipient, secret);
                await SecureStoreTests.Reject<IOException>(() => writer.WriteAsync(host, ticket, purpose, recipient, secret));
                try { File.ReadAllBytes(path); throw new Exception("Administrator received ReadData on recipient handoff."); } catch (UnauthorizedAccessException) { }
                RunUser(executable, userB, password, "handoff-denied", ids, path, root, shared); // B also has product-group membership now
                RunUser(executable, userA, password, "handoff-peek", ids, path, root, shared);
                var savedAcl = new FileInfo(path).GetAccessControl();
                void Restore()
                {
                    // SetAccessControl persists only modified sections, not a clean previously-read object.
                    var restored = new FileSecurity(); restored.SetSecurityDescriptorBinaryForm(savedAcl.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access | AccessControlSections.Owner);
                    new FileInfo(path).SetAccessControl(restored);
                    Check(new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner) ==
                        savedAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner), "Fixture failed to restore original handoff ACL.");
                }
                var unsafeAcl = new FileInfo(path).GetAccessControl();
                unsafeAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(unsafeAcl);
                RunUser(executable, userA, password, "handoff-unsafe", ids, path, root, shared);
                Restore();
                var wrongOwner = new FileInfo(path).GetAccessControl(); wrongOwner.SetOwner(recipient);
                new FileInfo(path).SetAccessControl(wrongOwner);
                RunUser(executable, userA, password, "handoff-unsafe", ids, path, root, shared);
                Restore();
                RunUser(executable, userA, password, "handoff-read", ids, path, root, shared);
                Check(!File.Exists(path), "Recipient left consumed handoff artifact.");
            }
            var malformed = Guid.NewGuid(); await writer.WriteAsync(host, malformed, LocalEnrollmentPurpose.InitialOwner, recipient, secret);
            var malformedPath = Path.Combine(root, sidA, malformed.ToString("N") + ".bin");
            File.WriteAllBytes(malformedPath, [1]);
            RunUser(executable, userA, password, "handoff-malformed", host.ToString("D") + ":" + malformed.ToString("D"), malformedPath, root, shared);
            writer.DeletePrepared(malformed, recipient); writer.DeletePrepared(malformed, recipient);
            var linked = Guid.NewGuid(); var link = Path.Combine(root, sidA, linked.ToString("N") + ".bin");
            var target = Path.Combine(shared, "handoff-link-target"); File.WriteAllBytes(target, [7]); File.CreateSymbolicLink(link, target);
            RunUser(executable, userA, password, "handoff-reparse", host.ToString("D") + ":" + linked.ToString("D"), link, root, shared);
            await SecureStoreTests.Reject<IOException>(() => writer.WriteAsync(host, linked, LocalEnrollmentPurpose.InitialOwner, recipient, secret));
            try { writer.DeletePrepared(linked, recipient); throw new Exception("Writer followed handoff reparse point."); } catch (IOException) { }
            File.Delete(link); Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 7 }), "Rejected handoff link changed its target.");
            await SecureStoreTests.Reject<ArgumentException>(() => writer.WriteAsync(host, Guid.NewGuid(), LocalEnrollmentPurpose.InitialOwner,
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), secret));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await SecureStoreTests.Reject<OperationCanceledException>(() => writer.WriteAsync(host, Guid.NewGuid(), LocalEnrollmentPurpose.InitialOwner, recipient, secret, canceled.Token));
            Console.WriteLine("PASS integration: all Owner handoff purposes, recipient-only OS read/delete, group-only denial, no overwrite, unsafe ACL/partial artifact/reparse rejection");
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}
