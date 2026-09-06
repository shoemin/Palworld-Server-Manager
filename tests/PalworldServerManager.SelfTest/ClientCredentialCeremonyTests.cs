using System.Security.Cryptography;
using System.Text.Json;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Core.Security;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;
using static PalworldServerManager.SelfTest.SecureStoreTests;

namespace PalworldServerManager.SelfTest;

public static class ClientCredentialCeremonyTests
{
    private sealed class EmptyKeys : ILocalPrincipalKeyGenerator { public LocalPrincipalKeyPair Generate() => new([], []); }
    internal static ClientCredentialCeremony IntegrationRotation => new(Guid.Parse("b94414b3-9813-4027-8b28-fdf8a831a360"),
        Guid.Parse("f2d2d454-0db4-4a9f-8459-0f5dc5c82581"), ClientCredentialPurpose.OwnerRotation, ClientCredentialKeyUse.Fresh);
    internal static async Task IntegrationProbe(string path, bool complete)
    {
        var store = new WindowsLocalPrincipalCredentialStore(new WindowsLocalPrincipalCryptography(), path);
        if (!complete)
        {
            var bootstrap = IntegrationRotation with { TicketId = Guid.NewGuid(), Purpose = ClientCredentialPurpose.Bootstrap };
            var initial = await store.PrepareAsync(bootstrap);
            try { await store.ConfirmAsync(bootstrap, Guid.NewGuid(), initial.PublicKey); }
            finally { CryptographicOperations.ZeroMemory(initial.PrivateKey); }
        }
        var current = await store.LoadAsync() ?? throw new Exception("User A current credential missing.");
        var pending = await store.PrepareAsync(IntegrationRotation);
        try
        {
            Check(!current.KeyPair.PublicKey.SequenceEqual(pending.PublicKey), "User A pending recovery reused current key.");
            if (!complete) File.WriteAllBytes(path + ".pending-public", pending.PublicKey);
            if (complete)
            {
                Check(File.ReadAllBytes(path + ".pending-public").SequenceEqual(pending.PublicKey), "Separate process generated a different pending recovery key.");
                await store.ConfirmAsync(IntegrationRotation, current.LocalPrincipalId, pending.PublicKey);
                var loaded = await store.LoadAsync() ?? throw new Exception("User A confirmation missing.");
                try { Check(loaded.KeyPair.PrivateKey.SequenceEqual(pending.PrivateKey), "Separate user process failed to recover pending key."); }
                finally { CryptographicOperations.ZeroMemory(loaded.KeyPair.PrivateKey); }
            }
        }
        finally { CryptographicOperations.ZeroMemory(current.KeyPair.PrivateKey); CryptographicOperations.ZeroMemory(pending.PrivateKey); }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "PSMAstraCeremony-" + Guid.NewGuid().ToString("N"));
        public string PathName => Path.Combine(Root, "principal.bin");
        public Guid Host { get; } = Guid.NewGuid();
        public Guid Principal { get; } = Guid.NewGuid();
        private readonly List<byte[]> _privateBuffers = [];
        public WindowsLocalPrincipalCredentialStore Store(Action<string, byte[]>? writer = null) => new(new WindowsLocalPrincipalCryptography(), PathName, writer);
        public ClientCredentialCeremony Ceremony(ClientCredentialPurpose purpose = ClientCredentialPurpose.Bootstrap,
            ClientCredentialKeyUse keyUse = ClientCredentialKeyUse.Fresh) => new(Host, Guid.NewGuid(), purpose, keyUse);
        public LocalPrincipalKeyPair Keep(LocalPrincipalKeyPair pair) { _privateBuffers.Add(pair.PrivateKey); return pair; }
        public async Task<LocalPrincipalClientCredential?> Load()
        { var value = await Store().LoadAsync(); if (value is not null) Keep(value.KeyPair); return value; }
        public void Dispose() { foreach (var key in _privateBuffers) CryptographicOperations.ZeroMemory(key); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    public static async Task RetryAndRotation()
    {
        using var f = new Fixture(); var store = f.Store(); var bootstrap = f.Ceremony();
        var first = f.Keep(await store.PrepareAsync(bootstrap));
        Check(!await store.HasCredentialAsync() && await f.Load() is null, "Unconfirmed key exposed as current authority.");
        var repeated = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => f.Store().PrepareAsync(bootstrap))));
        foreach (var pair in repeated) Check(f.Keep(pair).PrivateKey.SequenceEqual(first.PrivateKey), "Concurrent/reopened preparation generated another key.");
        await Reject<InvalidOperationException>(() => store.PrepareAsync(f.Ceremony()));
        await Reject<InvalidOperationException>(() => store.CreateAndStoreAsync());
        await Reject<InvalidOperationException>(() => store.BindPrincipalIdAsync(f.Principal));
        await Reject<InvalidOperationException>(() => store.DeleteAsync());
        await Reject<InvalidOperationException>(() => store.ConfirmAsync(bootstrap, f.Principal, new byte[] { 9 }));
        await Reject<InvalidOperationException>(() => store.ConfirmAsync(bootstrap with { HostId = Guid.NewGuid() }, f.Principal, first.PublicKey));
        await store.ConfirmAsync(bootstrap, f.Principal, first.PublicKey);
        await f.Store().ConfirmAsync(bootstrap, f.Principal, first.PublicKey);
        Check(f.Keep(await f.Store().PrepareAsync(bootstrap)).PrivateKey.SequenceEqual(first.PrivateKey), "Completed retry lost the confirmed key.");
        await Reject<InvalidOperationException>(() => store.ConfirmAsync(bootstrap, Guid.NewGuid(), first.PublicKey));
        await Reject<InvalidOperationException>(() => store.PrepareAsync(bootstrap with { Purpose = ClientCredentialPurpose.OwnerRotation }));
        var rotation = f.Ceremony(ClientCredentialPurpose.OwnerRotation); var fresh = f.Keep(await store.PrepareAsync(rotation));
        Check(!fresh.PublicKey.SequenceEqual(first.PublicKey) && (await f.Load())!.KeyPair.PrivateKey.SequenceEqual(first.PrivateKey), "Rotation replaced current before confirmation or reused the old key.");
        await Reject<InvalidOperationException>(() => store.ConfirmAsync(bootstrap, f.Principal, first.PublicKey));
        await Reject<InvalidOperationException>(() => store.ConfirmAsync(rotation, Guid.NewGuid(), fresh.PublicKey));
        await store.ConfirmAsync(rotation, f.Principal, fresh.PublicKey);
        Check((await f.Load())!.KeyPair.PrivateKey.SequenceEqual(fresh.PrivateKey), "Rotation did not promote exact prepared key.");
        await Reject<InvalidOperationException>(() => f.Store().PrepareAsync(bootstrap));
        await Reject<InvalidOperationException>(() => f.Store().PrepareAsync(bootstrap with { Purpose = ClientCredentialPurpose.OwnerRotation }));
        await Reject<InvalidOperationException>(() => store.ConfirmAsync(bootstrap, f.Principal, first.PublicKey));
        Check((await f.Load())!.KeyPair.PrivateKey.SequenceEqual(fresh.PrivateKey), "Stale completion replaced later current key.");
        Check(File.ReadAllBytes(f.PathName).AsSpan().IndexOf(fresh.PrivateKey) < 0, "Pending/current private key persisted in plaintext.");
    }
    public static async Task RehomeAndDiscard()
    {
        using var f = new Fixture(); var store = f.Store(); var bootstrap = f.Ceremony();
        var first = f.Keep(await store.PrepareAsync(bootstrap)); await store.ConfirmAsync(bootstrap, f.Principal, first.PublicKey);
        var rehome = f.Ceremony(ClientCredentialPurpose.OwnerRehome, ClientCredentialKeyUse.ExistingForRehome);
        var existing = f.Keep(await store.PrepareAsync(rehome)); Check(existing.PrivateKey.SequenceEqual(first.PrivateKey), "Active target re-home replaced existing credential.");
        await store.ConfirmAsync(rehome, f.Principal, existing.PublicKey);
        var enrollment = f.Ceremony(ClientCredentialPurpose.Enrollment); var pending = f.Keep(await store.PrepareAsync(enrollment));
        await Reject<InvalidOperationException>(() => store.DiscardPendingAsync(rehome));
        await store.DiscardPendingAsync(enrollment); await store.DiscardPendingAsync(enrollment);
        Check((await f.Load())!.KeyPair.PrivateKey.SequenceEqual(existing.PrivateKey), "Discard removed current credential.");
        var next = f.Ceremony(ClientCredentialPurpose.OwnerRehome); var nextKey = f.Keep(await store.PrepareAsync(next));
        Check(!nextKey.PublicKey.SequenceEqual(existing.PublicKey), "Fresh target recovery reused revoked material.");
        await store.ConfirmAsync(next, Guid.NewGuid(), nextKey.PublicKey);
        await Reject<InvalidOperationException>(() => store.PrepareAsync(next with { TicketId = Guid.NewGuid(), HostId = Guid.NewGuid() }));
        foreach (var invalid in new[] { next with { TicketId = Guid.Empty }, next with { Purpose = (ClientCredentialPurpose)99 },
            next with { KeyUse = (ClientCredentialKeyUse)99 }, next with { Purpose = ClientCredentialPurpose.OwnerRotation, KeyUse = ClientCredentialKeyUse.ExistingForRehome } })
            await Reject<ArgumentException>(() => store.PrepareAsync(invalid));
        using var empty = new Fixture(); await Reject<InvalidOperationException>(() => empty.Store().PrepareAsync(empty.Ceremony(ClientCredentialPurpose.OwnerRehome, ClientCredentialKeyUse.ExistingForRehome)));
    }
    public static async Task FailureAndMigration()
    {
        using var empty = new Fixture();
        await Reject<InvalidDataException>(() => new WindowsLocalPrincipalCredentialStore(new EmptyKeys(), empty.PathName).CreateAndStoreAsync());
        Check(!File.Exists(empty.PathName), "Empty generator output was persisted as a credential.");
        using var f = new Fixture(); Directory.CreateDirectory(f.Root);
        var legacy = f.Keep(new WindowsLocalPrincipalCryptography().Generate());
        var plain = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, PrincipalId = f.Principal, PublicKey = legacy.PublicKey, PrivateKey = legacy.PrivateKey });
        try { File.WriteAllBytes(f.PathName, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Check((await f.Load())!.KeyPair.PrivateKey.SequenceEqual(legacy.PrivateKey), "v1 credential could not be read.");
        var rotation = f.Ceremony(ClientCredentialPurpose.OwnerRotation); var disk = File.ReadAllBytes(f.PathName);
        var failing = f.Store((_, _) => throw new IOException("Synthetic pre-rename failure."));
        await Reject<IOException>(() => failing.PrepareAsync(rotation));
        Check(File.ReadAllBytes(f.PathName).SequenceEqual(disk), "Failed preparation changed durable v1 state.");
        var prepared = f.Keep(await f.Store().PrepareAsync(rotation)); disk = File.ReadAllBytes(f.PathName);
        await Reject<IOException>(() => failing.ConfirmAsync(rotation, f.Principal, prepared.PublicKey));
        Check(File.ReadAllBytes(f.PathName).SequenceEqual(disk) && (await f.Load())!.KeyPair.PrivateKey.SequenceEqual(legacy.PrivateKey), "Failed promotion lost current or pending state.");
        Check(f.Keep(await f.Store().PrepareAsync(rotation)).PrivateKey.SequenceEqual(prepared.PrivateKey), "Lost-response retry changed the pending key.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Reject<OperationCanceledException>(() => f.Store().ConfirmAsync(rotation, f.Principal, prepared.PublicKey, canceled.Token));
        await Reject<OperationCanceledException>(() => f.Store().DiscardPendingAsync(rotation, canceled.Token));
        Check(File.ReadAllBytes(f.PathName).SequenceEqual(disk), "Canceled operation changed durable state.");
        var lostResult = f.Store((path, bytes) => { WindowsLocalPrincipalCredentialStore.AtomicWrite(path, bytes); throw new IOException("Synthetic lost response after durable rename."); });
        await Reject<IOException>(() => lostResult.ConfirmAsync(rotation, f.Principal, prepared.PublicKey));
        await f.Store().ConfirmAsync(rotation, f.Principal, prepared.PublicKey);
        Check((await f.Load())!.KeyPair.PrivateKey.SequenceEqual(prepared.PrivateKey), "Post-rename failure stranded confirmed material.");
        foreach (var malformed in new[] { "{\"Version\":99}", "{\"Version\":2,\"PublicKey\":null}", "{\"Version\":2,\"Pending\":{}}" })
        {
            File.WriteAllBytes(f.PathName, ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(malformed), null, DataProtectionScope.CurrentUser));
            try { await f.Store().LoadAsync(); throw new Exception("Malformed client state accepted."); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { }
        }
    }
    public static async Task HostConsumedRetry()
    {
        // Real repository transactions with synthetic native identity; not multi-user field evidence.
        using var host = new LocalEnrollmentTests.Fixture(initialized: false); using var client = new Fixture();
        var store = client.Store(); var bootstrap = client.Ceremony() with { HostId = host.HostId };
        using var bootstrapProof = host.Proof(bootstrap.TicketId, bootstrap: true);
        host.Repository.PrepareOfflineBootstrap(bootstrap.TicketId, "owner", bootstrapProof, host.Time.Now.AddMinutes(15));
        var first = client.Keep(await store.PrepareAsync(bootstrap));
        var principal = host.Repository.CompleteBootstrap(bootstrap.TicketId, "owner", bootstrapProof, Convert.ToBase64String(first.PublicKey));
        // Drop the result; another client process reopens and submits the same durable key.
        var retry = client.Keep(await client.Store().PrepareAsync(bootstrap));
        var repeated = host.Repository.CompleteBootstrap(bootstrap.TicketId, "owner", bootstrapProof, Convert.ToBase64String(retry.PublicKey));
        await store.ConfirmAsync(bootstrap, repeated, retry.PublicKey);
        using (var proof = host.Authenticate(principal, "owner", retry)) Check(proof.GetCurrentPrincipal().IsOwner, "Lost-result bootstrap bound the wrong client key.");
        var rotation = client.Ceremony(ClientCredentialPurpose.OwnerRotation) with { HostId = host.HostId };
        using var rotationProof = LocalEnrollmentVerifier.Compute(host.Key, host.HostId, LocalEnrollmentPurpose.OwnerRotation, rotation.TicketId, new byte[] { 4, 7, 2, 9 });
        host.Repository.PrepareOfflineOwnerRotation(rotation.TicketId, rotationProof, host.Time.Now.AddMinutes(15));
        var fresh = client.Keep(await store.PrepareAsync(rotation));
        var rotated = host.Repository.CompleteOwnerRotation(rotation.TicketId, "owner", rotationProof, Convert.ToBase64String(fresh.PublicKey));
        await store.ConfirmAsync(rotation, rotated, fresh.PublicKey);
        Check(host.Repository.CompleteBootstrap(bootstrap.TicketId, "owner", bootstrapProof, "ignored-on-consumed-retry") == principal,
            "Fixture no longer exercises Host historical confirmation semantics.");
        await Reject<InvalidOperationException>(() => client.Store().PrepareAsync(bootstrap));
        var current = await client.Load() ?? throw new Exception("Client credential lost after historical retry.");
        using var authenticated = host.Authenticate(principal, "owner", current.KeyPair);
        Check(authenticated.GetCurrentPrincipal().IsOwner, "Historical ticket displaced the current authenticated key.");
    }
}
