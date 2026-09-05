using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using PalworldServerManager.Host.Persistence;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static class NativeTlsCacheTests
{
    public static async Task Lifecycle()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var store = new TestStore();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var original = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        store.Bytes = original.Export(X509ContentType.Pfx);
        var hostId = Guid.NewGuid();
        using var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, @"Global\PSMNativeCacheTest" + hostId.ToString("N"));
        Check(lease is not null, "Native cache test lacks its exclusive writer lease.");
        var cache = new WindowsHostTlsCredentialCache(hostId, identity.User!, store);
        string? name = null;
        byte[]? approvedDescriptor = null;
        try
        {
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await SecureStoreTests.Reject<OperationCanceledException>(() => cache.LoadAsync("current", canceled.Token));
                await SecureStoreTests.Reject<OperationCanceledException>(() => cache.ReconcileAsync([], canceled.Token));
            }
            await SecureStoreTests.Reject<ArgumentException>(() => cache.LoadAsync("../invalid"));
            await SecureStoreTests.Reject<ArgumentException>(() => cache.ReconcileAsync(["../invalid"]));
            using (var loaded = await cache.LoadAsync("current"))
            {
                Check(loaded.RawData.SequenceEqual(original.RawData), "Cache changed certificate identity.");
                using var cached = (ECDsaCng)loaded.GetECDsaPrivateKey()!; name = cached.Key.KeyName!;
                approvedDescriptor = cached.Key.GetProperty("Security Descr", (CngPropertyOptions)5).GetValue();
                var challenge = RandomNumberGenerator.GetBytes(32);
                Check(key.VerifyData(challenge, cached.SignData(challenge, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256), "Cached key mismatched authority.");
                try { cached.ExportPkcs8PrivateKey(); throw new Exception("Cache private key was exportable."); } catch (CryptographicException) { }
                await using var tls = await LocalIpcSpike.StartAsync(identity.User!, loaded);
                Check(await LocalIpcSpike.RequestAsync(tls.PipeName, tls.PublicPin) == identity.User!.Value, "Persisted cache failed actual Schannel TLS.");
            }
            using (var reopened = await new WindowsHostTlsCredentialCache(hostId, identity.User!, store).LoadAsync("current"))
            {
                using var cached = (ECDsaCng)reopened.GetECDsaPrivateKey()!;
                Check(cached.Key.KeyName == name, "Reopen created an unrelated native key.");
            }
            var material = store.Bytes; store.Bytes = null;
            await SecureStoreTests.Reject<CryptographicException>(() => cache.LoadAsync("current"));
            store.Bytes = material;
            using (var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            using (var other = new CertificateRequest("CN=localhost", otherKey, HashAlgorithmName.SHA256)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1)))
            {
                store.Bytes = other.Export(X509ContentType.Pfx);
                try { await SecureStoreTests.Reject<CryptographicException>(() => cache.LoadAsync("current")); }
                finally { CryptographicOperations.ZeroMemory(store.Bytes); store.Bytes = material; }
            }
            using (var native = CngKey.Open(name!, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey))
            {
                var file = new FileInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto", "Keys", native.UniqueName!));
                var good = file.GetAccessControl().GetSecurityDescriptorBinaryForm();
                var bad = file.GetAccessControl();
                bad.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
                file.SetAccessControl(bad);
                try
                {
                    await SecureStoreTests.Reject<UnauthorizedAccessException>(() => cache.LoadAsync("current"));
                    await SecureStoreTests.Reject<UnauthorizedAccessException>(() => cache.ReconcileAsync([]));
                }
                finally
                {
                    var restore = new FileSecurity(); restore.SetSecurityDescriptorBinaryForm(good, AccessControlSections.Access); file.SetAccessControl(restore);
                }
            }
            using (var repaired = await cache.LoadAsync("current")) Check(repaired.HasPrivateKey, "ACL fixture did not restore valid baseline.");
            await cache.ReconcileAsync(["current"]);
            Check(CngKey.Exists(name!, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey), "Retained current key was retired.");
            await cache.ReconcileAsync([]);
            Check(!CngKey.Exists(name!, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey), "Stale native key survived retirement.");
            // A pre-existing, exportable native object must never be adopted, even with this name.
            var weakenedParameters = new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                KeyCreationOptions = CngKeyCreationOptions.MachineKey,
                ExportPolicy = CngExportPolicies.AllowPlaintextExport
            };
            weakenedParameters.Parameters.Add(new CngProperty("Security Descr", approvedDescriptor!, (CngPropertyOptions)unchecked((int)0x80000005)));
            using (var weakened = CngKey.Create(CngAlgorithm.ECDsaP256, name, weakenedParameters))
            {
                try
                {
                    try { using var invalid = await cache.LoadAsync("current"); throw new Exception("Exportable cache was adopted."); }
                    catch (UnauthorizedAccessException ex) { Check(ex.Message == "Native cache protection policy is unsafe.", "Export-policy test failed for an unrelated reason."); }
                }
                finally { weakened.Delete(); }
            }
            using var rebuilt = await cache.LoadAsync("current");
            Check(rebuilt.RawData.SequenceEqual(original.RawData), "Missing cache could not rebuild from authority.");
        }
        finally
        {
            await cache.ReconcileAsync([]);
            if (store.Bytes is not null) CryptographicOperations.ZeroMemory(store.Bytes);
        }
    }
    private sealed class TestStore : ISecureCredentialStore
    {
        internal byte[]? Bytes;
        public Task<byte[]?> RetrieveAsync(string key, CancellationToken ct = default) => Task.FromResult(Bytes?.ToArray());
        public Task StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
