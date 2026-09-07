using System.Net;
using System.Security.AccessControl;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

// Shared disposable fixture infrastructure; never an installed Host entry point.
internal static partial class WindowsPeerProcessFixture
{
    internal const string ConfigName = "peer-process.json";
    internal sealed record Config(Guid Nonce, Guid Host, Guid Peer, Guid Owner, string Root, string Sid,
        string? NativePath = null, string? NativeHash = null)
    {
        internal string Mutex => @"Global\PSMPeerProcess-" + Host.ToString("N");
        internal string Pipe => "PSMPeerProcess" + Host.ToString("N");
        internal string Reference => "peer-process-" + Host.ToString("N");
        internal string PublicDirectory => Path.Combine(Root, "public");
    }
    internal static void Check(bool value, string message)
    { if (!value) throw new Exception("Peer process qualification: " + message); }
    internal static HostExclusivityLock Lease(Config c) => HostExclusivityLock.TryAcquire(TimeSpan.Zero, c.Mutex)
        ?? throw new InvalidOperationException("Fixture Host lease is already held.");
    private sealed class RefusingPairing : IPairingKeyExchangeFactory
    {
        public IPairingKeyExchange Start(PairingRole role, byte[] code, byte[] nonce, CancellationToken ct = default)
            => throw new CryptographicException("Fixture cannot produce PAKE proof.");
    }
    private sealed class Activation : IPeerActivationHook
    {
        public void Apply(SqliteConnection c, SqliteTransaction tx, PeerActivationContext context)
        {
            using var q = c.CreateCommand(); q.Transaction = tx;
            q.CommandText = "INSERT INTO PeerProcessFixtureActivation VALUES ($peer);";
            q.Parameters.AddWithValue("$peer", context.PeerHostId.ToString("D")); q.ExecuteNonQuery();
        }
    }
    internal sealed class FixtureHost(Config config)
    {
        internal readonly Config Config = config;
        internal HostDatabase Database => new(new HostDataRoot(Config.Root));
        internal HostCredentialStateRepository State => new(Database, Config.Host);
        internal PeerTrustRepository Peers => new(Database, Config.Host);
        internal WindowsSecureCredentialStore Store => new(Config.Root, new(Config.Sid));
        internal WindowsHostCredentialMaterial Material => new(Store);
        internal WindowsHostTlsCredentialCache Cache => new(Config.Host, new(Config.Sid), Store);
        internal LocalPrincipalMutationActor Actor => new(Config.Host, Config.Owner, Config.Sid, "receipt-fixture-public");
        internal HostGenerationTransitions Create(IPairingKeyExchangeFactory? pairing = null) => WindowsHostComposition.CreateNetworkTransitions(Database, Config.Host, Store,
            new(Config.Sid), new(Config.Sid), new WindowsLocalHostTrustPublisher(Config.PublicDirectory, new(Config.Sid)), Config.Pipe,
            new(IPAddress.Loopback, 0), new(IPAddress.Loopback, 0), pairing ?? new RefusingPairing(), new Activation());
        internal async Task<string> Initialize(CancellationToken ct)
        {
            Check(!Directory.Exists(Config.Root) && !File.Exists(Config.Root), "Refusing existing fixture state.");
            // A nested directory inherits permissions but is not itself protected from
            // inheritance. Provision the new fixture root explicitly before the database;
            // the production store must continue refusing unprotected roots.
            Directory.CreateDirectory(Path.GetDirectoryName(Config.Root)!);
            var acl = WindowsHostPlatform.BuildHostDirectoryAcl(new(Config.Sid));
            acl.SetOwner(new SecurityIdentifier(Config.Sid)); new DirectoryInfo(Config.Root).Create(acl);
            // The service publisher also deliberately requires prior provisioning. This
            // fixture creates only its own new directory, using the exact platform policy.
            var publicAcl = new PublicTrustFileSecurity(new(Config.Sid)).NewDirectory();
            publicAcl.SetOwner(new SecurityIdentifier(Config.Sid)); new DirectoryInfo(Config.PublicDirectory).Create(publicAcl);
            using var c = Database.OpenConnection(); new HostSchemaMigrationRunner(HostSchema.AllMigrations()).Migrate(c);
            var identity = new HostIdentityRepository(Database);
            identity.EnsureHostIdentity(c, hostIdFactory: () => Config.Host.ToString("D"));
            State.PlanCredential(Config.Reference); var pin = await Material.CreateAsync(Config.Host, Config.Reference, ct);
            State.RecordCreated(Config.Reference, pin); State.InstallInitial(Config.Reference);
            using var tx = c.BeginTransaction();
            identity.InitializeWithOwner(c, tx, Config.Owner.ToString("D"), Config.Sid, Actor.PublicVerificationKey);
            using var q = c.CreateCommand(); q.Transaction = tx;
            q.CommandText = "CREATE TABLE PeerProcessFixture (Nonce TEXT PRIMARY KEY); INSERT INTO PeerProcessFixture VALUES ($nonce); " +
                "CREATE TABLE PeerProcessFixtureActivation (Peer TEXT PRIMARY KEY);";
            q.Parameters.AddWithValue("$nonce", Config.Nonce.ToString("D")); q.ExecuteNonQuery(); tx.Commit();
            return pin;
        }
        internal void RequireFixture()
        {
            using var c = Database.OpenConnection(); using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM PeerProcessFixture WHERE Nonce=$nonce;";
            q.Parameters.AddWithValue("$nonce", Config.Nonce.ToString("D"));
            Check((long)q.ExecuteScalar()! == 1 && State.Read().HostId == Config.Host && State.Read().Initialized,
                "Missing exact disposable fixture identity.");
        }
        internal void RequireNoGrants(int effects = 1)
        {
            using var c = Database.OpenConnection();
            Check(HostDatabase.QueryScalarLong(c, "SELECT COUNT(*) FROM HostCapabilityGrants;") == 0 &&
                HostDatabase.QueryScalarLong(c, "SELECT COUNT(*) FROM ServerCapabilityGrants;") == 0 &&
                HostDatabase.QueryScalarLong(c, "SELECT COUNT(*) FROM PeerProcessFixtureActivation;") == effects,
                "Activation retry changed effects or canonical grants.");
        }
        internal async Task<(string Pin, string Key)> Credential(CancellationToken ct)
        {
            var s = State.Read(); var pin = s.Credentials.Single(c => c.Reference == s.CurrentReference).PublicKeyFingerprint!;
            await Material.ValidateAsync(s.CurrentReference!, pin, ct);
            using var cert = await Cache.LoadAsync(s.CurrentReference!, ct);
            using var key = (ECDsaCng)cert.GetECDsaPrivateKey()!;
            return (pin, key.Key.KeyName!); // Public identity/container name only; never export private material.
        }
        internal async Task Cleanup()
        {
            // Caller must have observed process exit/whole generation closure and hold this fixture lease.
            if (!Directory.Exists(Config.Root)) return;
            RequireFixture(); var references = State.Read().Credentials.Select(c => c.Reference).ToArray();
            await Cache.ReconcileAsync([], CancellationToken.None);
            foreach (var reference in references) await Store.DeleteAsync(reference, CancellationToken.None);
            using (var c = Database.OpenConnection()) SqliteConnection.ClearPool(c);
            RequirePath(Config); Directory.Delete(Config.Root, true);
        }
    }
    internal static void RequirePath(Config c)
    {
        Check(c.Nonce != Guid.Empty && c.Host != Guid.Empty && c.Peer != Guid.Empty && c.Host != c.Peer && c.Owner != Guid.Empty &&
            Path.IsPathFullyQualified(c.Root) && !c.Root.StartsWith(@"\\") && Path.GetFullPath(c.Root) == c.Root &&
            Path.GetFileName(c.Root) == "host-" + c.Host.ToString("N") &&
            Path.GetFileName(Path.GetDirectoryName(c.Root)) == "peer-process-" + c.Nonce.ToString("N"), "Unsafe fixture path or identity.");
        for (var item = new DirectoryInfo(c.Root); item is not null; item = item.Parent)
            if (item.Exists) Check((item.Attributes & FileAttributes.ReparsePoint) == 0, "Reparse point in fixture ancestry.");
    }
}
