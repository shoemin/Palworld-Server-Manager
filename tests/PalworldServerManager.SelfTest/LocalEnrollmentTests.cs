using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;
using PalworldServerManager.Platform.Contracts;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static class LocalEnrollmentTests
{
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); throw new Exception("Expected " + typeof(T).Name); } catch (T) { } }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); throw new Exception("Expected " + typeof(T).Name); } catch (T) { } }
    internal sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 9, 6, 1, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal sealed class Store(byte[] key) : ISecureCredentialStore
    {
        internal byte[]? Key = key.ToArray(); internal byte[]? LastRead; internal int Writes; internal Action? OnRead;
        public Task<byte[]?> RetrieveAsync(string name, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); OnRead?.Invoke(); LastRead = Key?.ToArray(); return Task.FromResult(LastRead); }
        public Task StoreAsync(string name, ReadOnlyMemory<byte> value, CancellationToken ct = default)
        { Writes++; throw new Exception("Online enrollment wrote a secure-store key."); }
        public Task DeleteAsync(string name, CancellationToken ct = default) => throw new Exception("Online enrollment deleted a secure-store key.");
    }
    internal sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "PSMEnroll" + Guid.NewGuid().ToString("N"));
        internal readonly Guid HostId = Guid.NewGuid();
        internal readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
        internal readonly LocalPrincipalKeyPair OwnerKey = new WindowsLocalPrincipalCryptography().Generate();
        internal readonly LocalPrincipalKeyPair UserKey = new WindowsLocalPrincipalCryptography().Generate();
        internal readonly Clock Time = new(); internal readonly HostDatabase Database; internal readonly SqliteConnection Writer;
        internal readonly LocalEnrollmentRepository Repository; internal readonly Store Secrets; internal readonly LocalEnrollmentService Service;
        internal Guid Owner;
        private readonly HostExclusivityLock _lease;
        internal Fixture(bool initialized = true)
        {
            _lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, @"Global\PSMEnroll" + Guid.NewGuid().ToString("N"))!;
            Database = new(new HostDataRoot(Root)); Writer = Database.OpenConnection(); HostSchemaMigrationRunner.Default().Migrate(Writer);
            new HostIdentityRepository(Database).EnsureHostIdentity(Writer, hostIdFactory: () => HostId.ToString("D"));
            Repository = new(Database, HostId, Time); Secrets = new(Key); Service = new(Repository, Secrets, HostId, Time);
            if (initialized)
            {
                var id = Guid.NewGuid(); using var verifier = Proof(id, bootstrap: true);
                Repository.PrepareOfflineBootstrap(id, "owner", verifier, Time.Now.AddMinutes(15));
                Owner = Repository.CompleteBootstrap(id, "owner", verifier, Public(OwnerKey));
            }
        }
        internal LocalEnrollmentVerifier Proof(Guid ticket, bool bootstrap = false, byte[]? code = null) =>
            LocalEnrollmentVerifier.Compute(Key, HostId, bootstrap ? LocalEnrollmentPurpose.InitialOwner : LocalEnrollmentPurpose.AdditionalPrincipal,
                ticket, code ?? Encoding.ASCII.GetBytes("test-only-bearer-code"));
        internal LocalPrincipalMutationActor Actor => new(HostId, Owner, "owner", Public(OwnerKey));
        internal Guid Invite(string native = "user")
        { var id = Guid.NewGuid(); using var v = Proof(id); Repository.CreateEnrollment(Actor, id, native, v, Time.Now.AddMinutes(15)); return id; }
        internal Guid Enroll(string native = "user")
        { var ticket = Invite(native); using var v = Proof(ticket); return Repository.CompleteEnrollment(ticket, native, v, Public(UserKey)); }
        internal LocalPrincipalConnectionAuthentication Authenticate(Guid id, string native, LocalPrincipalKeyPair key)
        {
            var auth = new LocalPrincipalConnectionAuthentication(new(Database), HostId, native, _ => { });
            auth.Authenticate(new WindowsLocalPrincipalCryptography().Sign(new(id, key), HostId, auth.IssueChallenge(id))); return auth;
        }
        internal long Count(string sql) => HostDatabase.QueryScalarLong(Writer, sql);
        internal string Text(string sql) => HostDatabase.QueryScalarText(Writer, sql);
        internal void Sql(string sql) => HostDatabase.Execute(Writer, sql);
        public void Dispose()
        {
            Writer.Dispose(); SqliteConnection.ClearAllPools(); _lease.Dispose(); Directory.Delete(Root, true);
            CryptographicOperations.ZeroMemory(Key); CryptographicOperations.ZeroMemory(OwnerKey.PrivateKey); CryptographicOperations.ZeroMemory(UserKey.PrivateKey);
            if (Secrets.Key is { } k) CryptographicOperations.ZeroMemory(k);
        }
    }
    internal static string Public(LocalPrincipalKeyPair key) => Convert.ToBase64String(key.PublicKey);

    public static async Task BootstrapAndRetries()
    {
        using var f = new Fixture(false); var ticket = Guid.NewGuid(); using var v = f.Proof(ticket, true);
        f.Repository.PrepareOfflineBootstrap(ticket, "owner", v, f.Time.Now.AddMinutes(15));
        var auditBefore = f.Count("SELECT COUNT(*) FROM AuditEvents;");
        var duplicate = Guid.NewGuid(); using var other = f.Proof(duplicate, true);
        Reject<AuthenticationException>(() => f.Repository.PrepareOfflineBootstrap(duplicate, "other", other, f.Time.Now.AddMinutes(15)));
        using var wrong = f.Proof(ticket, true, [1]);
        Reject<AuthenticationException>(() => f.Repository.CompleteBootstrap(ticket, "owner", wrong, Public(f.OwnerKey)));
        Reject<AuthenticationException>(() => f.Repository.CompleteBootstrap(ticket, "other", v, Public(f.OwnerKey)));
        Reject<AuthenticationException>(() => f.Repository.CompleteBootstrap(ticket, "owner", v, "bad-key"));
        Check(f.Count("SELECT COUNT(*) FROM LocalPrincipals;") == 0, "Rejected bootstrap created authority.");
        // Force the final audit insert to fail: identity, consumption and Initialized must all roll back.
        f.Sql("CREATE TRIGGER fail_audit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT, 'test audit failure'); END;");
        Reject<SqliteException>(() => f.Repository.CompleteBootstrap(ticket, "owner", v, Public(f.OwnerKey)));
        Check(f.Count("SELECT COUNT(*) FROM LocalPrincipals;") == 0 && f.Count("SELECT COUNT(*) FROM PendingOwnerEnrollments WHERE ConsumedUtc IS NOT NULL;") == 0 &&
            f.Text("SELECT HostBootstrapState FROM HostIdentity;") == "Uninitialized", "Failed audit left partial bootstrap authority.");
        f.Sql("DROP TRIGGER fail_audit;");
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => f.Repository.CompleteBootstrap(ticket, "owner", v, Public(f.OwnerKey)))));
        Check(results.Distinct().Count() == 1 && HostIdentityRepository.CountActiveOwners(f.Writer) == 1, "Concurrent bootstrap created duplicate Owners/results.");
        f.Time.Now = f.Time.Now.AddDays(2);
        Check(f.Repository.CompleteBootstrap(ticket, "owner", v, Public(f.UserKey)) == results[0], "Consumed bootstrap retry expired.");
        Check(f.Text("SELECT PublicVerificationKey FROM LocalPrincipals WHERE IsOwner=1;") == Public(f.OwnerKey), "Bootstrap retry replaced the key.");
        Check(f.Count("SELECT COUNT(*) FROM AuditEvents;") == auditBefore + 1, "Bootstrap retry repeated audit/write transaction.");
        Reject<AuthenticationException>(() => f.Repository.CompleteBootstrap(ticket, "owner", wrong, Public(f.OwnerKey)));
        Reject<AuthenticationException>(() => f.Repository.PrepareOfflineBootstrap(duplicate, "other", other, f.Time.Now.AddMinutes(15)));
        using var expired = new Fixture(false); var old = Guid.NewGuid(); using var oldProof = expired.Proof(old, true);
        expired.Repository.PrepareOfflineBootstrap(old, "owner", oldProof, expired.Time.Now.AddSeconds(1)); expired.Time.Now = expired.Time.Now.AddSeconds(1);
        Reject<AuthenticationException>(() => expired.Repository.CompleteBootstrap(old, "owner", oldProof, Public(expired.OwnerKey)));
        var fresh = Guid.NewGuid(); using var newProof = expired.Proof(fresh, true);
        expired.Repository.PrepareOfflineBootstrap(fresh, "owner", newProof, expired.Time.Now.AddMinutes(15));
        Check(expired.Count("SELECT COUNT(*) FROM PendingOwnerEnrollments WHERE InvalidatedUtc IS NOT NULL;") == 1, "Expired bootstrap not retired.");
        Reject<AuthenticationException>(() => expired.Repository.CompleteBootstrap(old, "owner", oldProof, Public(expired.OwnerKey)));
        Check(expired.Repository.CompleteBootstrap(fresh, "owner", newProof, Public(expired.OwnerKey)) != Guid.Empty, "Fresh privileged preparation could not recover expiry.");
    }

    public static async Task EnrollmentAttemptsAndConcurrency()
    {
        using var f = new Fixture(); var id = f.Invite(); using var v = f.Proof(id); using var wrong = f.Proof(id, code: [7]);
        for (var n = 0; n < 9; n++) Reject<AuthenticationException>(() => f.Repository.CompleteEnrollment(id, "user", wrong, Public(f.UserKey)));
        Check(f.Count("SELECT FailedAttempts FROM PendingLocalPrincipalEnrollments;") == 9, "Wrong-code attempts were not durable.");
        var user = f.Repository.CompleteEnrollment(id, "user", v, Public(f.UserKey));
        var audit = f.Count("SELECT COUNT(*) FROM AuditEvents;"); f.Time.Now = f.Time.Now.AddDays(2);
        Check(f.Repository.CompleteEnrollment(id, "user", v, Public(f.OwnerKey)) == user, "Consumed enrollment retry expired.");
        Reject<AuthenticationException>(() => f.Repository.CompleteEnrollment(id, "user", wrong, Public(f.UserKey)));
        f.Repository.RevokePrincipal(f.Actor, user);
        Check(f.Repository.CompleteEnrollment(id, "user", v, "ignored-on-retry") == user &&
            f.Text($"SELECT State FROM LocalPrincipals WHERE LocalPrincipalId='{user:D}';") == "Revoked", "Consumed retry restored revoked authority.");
        Check(f.Count("SELECT COUNT(*) FROM AuditEvents;") == audit + 1, "Retry wrote a new audit event.");
        var locked = f.Invite(); using var lockedProof = f.Proof(locked); using var bad = f.Proof(locked, code: []);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            Reject<AuthenticationException>(() => f.Repository.CompleteEnrollment(locked, "user", bad, Public(f.UserKey))))));
        Check(f.Count($"SELECT FailedAttempts FROM PendingLocalPrincipalEnrollments WHERE EnrollmentId='{locked:D}';") == 10, "Concurrent guesses bypassed/corrupted ten-attempt cap.");
        Reject<AuthenticationException>(() => f.Repository.CompleteEnrollment(locked, "user", lockedProof, Public(f.UserKey)));
        var replacement = f.Invite(); using var good = f.Proof(replacement);
        f.Sql("CREATE TRIGGER fail_audit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT, 'test audit failure'); END;");
        Reject<SqliteException>(() => f.Repository.CompleteEnrollment(replacement, "user", good, Public(f.UserKey)));
        Check(f.Text($"SELECT State FROM LocalPrincipals WHERE LocalPrincipalId='{user:D}';") == "Revoked" &&
            f.Count($"SELECT COUNT(*) FROM PendingLocalPrincipalEnrollments WHERE EnrollmentId='{replacement:D}' AND ConsumedUtc IS NOT NULL;") == 0 &&
            f.Count($"SELECT COUNT(*) FROM PendingLocalPrincipalEnrollments WHERE EnrollmentId='{locked:D}' AND InvalidatedUtc IS NOT NULL;") == 0,
            "Enrollment audit failure left a partially reactivated row or invalidated sibling.");
        f.Sql("DROP TRIGGER fail_audit;");
        var renewed = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => f.Repository.CompleteEnrollment(replacement, "user", good, Public(f.UserKey)))));
        Check(renewed.All(x => x == user) && f.Count("SELECT COUNT(*) FROM LocalPrincipals;") == 2, "Concurrent reactivation duplicated identity.");
        Reject<AuthenticationException>(() => f.Repository.CompleteEnrollment(locked, "user", lockedProof, Public(f.UserKey)));
        var a = f.Invite("new-user"); var b = f.Invite("new-user"); using var va = f.Proof(a); using var vb = f.Proof(b);
        var winner = f.Repository.CompleteEnrollment(b, "new-user", vb, Public(f.UserKey));
        Reject<AuthenticationException>(() => f.Repository.CompleteEnrollment(a, "new-user", va, Public(f.OwnerKey)));
        Check(f.Count("SELECT COUNT(*) FROM LocalPrincipals WHERE OsPrincipalRef='new-user';") == 1 && winner != user, "Duplicate new ticket created/overwrote a principal.");
        var exp = f.Invite("expired"); using var ev = f.Proof(exp); f.Time.Now = f.Time.Now.AddMinutes(15);
        Reject<AuthenticationException>(() => f.Repository.CompleteEnrollment(exp, "expired", ev, Public(f.UserKey)));
        Check(f.Count("SELECT COUNT(*) FROM LocalPrincipals WHERE OsPrincipalRef='expired';") == 0, "Expired first use created a principal.");
    }

    public static Task RevocationAndAba()
    {
        using var f = new Fixture(); var user = f.Enroll(); var independent = f.Enroll("independent");
        foreach (var table in new[] { "HostCapabilityGrants", "ServerCapabilityGrants" })
        {
            var targetColumns = table == "HostCapabilityGrants" ? "TargetHostId" : "AuthoritativeHostId,ServerProfileId";
            var targetValues = table == "HostCapabilityGrants" ? $"'{f.HostId:D}'" : $"'{f.HostId:D}','server'";
            void Grant(string id, Guid grantee, Guid issuer, string? parent) => f.Sql($"""
                INSERT INTO {table} (GrantId,{targetColumns},Capability,GranteeActorKind,GranteeLocalPrincipalId,
                GrantedByActorKind,GrantedByLocalPrincipalId,CanDelegate,CanDelegateOnwardDelegation,DerivedFromGrantId,CreatedUtc)
                VALUES ('{id}',{targetValues},'test-capability','LocalPrincipal','{grantee:D}',
                'LocalPrincipal','{issuer:D}',1,1,{(parent is null ? "NULL" : "'" + parent + "'")},'test');
                """);
            Grant("root", user, f.Owner, null); Grant("child", independent, user, "root");
            Grant("grandchild", f.Owner, independent, "child"); Grant("unrelated", independent, f.Owner, null);
        }
        Reject<AuthenticationException>(() => f.Repository.RevokePrincipal(f.Actor, f.Owner));
        // A retained recovery candidate targeting this principal must be invalidated by revocation.
        f.Sql($"""
            INSERT INTO PendingOwnerRehomes (RehomeTicketId,NewOsPrincipalRef,SecretVerifier,ExpectedCurrentOwnerLocalPrincipalId,
                ExpectedCurrentOwnerPublicVerificationKey,ExpectedTargetLocalPrincipalId,ExpectedTargetState,ExpectedTargetPublicVerificationKey,ExpiresUtc,CreatedUtc)
            VALUES ('rehome','user','test-verifier','{f.Owner:D}','{Public(f.OwnerKey)}','{user:D}','Active','{Public(f.UserKey)}','later','test');
            """);
        var before = f.Count("SELECT COUNT(*) FROM AuditEvents;");
        f.Sql("CREATE TRIGGER fail_audit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT, 'test audit failure'); END;");
        Reject<SqliteException>(() => f.Repository.RevokePrincipal(f.Actor, user));
        Check(f.Text($"SELECT State FROM LocalPrincipals WHERE LocalPrincipalId='{user:D}';") == "Active" &&
            f.Count("SELECT COUNT(*) FROM HostCapabilityGrants WHERE InvalidatedUtc IS NOT NULL;") == 0 &&
            f.Count("SELECT COUNT(*) FROM PendingOwnerRehomes WHERE InvalidatedUtc IS NOT NULL;") == 0, "Revocation audit failure left a partial cascade.");
        f.Sql("DROP TRIGGER fail_audit;"); f.Repository.RevokePrincipal(f.Actor, user);
        Check(f.Count("SELECT COUNT(*) FROM AuditEvents;") == before + 1 && f.Count("SELECT COUNT(*) FROM PendingOwnerRehomes WHERE InvalidatedUtc IS NOT NULL;") == 1, "Revocation was not audited or missed dependent recovery ticket.");
        foreach (var table in new[] { "HostCapabilityGrants", "ServerCapabilityGrants" })
            Check(f.Count($"SELECT COUNT(*) FROM {table} WHERE InvalidatedUtc IS NOT NULL;") == 3 &&
                f.Count($"SELECT COUNT(*) FROM {table} WHERE GrantId='unrelated' AND InvalidatedUtc IS NULL;") == 1, "Grant provenance cascade altered unrelated authority or left descendants live.");
        var stale = f.Invite(); var fresh = f.Invite(); using var old = f.Proof(stale); using var current = f.Proof(fresh);
        f.Sql($"""
            INSERT INTO PendingOwnerRehomes (RehomeTicketId,NewOsPrincipalRef,SecretVerifier,ExpectedCurrentOwnerLocalPrincipalId,
                ExpectedCurrentOwnerPublicVerificationKey,ExpectedTargetLocalPrincipalId,ExpectedTargetState,ExpectedTargetPublicVerificationKey,ExpiresUtc,CreatedUtc)
            VALUES ('rehome-aba','user','test-verifier','{f.Owner:D}','{Public(f.OwnerKey)}','{user:D}','Revoked',NULL,'later','test');
            """);
        Check(f.Repository.CompleteEnrollment(fresh, "user", current, Public(f.UserKey)) == user, "Reactivation changed semantic identity.");
        Check(f.Count("SELECT COUNT(*) FROM PendingOwnerRehomes WHERE RehomeTicketId='rehome-aba' AND InvalidatedUtc IS NOT NULL;") == 1,
            "Reactivation left a recovery ticket valid through ABA.");
        f.Repository.RevokePrincipal(f.Actor, user);
        Reject<AuthenticationException>(() => f.Repository.CompleteEnrollment(stale, "user", old, Public(f.OwnerKey)));
        Check(f.Text($"SELECT State FROM LocalPrincipals WHERE LocalPrincipalId='{user:D}';") == "Revoked", "Stale ticket survived full ABA cycle.");
        Check(f.Count("SELECT COUNT(*) FROM HostCapabilityGrants WHERE InvalidatedUtc IS NOT NULL;") == 3, "Reactivation restored prior grants.");
        Check(HostIdentityRepository.CountActiveOwners(f.Writer) == 1, "Ordinary lifecycle changed Owner cardinality.");
        return Task.CompletedTask;
    }

    public static async Task HostBoundaryAndRedaction()
    {
        using var f = new Fixture(); using var owner = f.Authenticate(f.Owner, "owner", f.OwnerKey);
        using var invitation = await f.Service.CreateEnrollmentAsync(owner, "user");
        var code = invitation.Code.CopyBytes();
        try
        {
            Check(f.Secrets.Writes == 0 && f.Secrets.LastRead!.All(b => b == 0), "Online verifier key was persisted or left uncleared.");
            Reject<AuthenticationException>(() => f.Repository.CreateEnrollment(f.Actor with { PublicVerificationKey = Public(f.UserKey) }, Guid.NewGuid(), "outsider", f.Proof(Guid.NewGuid()), f.Time.Now.AddMinutes(15)));
            Reject<AuthenticationException>(() => f.Repository.RevokePrincipal(f.Actor with { HostId = Guid.NewGuid() }, f.Owner));
            Reject<AuthenticationException>(() => f.Repository.RevokePrincipal(f.Actor with { OsPrincipalRef = "user" }, f.Owner));
            await RejectAsync<AuthenticationException>(() => f.Service.CompleteEnrollmentAsync(invitation.TicketId, "other", code, Public(f.UserKey)));
            var user = await f.Service.CompleteEnrollmentAsync(invitation.TicketId, "user", code, Public(f.UserKey));
            using var unprivileged = f.Authenticate(user, "user", f.UserKey);
            await RejectAsync<AuthenticationException>(() => f.Service.CreateEnrollmentAsync(unprivileged, "outsider"));
            Reject<AuthenticationException>(() => f.Service.RevokePrincipal(unprivileged, f.Owner));
            Reject<AuthenticationException>(() => f.Service.RevokePrincipal(owner, f.Owner));
            f.Service.RevokePrincipal(owner, user);
            Reject<AuthenticationException>(() => unprivileged.GetCurrentPrincipal());
            var count = f.Count("SELECT COUNT(*) FROM PendingLocalPrincipalEnrollments;");
            f.Secrets.Key = null; await RejectAsync<CryptographicException>(() => f.Service.CreateEnrollmentAsync(owner, "outsider"));
            f.Secrets.Key = [1]; await RejectAsync<CryptographicException>(() => f.Service.CreateEnrollmentAsync(owner, "outsider"));
            f.Secrets.Key = f.Key.ToArray();
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await RejectAsync<OperationCanceledException>(() => f.Service.CreateEnrollmentAsync(owner, "outsider", cancel.Token));
            Check(f.Count("SELECT COUNT(*) FROM PendingLocalPrincipalEnrollments;") == count && f.Secrets.Writes == 0, "Failed secret read/cancellation created an enrollment.");
            // Force credential change AFTER the connection snapshot but BEFORE the writer transaction.
            f.Secrets.OnRead = () => f.Sql($"UPDATE LocalPrincipals SET PublicVerificationKey='{Public(f.UserKey)}' WHERE IsOwner=1;");
            await RejectAsync<AuthenticationException>(() => f.Service.CreateEnrollmentAsync(owner, "stale-owner-request"));
            Check(f.Count("SELECT COUNT(*) FROM PendingLocalPrincipalEnrollments;") == count, "Stale Owner authentication authorized a later write.");
            f.Secrets.OnRead = null; f.Sql($"UPDATE LocalPrincipals SET PublicVerificationKey='{Public(f.OwnerKey)}' WHERE IsOwner=1;");
            using var verifier = f.Proof(invitation.TicketId, code: code);
            var encodedVerifier = verifier.ExportForPersistence();
            var diagnostics = JsonSerializer.Serialize(new { Invitation = invitation, Verifier = verifier }) + invitation + verifier;
            Check(!diagnostics.Contains(encodedVerifier) && !diagnostics.Contains(Convert.ToBase64String(code)) && diagnostics.Contains("[REDACTED]"), "Diagnostic serialization exposed code/verifier.");
            Check(typeof(LocalEnrollmentVerifier).GetProperties().Length == 0, "Verifier destructuring exposes secret material.");
            using var differentHost = LocalEnrollmentVerifier.Compute(f.Key, Guid.NewGuid(), LocalEnrollmentPurpose.AdditionalPrincipal, invitation.TicketId, code);
            using var differentPurpose = LocalEnrollmentVerifier.Compute(f.Key, f.HostId, LocalEnrollmentPurpose.InitialOwner, invitation.TicketId, code);
            using var differentTicket = f.Proof(Guid.NewGuid(), code: code);
            Check(!differentHost.MatchesPersisted(encodedVerifier) && !differentPurpose.MatchesPersisted(encodedVerifier) && !differentTicket.MatchesPersisted(encodedVerifier), "Verifier is not domain/Host/ticket bound.");
            var audit = f.Text("SELECT group_concat(Summary || EventKind,' ') FROM AuditEvents;");
            Check(!audit.Contains(encodedVerifier) && !audit.Contains(Convert.ToBase64String(code)), "Audit contains bearer/verifier material.");
            f.Writer.Close(); SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(f.Root))
            {
                var bytes = File.ReadAllBytes(file);
                Check(bytes.AsSpan().IndexOf(code) < 0 && bytes.AsSpan().IndexOf(f.Key) < 0 && bytes.AsSpan().IndexOf(f.UserKey.PrivateKey) < 0,
                    "Authoritative persistence contains raw code, HMAC key or private principal key.");
                Check(!Encoding.UTF8.GetString(bytes).Contains(Convert.ToBase64String(code)) && !Encoding.UTF8.GetString(bytes).Contains(Convert.ToBase64String(f.Key)),
                    "Authoritative persistence contains encoded bearer or HMAC key.");
            }
        }
        finally { CryptographicOperations.ZeroMemory(code); }
    }
}
