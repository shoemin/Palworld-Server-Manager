using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Contracts;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static class LocalPrincipalAuthenticationTests
{
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); throw new Exception("Expected rejection: " + typeof(T).Name); } catch (T) { } }
    private sealed class Clock : TimeProvider
    {
        private long _timestamp = 100;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref _timestamp, (long)amount.TotalMilliseconds);
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "PSMAuth" + Guid.NewGuid().ToString("N"));
        internal readonly Guid HostId = Guid.NewGuid(), OwnerId = Guid.NewGuid(), UserId = Guid.NewGuid();
        internal readonly LocalPrincipalKeyPair OwnerKey = new P256LocalPrincipalCryptography().Generate();
        internal readonly LocalPrincipalKeyPair UserKey = new P256LocalPrincipalCryptography().Generate();
        internal readonly List<LocalAuthenticationFailure> Failures = [];
        internal readonly Clock Time = new();
        internal readonly HostDatabase Database;
        internal readonly SqliteConnection Writer;
        internal readonly LocalPrincipalAuthenticationRepository Repository;
        private readonly HostExclusivityLock _lease;
        internal Fixture(bool initialized = true)
        {
            _lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, @"Global\PSMAuth" + Guid.NewGuid().ToString("N"))!;
            Database = new(new HostDataRoot(Path.Combine(Root, "Host"))); Writer = Database.OpenConnection();
            HostSchemaMigrationRunner.Default().Migrate(Writer);
            new HostIdentityRepository(Database).EnsureHostIdentity(Writer, hostIdFactory: () => HostId.ToString("D"));
            if (initialized)
            {
                using var tx = Writer.BeginTransaction();
                new HostIdentityRepository(Database).InitializeWithOwner(Writer, tx, OwnerId.ToString("D"), "native-owner", Public(OwnerKey));
                using var command = Writer.CreateCommand(); command.Transaction = tx;
                command.CommandText = """
                    INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc)
                    VALUES ($id, 'native-user', $key, 0, 'Active', $now);
                    """;
                command.Parameters.AddWithValue("$id", UserId.ToString("D")); command.Parameters.AddWithValue("$key", Public(UserKey));
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery(); tx.Commit();
            }
            Repository = new(Database);
        }
        internal LocalPrincipalConnectionAuthentication Connection(string native = "native-user", Guid? expectedHostId = null) =>
            new(Repository, expectedHostId ?? HostId, native, reason => { lock (Failures) Failures.Add(reason); }, Time);
        internal LocalPrincipalClientCredential Credential(bool owner = false) => new(owner ? OwnerId : UserId, owner ? OwnerKey : UserKey);
        internal byte[] Sign(byte[] payload, bool owner = false) => P256LocalPrincipalCryptography.Sign(Credential(owner), HostId, payload);
        internal void ChangeUser(string? publicKey)
        {
            using var command = Writer.CreateCommand();
            command.CommandText = "UPDATE LocalPrincipals SET PublicVerificationKey=$key, State=$state WHERE LocalPrincipalId=$id;";
            command.Parameters.AddWithValue("$key", (object?)publicKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$state", publicKey is null ? "Revoked" : "Active");
            command.Parameters.AddWithValue("$id", UserId.ToString("D")); command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(OwnerKey.PrivateKey); CryptographicOperations.ZeroMemory(UserKey.PrivateKey);
            Writer.Dispose(); SqliteConnection.ClearAllPools(); _lease.Dispose();
            Directory.Delete(Root, true);
        }
    }
    private static string Public(LocalPrincipalKeyPair key) => Convert.ToBase64String(key.PublicKey);

    public static Task MappingAndBoundaries()
    {
        using var fixture = new Fixture();
        var missingRoot = new HostDataRoot(Path.Combine(fixture.Root, "MissingHost"));
        Reject<SqliteException>(() => new LocalPrincipalAuthenticationRepository(new HostDatabase(missingRoot)).TryReadActive(fixture.UserId));
        Check(!Directory.Exists(missingRoot.RootDirectory), "Authentication created an authoritative data root.");
        using var user = fixture.Connection();
        var userResult = user.Authenticate(fixture.Sign(user.IssueChallenge(fixture.UserId)));
        Check(userResult.LocalPrincipalId == fixture.UserId && !userResult.IsOwner, "Non-Owner mapped to wrong identity/authority.");
        using var owner = fixture.Connection("native-owner");
        var ownerResult = owner.Authenticate(fixture.Sign(owner.IssueChallenge(fixture.OwnerId), owner: true));
        Check(ownerResult.LocalPrincipalId == fixture.OwnerId && ownerResult.IsOwner && ownerResult.LocalPrincipalId != userResult.LocalPrincipalId, "Distinct principal mapping collapsed.");
        Check(user.GetCurrentPrincipal().LocalPrincipalId == fixture.UserId, "Authenticated identity was lost.");
        Reject<AuthenticationException>(() => user.IssueChallenge(Guid.NewGuid()));
        Reject<AuthenticationException>(() => user.GetCurrentPrincipal());
        Reject<AuthenticationException>(() => user.IssueChallenge(fixture.OwnerId));
        Check(fixture.Failures.Contains(LocalAuthenticationFailure.NativeIdentityMismatch), "Native mismatch did not reach the bounded audit hook.");
        using var wrongHost = fixture.Connection(expectedHostId: Guid.NewGuid());
        Reject<AuthenticationException>(() => wrongHost.IssueChallenge(fixture.UserId));
        fixture.ChangeUser(null);
        Reject<AuthenticationException>(() => user.IssueChallenge(fixture.UserId));
        using var uninitialized = new Fixture(initialized: false);
        using (var seed = uninitialized.Writer.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO LocalPrincipals (LocalPrincipalId, OsPrincipalRef, PublicVerificationKey, IsOwner, State, CreatedUtc)
                VALUES ($id, 'native-user', $key, 0, 'Active', $now);
                """;
            seed.Parameters.AddWithValue("$id", uninitialized.UserId.ToString("D")); seed.Parameters.AddWithValue("$key", Public(uninitialized.UserKey));
            seed.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); seed.ExecuteNonQuery();
        }
        using var noAuthority = uninitialized.Connection();
        Reject<AuthenticationException>(() => noAuthority.IssueChallenge(uninitialized.UserId));
        HostDatabase.Execute(uninitialized.Writer, "UPDATE HostIdentity SET HostBootstrapState='Initialized';");
        Reject<AuthenticationException>(() => noAuthority.IssueChallenge(uninitialized.UserId)); // malformed ownerless initialized state
        Check(HostIdentityRepository.CountActiveOwners(fixture.Writer) == 1, "Authentication changed the Owner invariant.");
        fixture.Writer.Close(); SqliteConnection.ClearPool(fixture.Writer);
        using (var exclusiveFile = new FileStream(fixture.Database.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(exclusiveFile.Length > 0, "Read-only authentication lookup retained a database handle after shutdown.");
        using var roleView = new Fixture(); using var oldOwner = roleView.Connection("native-owner"); using var successor = roleView.Connection();
        oldOwner.Authenticate(roleView.Sign(oldOwner.IssueChallenge(roleView.OwnerId), owner: true));
        successor.Authenticate(roleView.Sign(successor.IssueChallenge(roleView.UserId)));
        // Synthetic committed recovery state; this does not implement or claim the recovery ceremony.
        using (var tx = roleView.Writer.BeginTransaction())
        {
            using var change = roleView.Writer.CreateCommand(); change.Transaction = tx;
            change.CommandText = """
                UPDATE LocalPrincipals SET IsOwner=0, State='Revoked', PublicVerificationKey=NULL WHERE OsPrincipalRef='native-owner';
                UPDATE LocalPrincipals SET IsOwner=1 WHERE OsPrincipalRef='native-user';
                """;
            change.ExecuteNonQuery(); tx.Commit();
        }
        Reject<AuthenticationException>(() => oldOwner.GetCurrentPrincipal());
        Check(successor.GetCurrentPrincipal().IsOwner && HostIdentityRepository.CountActiveOwners(roleView.Writer) == 1, "Identity hook cached the prior Owner state.");
        return Task.CompletedTask;
    }
    public static async Task NonceAndLifetime()
    {
        using var fixture = new Fixture();
        using var a = fixture.Connection(); using var b = fixture.Connection();
        var challengeA = a.IssueChallenge(fixture.UserId); var signatureA = fixture.Sign(challengeA);
        _ = b.IssueChallenge(fixture.UserId);
        Reject<AuthenticationException>(() => b.Authenticate(signatureA));
        Check(a.Authenticate(signatureA).LocalPrincipalId == fixture.UserId, "Connection-bound challenge rejected its own valid proof.");
        Reject<AuthenticationException>(() => a.Authenticate(signatureA));
        var old = fixture.Sign(a.IssueChallenge(fixture.UserId)); _ = a.IssueChallenge(fixture.UserId);
        Reject<AuthenticationException>(() => a.Authenticate(old));
        Reject<AuthenticationException>(() => a.Authenticate(old)); // failed proof consumed the replacement too
        var expiry = fixture.Sign(a.IssueChallenge(fixture.UserId)); fixture.Time.Advance(TimeSpan.FromSeconds(30));
        Reject<AuthenticationException>(() => a.Authenticate(expiry));
        var fresh = fixture.Sign(a.IssueChallenge(fixture.UserId)); fixture.Time.Advance(TimeSpan.FromMilliseconds(29999));
        Check(a.Authenticate(fresh).LocalPrincipalId == fixture.UserId, "Monotonic challenge lifetime rejected a current proof.");
        var concurrent = fixture.Sign(a.IssueChallenge(fixture.UserId)); var successes = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try { a.Authenticate(concurrent); Interlocked.Increment(ref successes); }
            catch (AuthenticationException) { }
        })));
        Check(successes == 1, "Concurrent verification reused a nonce.");
        var isolated = a.IssueChallenge(fixture.UserId); var saved = isolated.ToArray(); isolated[0] ^= 1;
        Check(a.Authenticate(fixture.Sign(saved)).LocalPrincipalId == fixture.UserId, "Caller mutation modified retained Host challenge.");
        a.Dispose(); a.Dispose();
        Reject<ObjectDisposedException>(() => a.GetCurrentPrincipal());
        Reject<ObjectDisposedException>(() => a.IssueChallenge(fixture.UserId));
        Reject<ObjectDisposedException>(() => a.Authenticate(concurrent));
    }
    public static Task RevocationAndMalformedProofs()
    {
        using var fixture = new Fixture(); using var connection = fixture.Connection();
        var issued = connection.IssueChallenge(fixture.UserId);
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wrong = wrongKey.SignData(issued, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Reject<AuthenticationException>(() => connection.Authenticate(wrong));
        foreach (var signature in new[] { Array.Empty<byte>(), new byte[63], new byte[65], new byte[4096] })
        { _ = connection.IssueChallenge(fixture.UserId); Reject<AuthenticationException>(() => connection.Authenticate(signature)); }
        var prior = fixture.Sign(connection.IssueChallenge(fixture.UserId));
        fixture.ChangeUser(Convert.ToBase64String(wrongKey.ExportSubjectPublicKeyInfo()));
        Reject<AuthenticationException>(() => connection.Authenticate(prior));
        fixture.ChangeUser(Public(fixture.UserKey));
        connection.Authenticate(fixture.Sign(connection.IssueChallenge(fixture.UserId)));
        fixture.ChangeUser(null); Reject<AuthenticationException>(() => connection.GetCurrentPrincipal());
        fixture.ChangeUser(Public(fixture.UserKey)); Reject<AuthenticationException>(() => connection.GetCurrentPrincipal());
        connection.Authenticate(fixture.Sign(connection.IssueChallenge(fixture.UserId)));
        fixture.ChangeUser(Convert.ToBase64String(wrongKey.ExportSubjectPublicKeyInfo()));
        Reject<AuthenticationException>(() => connection.GetCurrentPrincipal());
        fixture.ChangeUser("not-a-public-key"); Reject<AuthenticationException>(() => connection.IssueChallenge(fixture.UserId));
        return Task.CompletedTask;
    }
    public static async Task ClientKeyAndFrame()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "Client", "principal.bin");
        var store = new WindowsLocalPrincipalCredentialStore(new P256LocalPrincipalCryptography(), path);
        var original = await store.CreateAndStoreAsync();
        try
        {
            Check(LocalPrincipalAuthentication.IsValidPublicKey(Public(original)), "Production client generated an unsupported public key.");
            Check(File.ReadAllBytes(path).AsSpan().IndexOf(original.PrivateKey) < 0, "Client key was stored without DPAPI.");
            var retry = await store.CreateAndStoreAsync();
            try { Check(retry.PrivateKey.SequenceEqual(original.PrivateKey), "Unbound retry replaced the production key."); }
            finally { CryptographicOperations.ZeroMemory(retry.PrivateKey); }
            await store.BindPrincipalIdAsync(fixture.UserId);
            var loaded = await store.LoadAsync() ?? throw new Exception("Bound production client credential missing.");
            try
            {
                var payload = LocalPrincipalAuthentication.EncodeChallenge(fixture.HostId, fixture.UserId, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
                var signature = P256LocalPrincipalCryptography.Sign(loaded, fixture.HostId, payload);
                Check(signature.Length == 64 && LocalPrincipalAuthentication.Verify(Public(original), payload, signature), "Client/Host crypto contract mismatch.");
                fixture.ChangeUser(Public(original));
                using var connection = fixture.Connection();
                var storedProof = P256LocalPrincipalCryptography.Sign(loaded, fixture.HostId, connection.IssueChallenge(fixture.UserId));
                Check(connection.Authenticate(storedProof).LocalPrincipalId == loaded.LocalPrincipalId, "Reloaded DPAPI key did not authenticate against the persisted public verifier.");
                foreach (var bad in new[] { payload.Concat(new byte[] { 10 }).ToArray(), new byte[] { 255 },
                    Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(payload).Replace("AUTH/1", "AUTH/2")), new byte[257] })
                    Reject<CryptographicException>(() => P256LocalPrincipalCryptography.Sign(loaded, fixture.HostId, bad));
                Reject<CryptographicException>(() => P256LocalPrincipalCryptography.Sign(loaded, Guid.NewGuid(), payload));
                Reject<CryptographicException>(() => P256LocalPrincipalCryptography.Sign(new(Guid.NewGuid(), loaded.KeyPair), fixture.HostId, payload));
                using var otherCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
                Check(!LocalPrincipalAuthentication.IsValidPublicKey(Convert.ToBase64String(otherCurve.ExportSubjectPublicKeyInfo())), "Wrong public curve accepted.");
                Check(!LocalPrincipalAuthentication.IsValidPublicKey(Convert.ToBase64String(original.PrivateKey)), "Private blob accepted as public verifier.");
                Check(!LocalPrincipalAuthentication.IsValidPublicKey(Public(original) + "\n"), "Noncanonical public key accepted.");
                Check(!LocalPrincipalAuthentication.IsValidPublicKey(Convert.ToBase64String(original.PublicKey.Concat(new byte[] { 0 }).ToArray())), "Trailing public-key data accepted.");
                fixture.Writer.Close(); SqliteConnection.ClearAllPools(); // inspect quiescent persisted files, never a raw live WAL snapshot
                foreach (var hostFile in Directory.EnumerateFiles(fixture.Database.Root.RootDirectory, "*", SearchOption.AllDirectories))
                {
                    var content = File.ReadAllBytes(hostFile);
                    Check(content.AsSpan().IndexOf(original.PrivateKey) < 0 &&
                        !Encoding.UTF8.GetString(content).Contains(Convert.ToBase64String(original.PrivateKey), StringComparison.Ordinal),
                        "Client private material entered authoritative Host storage.");
                }
            }
            finally { CryptographicOperations.ZeroMemory(loaded.KeyPair.PrivateKey); }
        }
        finally { CryptographicOperations.ZeroMemory(original.PrivateKey); await store.DeleteAsync(); }
    }
}
