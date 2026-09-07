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

// Two simulated machines on one disposable service runner. Binding is explicitly seeded;
// the qualified crash boundary is a real receiver process after New TLS promotion, not PAKE.
internal static partial class WindowsPeerReceiptCrashQualification
{
    private const string ConfigName = "receipt-process.json";
    private sealed record Config(Guid Nonce, Guid Host, Guid Peer, Guid Owner, string Root, string Sid)
    {
        internal string Mutex => @"Global\PSMReceiptCrash-" + Host.ToString("N");
        internal string Pipe => "PSMReceiptCrash" + Host.ToString("N");
        internal string Reference => "receipt-" + Host.ToString("N");
        internal string PublicDirectory => Path.Combine(Root, "public");
    }
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("Receipt process qualification: " + message); }
    private static HostExclusivityLock Lease(Config c) => HostExclusivityLock.TryAcquire(TimeSpan.Zero, c.Mutex)
        ?? throw new InvalidOperationException("Fixture Host lease is already held.");
    private sealed class RefusingPairing : IPairingKeyExchangeFactory
    {
        public IPairingKeyExchange Start(PairingRole role, byte[] code, byte[] nonce, CancellationToken ct = default)
            => throw new CryptographicException("Receipt fixture cannot produce PAKE proof.");
    }
    private sealed class Activation : IPeerActivationHook
    {
        public void Apply(SqliteConnection c, SqliteTransaction tx, PeerActivationContext context)
        {
            using var q = c.CreateCommand(); q.Transaction = tx;
            q.CommandText = "INSERT INTO ReceiptFixtureActivation VALUES ($peer);";
            q.Parameters.AddWithValue("$peer", context.PeerHostId.ToString("D")); q.ExecuteNonQuery();
        }
    }
    private sealed class Host(Config config)
    {
        internal readonly Config Config = config;
        internal HostDatabase Database => new(new HostDataRoot(Config.Root));
        internal HostCredentialStateRepository State => new(Database, Config.Host);
        internal PeerTrustRepository Peers => new(Database, Config.Host);
        internal WindowsSecureCredentialStore Store => new(Config.Root, new(Config.Sid));
        internal WindowsHostCredentialMaterial Material => new(Store);
        internal WindowsHostTlsCredentialCache Cache => new(Config.Host, new(Config.Sid), Store);
        internal LocalPrincipalMutationActor Actor => new(Config.Host, Config.Owner, Config.Sid, "receipt-fixture-public");
        internal HostGenerationTransitions Create() => WindowsHostComposition.CreateNetworkTransitions(Database, Config.Host, Store,
            new(Config.Sid), new(Config.Sid), new WindowsLocalHostTrustPublisher(Config.PublicDirectory, new(Config.Sid)), Config.Pipe,
            new(IPAddress.Loopback, 0), new(IPAddress.Loopback, 0), new RefusingPairing(), new Activation());
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
            q.CommandText = "CREATE TABLE ReceiptFixture (Nonce TEXT PRIMARY KEY); INSERT INTO ReceiptFixture VALUES ($nonce); " +
                "CREATE TABLE ReceiptFixtureActivation (Peer TEXT PRIMARY KEY);";
            q.Parameters.AddWithValue("$nonce", Config.Nonce.ToString("D")); q.ExecuteNonQuery(); tx.Commit();
            return pin;
        }
        internal void RequireFixture()
        {
            using var c = Database.OpenConnection(); using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM ReceiptFixture WHERE Nonce=$nonce;";
            q.Parameters.AddWithValue("$nonce", Config.Nonce.ToString("D"));
            Check((long)q.ExecuteScalar()! == 1 && State.Read().HostId == Config.Host && State.Read().Initialized,
                "Missing exact disposable fixture identity.");
        }
        internal void RequireNoGrants()
        {
            using var c = Database.OpenConnection();
            Check(HostDatabase.QueryScalarLong(c, "SELECT COUNT(*) FROM HostCapabilityGrants;") == 0 &&
                HostDatabase.QueryScalarLong(c, "SELECT COUNT(*) FROM ServerCapabilityGrants;") == 0 &&
                HostDatabase.QueryScalarLong(c, "SELECT COUNT(*) FROM ReceiptFixtureActivation;") == 1,
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
    private static void RequirePath(Config c)
    {
        Check(c.Nonce != Guid.Empty && c.Host != Guid.Empty && c.Peer != Guid.Empty && c.Host != c.Peer && c.Owner != Guid.Empty &&
            Path.IsPathFullyQualified(c.Root) && !c.Root.StartsWith(@"\\") && Path.GetFullPath(c.Root) == c.Root &&
            Path.GetFileName(c.Root) == "host-" + c.Host.ToString("N") &&
            Path.GetFileName(Path.GetDirectoryName(c.Root)) == "receipt-crash-" + c.Nonce.ToString("N"), "Unsafe fixture path or identity.");
        for (var item = new DirectoryInfo(c.Root); item is not null; item = item.Parent)
            if (item.Exists) Check((item.Attributes & FileAttributes.ReparsePoint) == 0, "Reparse point in fixture ancestry.");
    }
    internal static async Task Run(string serviceRoot, SecurityIdentifier sid, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var ct = timeout.Token; var nonce = Guid.NewGuid(); var aId = Guid.NewGuid(); var bId = Guid.NewGuid();
        var parent = Path.GetFullPath(serviceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(parent, "receipt-crash-" + nonce.ToString("N")));
        Check(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(root), "Unsafe or reused fixture root.");
        var a = new Host(new(nonce, aId, bId, Guid.NewGuid(), Path.Combine(root, "host-" + aId.ToString("N")), sid.Value));
        var b = new Host(new(nonce, bId, aId, Guid.NewGuid(), Path.Combine(root, "host-" + bId.ToString("N")), sid.Value));
        RequirePath(a.Config); RequirePath(b.Config);
        using var aLease = Lease(a.Config);
        HostGenerationTransitions? sender = null; Child? receiver = null; bool initialized = false;
        try
        {
            var oldPin = await a.Initialize(ct); string receiverPin;
            using (var lease = Lease(b.Config))
            {
                receiverPin = await b.Initialize(ct); initialized = true;
                // These are explicit verified-binding fixture inputs using actual public pins.
                a.Peers.RecordVerifiedBinding(bId, receiverPin, oldPin);
                b.Peers.RecordVerifiedBinding(aId, oldPin, receiverPin);
                File.WriteAllText(Path.Combine(b.Config.Root, ConfigName), JsonSerializer.Serialize(b.Config));
            }
            sender = a.Create(); await sender.StartAsync(ct);
            receiver = new Child(b.Config); var first = await receiver.Read(ct);
            Check(first.Kind == "ready" && first.Pin == receiverPin && first.CurrentPeerPin == oldPin, "Initial child state differs.");
            using (var denied = HostExclusivityLock.TryAcquire(TimeSpan.Zero, b.Config.Mutex))
                Check(denied is null, "Receiver did not hold its own fixture lease.");
            var activated = await receiver.Command("activate", sender.Endpoints!.Value.Peer, ct);
            Check(activated.Kind == "activated", "Actual activation failed."); a.RequireNoGrants();
            var rotation = a.State.PrepareRoutineRotation(a.Actor, Guid.NewGuid());
            rotation = await new RoutineRotationMaterialCoordinator(a.State, a.Material).PrepareAsync(a.Actor, rotation.RotationId, ct);
            a.State.BeginRoutineRotationStaging(a.Actor, rotation.RotationId);
            var proposal = a.State.PrepareRoutineRotationProposal(a.Actor, rotation.RotationId);
            await sender.StageRotationAsync(bId, first.Address, rotation.RotationId, ct);
            await sender.CutOverAsync(a.Actor, rotation.RotationId, new Dictionary<Guid, Uri> { [bId] = first.Address }, ct);
            var promoted = await receiver.Command("activate", sender.Endpoints!.Value.Peer, ct);
            Check(promoted.CurrentPeerPin == proposal.NewFingerprint && promoted.PendingRotation == rotation.RotationId,
                "Actual New TLS did not leave an undelivered receipt.");
            await SecureStoreTests.Reject<AuthenticationException>(() => sender.CompleteRotationAsync(a.Actor, rotation.RotationId, ct));
            await receiver.Kill(requireRunning: true); // Actual termination, without Host/generation Dispose.
            using (var lease = Lease(b.Config))
            {
                b.RequireFixture(); b.RequireNoGrants(); var persisted = b.Peers.Read(aId)!;
                Check(persisted.State == "Active" && !persisted.RecoveryRequired && persisted.CurrentFingerprint == proposal.NewFingerprint &&
                    persisted.PendingRotationId == rotation.RotationId && persisted.PendingFingerprint is null && persisted.PendingRotationExpiresUtc is null,
                    "Process exit lost durable promotion or retained receipt.");
            }
            receiver.Dispose(); receiver = null;
            receiver = new Child(b.Config); var restarted = await receiver.Read(ct);
            Check(restarted.Kind == "ready" && restarted.Instance != first.Instance && restarted.Pin == first.Pin && restarted.Key == first.Key &&
                restarted.CurrentPeerPin == proposal.NewFingerprint && restarted.PendingRotation == rotation.RotationId,
                "Fresh process changed protected/native identity or lost receipt.");
            var confirmed = await receiver.Command("receipt", sender.Endpoints!.Value.Peer, ct);
            Check(confirmed.Kind == "confirmed" && confirmed.PendingRotation is null, "Ordinary receipt retry did not clear retained ID.");
            await receiver.Stop(ct); receiver.Dispose(); receiver = null;
            using (var lease = Lease(b.Config))
            {
                b.RequireNoGrants(); Check(b.Peers.Read(aId)!.PendingRotationId is null, "Receipt acknowledgement did not survive orderly exit.");
            }
            using (var c = a.Database.OpenConnection())
                Check(HostDatabase.QueryScalarLong(c, "SELECT COUNT(*) FROM HostRotationPromotionEvidence e JOIN PeerRelationshipIncarnations i " +
                    "ON i.PeerHostId=e.PeerHostId AND i.Incarnation=e.Incarnation;") == 1, "Expected exactly one current-relationship receipt.");
            Check((await sender.CompleteRotationAsync(a.Actor, rotation.RotationId, ct)).State == HostCredentialRotationState.Completed,
                "Recovered receipt did not permit completion."); a.RequireNoGrants();
        }
        finally
        {
            // Failure to observe exit/closure must stop cleanup; never delete possibly borrowed material.
            try { if (receiver is not null) { await receiver.Kill(); receiver.Dispose(); } }
            finally { if (sender is not null) await sender.StopAsync(); }
            if (initialized)
            {
                using var lease = Lease(b.Config); await b.Cleanup(); await a.Cleanup();
                Check(Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "Unsafe cleanup root.");
                Directory.Delete(root, false);
            }
        }
    }
}
