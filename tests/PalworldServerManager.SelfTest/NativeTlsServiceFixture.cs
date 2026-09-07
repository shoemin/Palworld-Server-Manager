using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using PalworldServerManager.Platform.Windows;
using PalworldServerManager.Platform.Contracts;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

// Synthetic workload using production material/cache/publication adapters. Full authoritative
// executable composition and principal ceremonies remain #42d2c/d3.
internal sealed class NativeTlsServiceFixture : IDisposable
{
    internal sealed record Config(Guid HostId, string GroupSid, string PublicDirectory);
    internal sealed record Ready(int ProcessId, string KeyName, string KeyFile, string Pipe, string Pin);
    private readonly IDisposable _runtime;
    private readonly Task _worker;
    internal NativeTlsServiceFixture(string service, string root, IDisposable runtime, CancellationToken stop)
    {
        _runtime = runtime;
        _worker = Task.Run(async () =>
        {
            try
            {
                // NCrypt must not run on the SCM start callback, or while it is StartPending.
                // The elevated harness signals after it observes SCM Running. The intentionally
                // minimal service DACL does not grant the virtual account QUERY_STATUS on itself.
                var started = Path.Combine(root, "tls-started.txt");
                while (true)
                {
                    stop.ThrowIfCancellationRequested();
                    if (File.Exists(started) && File.ReadAllText(started) == Environment.ProcessId.ToString()) break;
                    await Task.Delay(50, stop);
                }
                var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path.Combine(root, "tls-config.json")))!;
                using var identity = WindowsIdentity.GetCurrent();
                var store = new WindowsSecureCredentialStore(root, identity.User!);
                var material = new WindowsHostCredentialMaterial(store);
                // Actual service-account protected storage; this is a reload fixture, not a crash claim.
                var preparedPin = await material.EnsurePreparedAsync(config.HostId, "tls-rotation", null, stop);
                var recoveredPin = await new WindowsHostCredentialMaterial(store).EnsurePreparedAsync(config.HostId, "tls-rotation", null, stop);
                var verifiedPin = await material.EnsurePreparedAsync(config.HostId, "tls-rotation", preparedPin, stop);
                if (preparedPin != recoveredPin || preparedPin != verifiedPin) throw new Exception("Rotation material changed during protected-store reload.");
                await store.DeleteAsync("tls-rotation", stop);
                var existing = await store.RetrieveAsync("tls-current", stop);
                if (existing is null)
                {
                    await material.CreateAsync(config.HostId, "tls-current", stop);
                }
                else CryptographicOperations.ZeroMemory(existing);
                await material.EnsureEnrollmentKeyAsync(config.HostId, hasEnrollmentHistory: false, stop);
                var cache = new WindowsHostTlsCredentialCache(config.HostId, identity.User!, store);
                await cache.ReconcileAsync(["tls-current"], stop);
                using var certificate = await cache.LoadAsync("tls-current", stop);
                using var key = (ECDsaCng)certificate.GetECDsaPrivateKey()!;
                var publicKeyPin = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
                await material.ValidateAsync("tls-current", publicKeyPin, stop);
                await new WindowsLocalHostTrustPublisher(config.PublicDirectory, identity.User!).PublishAsync(new LocalHostTrustPublication(config.HostId, publicKeyPin), stop);
                await using var tls = await LocalIpcSpike.StartAsync(new SecurityIdentifier(config.GroupSid), certificate);
                var ready = new Ready(Environment.ProcessId, key.Key.KeyName!,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto", "Keys", key.Key.UniqueName!), tls.PipeName, tls.PublicPin);
                File.WriteAllText(Path.Combine(root, "tls-ready.tmp"), JsonSerializer.Serialize(ready));
                File.Move(Path.Combine(root, "tls-ready.tmp"), Path.Combine(root, "tls-ready.json"), true);
                await Task.Delay(Timeout.Infinite, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // Protected test-only diagnostics; no credential bytes or certificate dumps.
                File.WriteAllText(Path.Combine(root, "tls-error.txt"), ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
                throw;
            }
        });
    }
    internal static async Task<Ready> WaitReady(string root)
    {
        var startedPid = File.ReadAllLines(Path.Combine(root, "identity.txt"))[1];
        File.WriteAllText(Path.Combine(root, "tls-started.tmp"), startedPid);
        File.Move(Path.Combine(root, "tls-started.tmp"), Path.Combine(root, "tls-started.txt"), true);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var error = Path.Combine(root, "tls-error.txt");
            if (File.Exists(error)) throw new Exception("Service TLS worker failed: " + File.ReadAllText(error));
            var path = Path.Combine(root, "tls-ready.json");
            if (File.Exists(path))
            {
                var ready = JsonSerializer.Deserialize<Ready>(File.ReadAllText(path))!;
                var pid = int.Parse(File.ReadAllLines(Path.Combine(root, "identity.txt"))[1]);
                if (ready.ProcessId == pid) return ready;
            }
            await Task.Delay(50);
        }
        throw new System.TimeoutException("Service native TLS cache did not become ready.");
    }
    public void Dispose() { try { _worker.GetAwaiter().GetResult(); } finally { _runtime.Dispose(); } }
}
