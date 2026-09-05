using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static class SecureStoreTests
{
    public static Task Lifecycle() => WithStore((root, sid) => SecureCredentialStoreContractTests.Run(() => new WindowsSecureCredentialStore(root, sid)));
    public static Task NegativePaths() => WithStore(async (root, sid) =>
    {
        var store = new WindowsSecureCredentialStore(root, sid);
        var plain = Encoding.UTF8.GetBytes("SYNTHETIC-HOST-SECRET-NEVER-LOGGED-21ba479a");
        foreach (var key in new[] { "../escape", "x/y", "x\\y", "", "x:y", new string('x', 129) })
            await Reject<ArgumentException>(() => store.StoreAsync(key, plain));
        await Reject<ArgumentException>(() => store.StoreAsync("large", new byte[WindowsSecureCredentialStore.MaximumSecretBytes + 1]));
        await store.StoreAsync("a", plain);
        var directory = Path.Combine(root, "credentials"); var a = Directory.GetFiles(directory, "*.bin").Single();
        var cipher = File.ReadAllBytes(a);
        Check(cipher.AsSpan().IndexOf(plain) < 0 && !Encoding.UTF8.GetString(cipher).Contains(Convert.ToBase64String(plain)), "Plaintext representation on disk.");
        await store.StoreAsync("b", plain);
        var b = Directory.GetFiles(directory, "*.bin").Single(p => p != a);
        File.WriteAllBytes(b, cipher);
        await Reject<CryptographicException>(() => store.RetrieveAsync("b")); // key-bound entropy
        cipher[^1] ^= 1; File.WriteAllBytes(a, cipher);
        await Reject<CryptographicException>(() => store.RetrieveAsync("a"));
        File.WriteAllText(a, "legacy plaintext reusable token");
        await Reject<CryptographicException>(() => store.RetrieveAsync("a")); // no legacy plaintext fallback
        await store.StoreAsync("a", plain);
        File.SetAttributes(a, FileAttributes.ReadOnly);
        try
        {
            try { await store.StoreAsync("a", new byte[] { 9, 8, 7 }); throw new Exception("Expected replacement failure."); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Check((await store.RetrieveAsync("a"))!.SequenceEqual(plain), "Failed replacement lost last good value.");
            Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Failed write leaked temporary file.");
        }
        finally { File.SetAttributes(a, FileAttributes.Normal); }
        var interrupted = Path.ChangeExtension(a, null) + ".crash.tmp";
        using (var stream = new FileInfo(interrupted).Create(FileMode.CreateNew, FileSystemRights.Write,
            FileShare.None, 4096, FileOptions.WriteThrough, new FileInfo(a).GetAccessControl()))
            stream.Write(File.ReadAllBytes(a));
        await store.DeleteAsync("a");
        Check(!File.Exists(a) && !File.Exists(interrupted), "Retirement retained an interrupted encrypted write.");

    });
    public static Task AclRejection() => WithStore(async (root, sid) =>
    {
        var store = new WindowsSecureCredentialStore(root, sid); await store.StoreAsync("acl", new byte[] { 1 });
        var path = Directory.GetFiles(Path.Combine(root, "credentials"), "*.bin").Single();
        var info = new FileInfo(path); var original = info.GetAccessControl(); var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(acl);
        await Reject<UnauthorizedAccessException>(() => store.RetrieveAsync("acl"));
        await Reject<UnauthorizedAccessException>(() => store.StoreAsync("acl", new byte[] { 2 }));
        await Reject<UnauthorizedAccessException>(() => store.DeleteAsync("acl"));
        Restore();
        Check((await store.RetrieveAsync("acl"))!.SequenceEqual(new byte[] { 1 }), "ACL restore did not return to valid baseline.");
        acl = info.GetAccessControl();
        acl.PurgeAccessRules(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)); info.SetAccessControl(acl);
        await Reject<UnauthorizedAccessException>(() => store.RetrieveAsync("acl"));
        Restore();
        Check((await store.RetrieveAsync("acl"))!.SequenceEqual(new byte[] { 1 }), "Missing-SYSTEM repair did not return to valid baseline.");
        var directory = new DirectoryInfo(Path.Combine(root, "credentials")); var goodDirectory = directory.GetAccessControl();
        var badDirectory = directory.GetAccessControl(); badDirectory.SetAccessRuleProtection(false, true); directory.SetAccessControl(badDirectory);
        await Reject<UnauthorizedAccessException>(() => store.RetrieveAsync("acl"));
        var restoredDirectory = new DirectorySecurity();
        restoredDirectory.SetSecurityDescriptorBinaryForm(goodDirectory.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        directory.SetAccessControl(restoredDirectory);
        Check((await store.RetrieveAsync("acl"))!.SequenceEqual(new byte[] { 1 }), "Directory ACL restore failed.");
        void Restore()
        {
            var restored = new FileSecurity(); restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            info.SetAccessControl(restored);
        }
    });
    public static Task ConcurrentWriters() => WithStore(async (root, sid) =>
    {
        var stores = new[] { new WindowsSecureCredentialStore(root, sid), new WindowsSecureCredentialStore(root + Path.DirectorySeparatorChar, sid) };
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            await stores[i % 2].StoreAsync("shared", Enumerable.Repeat((byte)i, 2048).ToArray());
            var value = await stores[i % 2].RetrieveAsync("shared");
            Check(value is { Length: 2048 } && value.All(b => b == value[0]), "Concurrent writer exposed a partial credential.");
        })));
        await stores[0].DeleteAsync("shared");
        Check(Directory.GetFiles(Path.Combine(root, "credentials")).Length == 0, "Concurrent writes left orphan files.");
    });
    public static Task Redaction()
    {
        var sentinel = "synthetic-secret-90088297"; using var secret = new RedactedSecret(Encoding.UTF8.GetBytes(sentinel));
        var json = JsonSerializer.Serialize(new { Audit = new { Credential = secret }, Diagnostics = new object[] { secret } });
        Check(!json.Contains(sentinel) && !json.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(sentinel))) && json.Contains("[REDACTED]"), "Diagnostic JSON leaked secret.");
        Check($"log: {secret}" == "log: [REDACTED]", "Formatting leaked material.");
        Check(typeof(RedactedSecret).GetProperties().Length == 0, "Destructuring can read secret properties.");
        Check(Encoding.UTF8.GetString(secret.CopyBytes()) == sentinel, "Explicit cryptographic extraction failed.");
        try { JsonSerializer.Deserialize<RedactedSecret>(JsonSerializer.Serialize(sentinel)); throw new Exception("Diagnostic deserialization accepted secret."); }
        catch (JsonException ex) { Check(!ex.ToString().Contains(sentinel), "JSON error leaked input."); }
        secret.Dispose();
        try { secret.CopyBytes(); throw new Exception("Disposed secret exported."); } catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }
    private static async Task WithStore(Func<string, SecurityIdentifier, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "psm-secret-test-" + Guid.NewGuid().ToString("N"));
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!;
        var acl = WindowsHostPlatform.BuildHostDirectoryAcl(sid); acl.SetOwner(sid);
        new DirectoryInfo(root).Create(acl);
        try { await test(root, sid); }
        finally { Directory.Delete(root, true); } // exact unique test root, never caller-supplied
    }
    internal static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
