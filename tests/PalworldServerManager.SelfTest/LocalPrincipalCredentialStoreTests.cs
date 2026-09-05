using System.Text;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;

namespace PalworldServerManager.SelfTest;

// #41 client credential-store tests (SS3a, LOCAL-002, CLIENT-002, CLIENT-003, SEC-001).
//
// Every test uses a dedicated temporary storage directory - never the real
// %LOCALAPPDATA%\PalworldServerManager path - so a developer's own credential is never touched.
public static class LocalPrincipalCredentialStoreTests
{
    /// <summary>
    /// Deterministic FAKE key generator, for storage-lifecycle coverage ONLY.
    ///
    /// This is explicitly NOT a production cryptographic implementation and selects no real
    /// signature algorithm - #42 owns that decision. Its existence is precisely what lets #41
    /// implement DPAPI storage without stealing #42's choice.
    /// </summary>
    private sealed class FakeKeyPairGenerator : ILocalPrincipalKeyPairGenerator
    {
        private int _counter;

        public int GenerateCallCount => _counter;

        public LocalPrincipalKeyMaterial Generate()
        {
            var n = ++_counter;
            return new LocalPrincipalKeyMaterial(
                AlgorithmId: "test-fake-not-production",
                PrivateKeyBlob: Encoding.UTF8.GetBytes($"FAKE-PRIVATE-{n}-{Guid.NewGuid():N}"),
                PublicKeyBlob: Encoding.UTF8.GetBytes($"FAKE-PUBLIC-{n}"));
        }
    }

    private sealed class TempClientRoot : IDisposable
    {
        public TempClientRoot()
            => Directory = Path.Combine(Path.GetTempPath(), "psm-client-" + Guid.NewGuid().ToString("N"));

        public string Directory { get; }

        public void Dispose()
        {
            try { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
            catch (IOException) { }
        }
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"{what}: expected {expected}, got {actual}");
        }
    }

    private static void True(bool condition, string what)
    {
        if (!condition) throw new Exception($"Expected condition to hold: {what}");
    }

    public static async Task TestCredentialLifecycleFromCreateThroughBindToDelete()
    {
        using var temp = new TempClientRoot();
        var generator = new FakeKeyPairGenerator();
        var store = new WindowsLocalPrincipalCredentialStore(generator, temp.Directory);

        // Nothing yet.
        Equal(false, await store.HasCredentialAsync(), "HasCredential false before any create");
        Equal(null, await store.LoadAsync(), "Load null before any create");

        // Create an unbound key.
        var created = await store.CreateAndStoreAsync();
        Equal("test-fake-not-production", created.AlgorithmId, "algorithm id round-trips from the injected generator");
        Equal(1, generator.GenerateCallCount, "exactly one generation");

        // Idempotent while unbound: same key, no regeneration.
        var again = await store.CreateAndStoreAsync();
        Equal(1, generator.GenerateCallCount, "repeat create while unbound does NOT regenerate");
        True(created.PublicKeyBlob.SequenceEqual(again.PublicKeyBlob), "repeat create returns the identical key");

        // Still unbound: not yet a usable credential.
        Equal(false, await store.HasCredentialAsync(), "HasCredential false while unbound");
        Equal(null, await store.LoadAsync(), "Load null while unbound");

        // Bind.
        await store.BindPrincipalIdAsync("principal-123");
        Equal(true, await store.HasCredentialAsync(), "HasCredential true once bound");

        var loaded = await store.LoadAsync();
        True(loaded is not null, "Load returns the binding");
        Equal("principal-123", loaded!.LocalPrincipalId, "bound principal id round-trips");
        True(created.PublicKeyBlob.SequenceEqual(loaded.PublicKeyBlob), "the bound credential keeps the same key");

        // Delete, then a fresh create yields NEW material.
        await store.DeleteAsync();
        Equal(false, await store.HasCredentialAsync(), "HasCredential false after delete");
        Equal(null, await store.LoadAsync(), "Load null after delete");

        var fresh = await store.CreateAndStoreAsync();
        Equal(2, generator.GenerateCallCount, "create after delete regenerates");
        True(!created.PublicKeyBlob.SequenceEqual(fresh.PublicKeyBlob), "fresh material differs from the deleted key");
    }

    public static async Task TestCredentialSurvivesRestartAndIsSharedBySameUserConsumers()
    {
        using var temp = new TempClientRoot();

        // First "process": create and bind.
        var first = new WindowsLocalPrincipalCredentialStore(new FakeKeyPairGenerator(), temp.Directory);
        await first.CreateAndStoreAsync();
        await first.BindPrincipalIdAsync("principal-restart");
        var before = await first.LoadAsync();

        // A completely separate store instance over the same per-user path stands in both for a
        // process restart AND for the other client consumer (Client.Avalonia vs Client.Cli run as
        // the same OS user must resolve the SAME binding - CLIENT-003).
        var second = new WindowsLocalPrincipalCredentialStore(new FakeKeyPairGenerator(), temp.Directory);
        var after = await second.LoadAsync();

        True(after is not null, "the binding survives a restart");
        Equal(before!.LocalPrincipalId, after!.LocalPrincipalId, "same principal id from the other consumer");
        True(before.PrivateKeyBlob.SequenceEqual(after.PrivateKeyBlob), "same private key from the other consumer");
    }

    public static async Task TestNoPlaintextPrivateKeyOnDisk()
    {
        using var temp = new TempClientRoot();
        var generator = new FakeKeyPairGenerator();
        var store = new WindowsLocalPrincipalCredentialStore(generator, temp.Directory);

        await store.CreateAndStoreAsync();
        await store.BindPrincipalIdAsync("principal-secret-check");

        var loaded = await store.LoadAsync();
        var privateKey = loaded!.PrivateKeyBlob;
        var onDisk = await File.ReadAllBytesAsync(store.FilePath);

        // The DPAPI-protected file must not contain the private key material anywhere.
        var haystack = Convert.ToHexString(onDisk);
        var needle = Convert.ToHexString(privateKey);
        True(!haystack.Contains(needle, StringComparison.Ordinal), "no plaintext private key on disk");

        // Nor the recognizable plaintext prefix of the fake material.
        var prefix = Convert.ToHexString(Encoding.UTF8.GetBytes("FAKE-PRIVATE-"));
        True(!haystack.Contains(prefix, StringComparison.Ordinal), "no recognizable plaintext key prefix on disk");
    }

    public static async Task TestStoreExposesNoHostMachineCredentialSurface()
    {
        using var temp = new TempClientRoot();
        var store = new WindowsLocalPrincipalCredentialStore(new FakeKeyPairGenerator(), temp.Directory);
        await store.CreateAndStoreAsync();

        // CLIENT-002: there is structurally no way to reach a Host machine credential through
        // this store - assert the interface surface itself, not merely current behavior.
        var members = typeof(ILocalPrincipalCredentialStore).GetMembers()
            .Select(m => m.Name)
            .Where(n => n.Contains("Host", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Machine", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Equal(0, members.Count, $"no Host/machine-credential member exists; found: {string.Join(", ", members)}");
    }

    public static async Task TestInterruptedWriteDoesNotCorruptLastGoodCredential()
    {
        using var temp = new TempClientRoot();
        var store = new WindowsLocalPrincipalCredentialStore(new FakeKeyPairGenerator(), temp.Directory);

        await store.CreateAndStoreAsync();
        await store.BindPrincipalIdAsync("principal-good");
        var good = await store.LoadAsync();

        // Simulate a crashed write: a leftover temp file must never be mistaken for the real one.
        await File.WriteAllTextAsync(store.FilePath + ".tmp", "garbage-from-an-interrupted-write");

        var reopened = new WindowsLocalPrincipalCredentialStore(new FakeKeyPairGenerator(), temp.Directory);
        var after = await reopened.LoadAsync();

        True(after is not null, "the last good credential still loads");
        Equal(good!.LocalPrincipalId, after!.LocalPrincipalId, "last good credential is intact");
        True(good.PrivateKeyBlob.SequenceEqual(after.PrivateKeyBlob), "last good key is intact");
    }

    private static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        catch (Exception ex) { throw new Exception($"{what}: expected {typeof(TException).Name} but got {ex.GetType().Name}"); }
        throw new Exception($"{what}: expected {typeof(TException).Name} but nothing was thrown");
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string what) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        catch (Exception ex) { throw new Exception($"{what}: expected {typeof(TException).Name} but got {ex.GetType().Name}"); }
        throw new Exception($"{what}: expected {typeof(TException).Name} but nothing was thrown");
    }

    public static async Task TestConcurrentCreateAcrossTwoStoreInstancesProducesExactlyOneKey()
    {
        // CLIENT-003: Client.Avalonia and Client.Cli may run simultaneously as the same Windows
        // user and race an initial create. The cross-process file lock must serialize them so
        // only one key is ever generated, and every caller observes that SAME key.
        using var temp = new TempClientRoot();
        var generatorA = new FakeKeyPairGenerator();
        var generatorB = new FakeKeyPairGenerator();
        var storeA = new WindowsLocalPrincipalCredentialStore(generatorA, temp.Directory);
        var storeB = new WindowsLocalPrincipalCredentialStore(generatorB, temp.Directory);

        using var start = new Barrier(2);
        LocalPrincipalKeyPair? resultA = null;
        LocalPrincipalKeyPair? resultB = null;

        var taskA = Task.Run(() =>
        {
            start.SignalAndWait();
            resultA = storeA.CreateAndStoreAsync().GetAwaiter().GetResult();
        });
        var taskB = Task.Run(() =>
        {
            start.SignalAndWait();
            resultB = storeB.CreateAndStoreAsync().GetAwaiter().GetResult();
        });

        await Task.WhenAll(taskA, taskB);

        Equal(1, generatorA.GenerateCallCount + generatorB.GenerateCallCount, "exactly one generator call wins the race");
        True(resultA!.PublicKeyBlob.SequenceEqual(resultB!.PublicKeyBlob), "both racing callers must observe the SAME resulting key");

        var persisted = await storeA.CreateAndStoreAsync();
        True(persisted.PublicKeyBlob.SequenceEqual(resultA.PublicKeyBlob), "the persisted credential matches what both callers observed");
    }

    public static async Task TestRebindIsIdempotentForSamePrincipalAndRejectsADifferentPrincipal()
    {
        using var temp = new TempClientRoot();
        var store = new WindowsLocalPrincipalCredentialStore(new FakeKeyPairGenerator(), temp.Directory);
        await store.CreateAndStoreAsync();
        await store.BindPrincipalIdAsync("principal-A");

        // Idempotent: rebinding to the SAME principal succeeds without mutating anything.
        await store.BindPrincipalIdAsync("principal-A");
        Equal("principal-A", (await store.LoadAsync())!.LocalPrincipalId, "rebind to the same principal is a no-op");

        // Rejected: a DIFFERENT principal must never silently steal the same private key.
        await AssertThrowsAsync<InvalidOperationException>(() => store.BindPrincipalIdAsync("principal-B"),
            "binding to a different principal id must be rejected");
        Equal("principal-A", (await store.LoadAsync())!.LocalPrincipalId, "the original binding is unchanged after a rejected rebind");
    }

    public static async Task TestCancellationDuringLockContentionExitsPromptlyWithoutMutation()
    {
        using var temp = new TempClientRoot();
        var store = new WindowsLocalPrincipalCredentialStore(new FakeKeyPairGenerator(), temp.Directory);
        await store.CreateAndStoreAsync();

        // Hold the SAME lock file externally to force the next operation into genuine contention.
        var lockPath = store.FilePath + ".lock";
        using var externalHolder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        using var cts = new CancellationTokenSource();
        var waitingTask = Task.Run(() => store.BindPrincipalIdAsync("principal-should-not-apply", cts.Token));

        await Task.Delay(200);
        True(!waitingTask.IsCompleted, "sanity: the second operation is genuinely waiting on the held lock");

        cts.Cancel();

        var completed = await Task.WhenAny(waitingTask, Task.Delay(TimeSpan.FromSeconds(3)));
        True(completed == waitingTask, "cancellation must be observed promptly, not only after the full lock-contention window");
        await AssertThrowsAsync<OperationCanceledException>(() => waitingTask,
            "a canceled lock wait must exit with OperationCanceledException, not silently succeed or hang");

        externalHolder.Dispose();

        // The canceled Bind must not have mutated anything: still unbound (never bound at all).
        Equal(false, await store.HasCredentialAsync(), "the canceled Bind must not have applied any mutation");
        Equal(null, (await store.LoadAsync())?.LocalPrincipalId, "no principal id was ever bound by the canceled operation");
    }

    public static Task TestNoProductionKeyGeneratorShipsInThisSlice()
    {
        // The crypto boundary, asserted structurally: #41 ships the store and the generator
        // ABSTRACTION, but no concrete production generator - #42 selects the real signature
        // algorithm and supplies its implementation.
        var clientPlatformWindows = typeof(WindowsLocalPrincipalCredentialStore).Assembly;
        var generators = clientPlatformWindows.GetTypes()
            .Where(t => typeof(ILocalPrincipalKeyPairGenerator).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .Select(t => t.FullName!)
            .ToList();

        if (generators.Count > 0)
        {
            throw new Exception($"#41 must ship no production key generator (#42 owns the algorithm); found: {string.Join(", ", generators)}");
        }

        return Task.CompletedTask;
    }
}
