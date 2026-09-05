using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
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
        var cache = new WindowsHostTlsCredentialCache(hostId, identity.User!, store);
        string? name = null;
        try
        {
            using (var loaded = await cache.LoadAsync("current"))
            {
                Check(loaded.RawData.SequenceEqual(original.RawData), "Cache changed certificate identity.");
                using var cached = (ECDsaCng)loaded.GetECDsaPrivateKey()!; name = cached.Key.KeyName!;
                var challenge = RandomNumberGenerator.GetBytes(32);
                Check(key.VerifyData(challenge, cached.SignData(challenge, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256), "Cached key mismatched authority.");
                try { cached.ExportPkcs8PrivateKey(); throw new Exception("Cache private key was exportable."); } catch (CryptographicException) { }
            }
            using (var reopened = await new WindowsHostTlsCredentialCache(hostId, identity.User!, store).LoadAsync("current"))
            {
                using var cached = (ECDsaCng)reopened.GetECDsaPrivateKey()!;
                Check(cached.Key.KeyName == name, "Reopen created an unrelated native key.");
            }
            var material = store.Bytes; store.Bytes = null;
            await SecureStoreTests.Reject<CryptographicException>(() => cache.LoadAsync("current"));
            store.Bytes = material;
            await cache.ReconcileAsync(["current"]);
            Check(CngKey.Exists(name!, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey), "Retained current key was retired.");
            await cache.ReconcileAsync([]);
            Check(!CngKey.Exists(name!, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey), "Stale native key survived retirement.");
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
