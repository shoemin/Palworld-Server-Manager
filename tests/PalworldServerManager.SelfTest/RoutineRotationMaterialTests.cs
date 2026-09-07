using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class RoutineRotationMaterialTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Routine rotation material assertion failed."); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected rotation material refusal."); }
    private static LocalPrincipalMutationActor Owner(PeerTrustTests.Fixture f) => new(f.HostId, f.OwnerId, "native-owner", "fixture-public");
    private static HostCredentialStateRepository Repo(PeerTrustTests.Fixture f) => new(f.Database, f.HostId);
    private sealed class Store : ISecureCredentialStore, IDisposable
    {
        internal readonly Dictionary<string, byte[]> Values = new();
        internal readonly List<byte[]> Returned = [];
        internal readonly List<ReadOnlyMemory<byte>> Exported = [];
        internal Action? AfterStore;
        internal int Writes, Reads;
        public Task<byte[]?> RetrieveAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Reads++;
            if (!Values.TryGetValue(key, out var value)) return Task.FromResult<byte[]?>(null);
            var copy = value.ToArray(); Returned.Add(copy); return Task.FromResult<byte[]?>(copy);
        }
        public Task StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Exported.Add(secret); Values.Add(key, secret.ToArray()); Writes++; AfterStore?.Invoke(); return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken ct = default) => throw new Exception("Material preparation cannot delete keys.");
        internal void CheckCleared() => Check(Returned.All(b => b.All(v => v == 0)) && Exported.All(b => b.Span.IndexOfAnyExcept((byte)0) < 0));
        public void Dispose() { foreach (var bytes in Values.Values) CryptographicOperations.ZeroMemory(bytes); }
    }
    public static async Task DurableWriteRetryAndSerialization()
    {
        using var f = new PeerTrustTests.Fixture(); using var store = new Store(); var owner = Owner(f); var request = Guid.NewGuid();
        var coordinator = new RoutineRotationMaterialCoordinator(Repo(f), new WindowsHostCredentialMaterial(store));
        store.AfterStore = () => throw new IOException("Fixture acknowledgement lost after secure write.");
        await Reject<IOException>(() => coordinator.PrepareAsync(owner, request));
        var reservation = Repo(f).PrepareRoutineRotation(owner, request); var saved = store.Values[reservation.NewReference].ToArray();
        try
        {
            Check(!reservation.PublicMetadataReady && store.Writes == 1 && f.Count("AuditEvents") == 1);
            Check(HostTrustPlanning.Build(Repo(f).Read()).Publication!.PendingFingerprint is null);
            store.AfterStore = null;
            var blocked = new BlockingMaterial(new WindowsHostCredentialMaterial(store));
            coordinator = new(Repo(f), blocked); // New coordinator, same durable reservation/material.
            var retries = Enumerable.Range(0, 12).Select(_ => coordinator.PrepareAsync(owner, Guid.NewGuid())).ToArray();
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(blocked.Calls == 1 && retries.All(t => !t.IsCompleted));
            blocked.Release.SetResult();
            var results = await Task.WhenAll(retries).WaitAsync(TimeSpan.FromSeconds(30));
            Check(results.All(r => r.RotationId == request && r.PublicMetadataReady && r.State == HostCredentialRotationState.Prepared));
            Check(store.Writes == 1 && saved.SequenceEqual(store.Values[reservation.NewReference]) && f.Count("AuditEvents") == 2);
            var fingerprint = Repo(f).Read().Credentials.Single(c => c.Reference == reservation.NewReference).PublicKeyFingerprint!;
            store.Values.Remove(reservation.NewReference, out var removed);
            await Reject<CryptographicException>(() => coordinator.PrepareAsync(owner, request));
            Check(store.Writes == 1); store.Values.Add(reservation.NewReference, removed!);
            await Reject<CryptographicException>(() => new WindowsHostCredentialMaterial(store).EnsurePreparedAsync(f.HostId, reservation.NewReference, new string('B', 64)));
            Check(await new WindowsHostCredentialMaterial(store).EnsurePreparedAsync(f.HostId, reservation.NewReference, fingerprint) == fingerprint);
            Check(Repo(f).Read().CurrentReference == "current" && f.Count("TrustedManagers") == 0 && f.Count("HostCapabilityGrants") == 0 && f.Count("ServerCapabilityGrants") == 0);
            store.CheckCleared();
        }
        finally { CryptographicOperations.ZeroMemory(saved); }
    }
    private sealed class BlockingMaterial(IHostRotationMaterial inner) : IHostRotationMaterial
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public async Task<string> EnsurePreparedAsync(Guid hostId, string reference, string? expected, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref Calls) == 1) { Entered.SetResult(); await Release.Task.WaitAsync(ct); }
            return await inner.EnsurePreparedAsync(hostId, reference, expected, ct);
        }
    }
    public static async Task CancellationAuthorizationAndAuditRollback()
    {
        using var f = new PeerTrustTests.Fixture(); using var store = new Store(); using var cancellation = new CancellationTokenSource();
        var owner = Owner(f); var request = Guid.NewGuid(); var coordinator = new RoutineRotationMaterialCoordinator(Repo(f), new WindowsHostCredentialMaterial(store));
        store.AfterStore = cancellation.Cancel;
        await Reject<OperationCanceledException>(() => coordinator.PrepareAsync(owner, request, cancellation.Token));
        var reserved = Repo(f).PrepareRoutineRotation(owner, request); Check(!reserved.PublicMetadataReady && store.Writes == 1);
        store.AfterStore = null;
        f.Execute("CREATE TRIGGER FailMaterialAudit BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='HostRoutineRotationMaterialPrepared' BEGIN SELECT RAISE(ABORT,'fixture material audit failure'); END;");
        await Reject<SqliteException>(() => coordinator.PrepareAsync(owner, request));
        Check(!Repo(f).PrepareRoutineRotation(owner, request).PublicMetadataReady && f.Count("AuditEvents") == 1);
        f.Execute("DROP TRIGGER FailMaterialAudit;");
        var staleMaterial = new CallbackMaterial(new WindowsHostCredentialMaterial(store), () => f.Execute("UPDATE LocalPrincipals SET PublicVerificationKey='fresh-owner' WHERE IsOwner=1;"));
        await Reject<AuthenticationException>(() => new RoutineRotationMaterialCoordinator(Repo(f), staleMaterial).PrepareAsync(owner, request));
        Check(Repo(f).Read().Credentials.Single(c => c.Reference == reserved.NewReference).PublicKeyFingerprint is null);
        var reads = store.Reads;
        await Reject<AuthenticationException>(() => coordinator.PrepareAsync(owner, request)); Check(store.Reads == reads);
        var fresh = owner with { PublicVerificationKey = "fresh-owner" };
        Check((await coordinator.PrepareAsync(fresh, request)).PublicMetadataReady && store.Writes == 1);
        Repo(f).AbortRoutineRotation(fresh, request); reads = store.Reads;
        Check((await coordinator.PrepareAsync(fresh, request)).State == HostCredentialRotationState.Aborted && store.Reads == reads);
        var next = Guid.NewGuid(); store.AfterStore = () => Repo(f).AbortRoutineRotation(fresh, next);
        await Reject<AuthenticationException>(() => coordinator.PrepareAsync(fresh, next));
        Check(Repo(f).Read().Rotations.Single(r => r.RotationId == next).State == HostCredentialRotationState.Aborted);
        Check(Repo(f).Read().Credentials.Single(c => c.Reference == "host-rotation-" + next.ToString("N")).PublicKeyFingerprint is null);
        Check(Repo(f).Read().CurrentReference == "current"); store.CheckCleared();
    }
    private sealed class CallbackMaterial(IHostRotationMaterial inner, Action afterValidation) : IHostRotationMaterial
    {
        public async Task<string> EnsurePreparedAsync(Guid hostId, string reference, string? expected, CancellationToken ct = default)
        { var result = await inner.EnsurePreparedAsync(hostId, reference, expected, ct); afterValidation(); return result; }
    }
    public static async Task InvalidExistingMaterialNeverReplaced()
    {
        using var store = new Store(); var host = Guid.NewGuid(); var material = new WindowsHostCredentialMaterial(store);
        byte[] Certificate(Guid subject, ECCurve curve, DateTimeOffset start, DateTimeOffset end, bool includePrivate)
        {
            using var key = ECDsa.Create(curve); var request = new CertificateRequest("CN=PalworldServerManager-" + subject.ToString("D"), key, HashAlgorithmName.SHA256);
            using var cert = request.CreateSelfSigned(start, end); return cert.Export(includePrivate ? X509ContentType.Pfx : X509ContentType.Cert);
        }
        var now = DateTimeOffset.UtcNow;
        store.Values.Add("corrupt", [1, 2, 3]);
        store.Values.Add("public-only", Certificate(host, ECCurve.NamedCurves.nistP256, now.AddMinutes(-1), now.AddDays(1), false));
        store.Values.Add("wrong-host", Certificate(Guid.NewGuid(), ECCurve.NamedCurves.nistP256, now.AddMinutes(-1), now.AddDays(1), true));
        store.Values.Add("expired", Certificate(host, ECCurve.NamedCurves.nistP256, now.AddDays(-2), now.AddDays(-1), true));
        store.Values.Add("future", Certificate(host, ECCurve.NamedCurves.nistP256, now.AddDays(1), now.AddDays(2), true));
        store.Values.Add("wrong-curve", Certificate(host, ECCurve.NamedCurves.nistP384, now.AddMinutes(-1), now.AddDays(1), true));
        foreach (var name in store.Values.Keys) await Reject<CryptographicException>(() => material.EnsurePreparedAsync(host, name, null));
        await Reject<ArgumentException>(() => material.EnsurePreparedAsync(host, "missing", "invalid"));
        await Reject<CryptographicException>(() => material.EnsurePreparedAsync(host, "missing", new string('A', 64)));
        Check(store.Writes == 0); store.CheckCleared();
    }
}
