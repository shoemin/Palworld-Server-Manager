using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

// Real service account and machine lease, with disposable exact fixture state. No live Owner.
internal static class WindowsGenerationTransitionQualification
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("Windows transition qualification: " + message); }
    private sealed class RefusingPairing : IPairingKeyExchangeFactory
    {
        public IPairingKeyExchange Start(PairingRole role, byte[] code, byte[] nonce, CancellationToken ct = default)
            => throw new CryptographicException("Fixture cannot produce PAKE proof.");
    }
    private sealed class RefusingActivation : IPeerActivationHook
    {
        public void Apply(SqliteConnection c, SqliteTransaction tx, PeerActivationContext context)
            => throw new InvalidOperationException("Empty-peer fixture cannot grant activation authority.");
    }
    private sealed class PublicationFault(WindowsLocalHostTrustPublisher actual) : ILocalHostTrustPublisher
    {
        internal string? FailCurrent;
        internal int Failed;
        public Task PublishAsync(LocalHostTrustPublication p, CancellationToken ct = default)
        {
            if (p.CurrentHostCredentialFingerprint == FailCurrent)
            { Failed++; throw new IOException("Injected final publication failure."); }
            return actual.PublishAsync(p, ct);
        }
    }
    internal static async Task Run(string root, Guid hostId, SecurityIdentifier serviceSid, string publicDirectory, CancellationToken ct)
    {
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var ownedRoot = Path.GetFullPath(Path.Combine(parent, "rotation-transitions-" + hostId.ToString("N")));
        Check(ownedRoot.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Unsafe fixture root.");
        Check(!Directory.Exists(ownedRoot) && !File.Exists(ownedRoot), "Refusing prior fixture state.");
        var database = new HostDatabase(new HostDataRoot(ownedRoot));
        var store = new WindowsSecureCredentialStore(root, serviceSid);
        var material = new WindowsHostCredentialMaterial(store);
        var cache = new WindowsHostTlsCredentialCache(hostId, serviceSid, store);
        var publisher = new PublicationFault(new(publicDirectory, serviceSid));
        var reader = new WindowsLocalHostTrustReader(publicDirectory, serviceSid);
        var references = new List<string> { "tls-transitions-" + hostId.ToString("N") + "-old" };
        var pipe = "PSMWindowsTransitions" + Guid.NewGuid().ToString("N");
        using var writer = database.OpenConnection();
        HostGenerationTransitions? owner = null;
        try
        {
            new HostSchemaMigrationRunner(HostSchema.AllMigrations()).Migrate(writer);
            var identity = new HostIdentityRepository(database); identity.EnsureHostIdentity(writer, hostIdFactory: () => hostId.ToString("D"));
            var state = new HostCredentialStateRepository(database, hostId); state.PlanCredential(references[0]);
            var oldPin = await material.CreateAsync(hostId, references[0], ct); state.RecordCreated(references[0], oldPin); state.InstallInitial(references[0]);
            var actor = new LocalPrincipalMutationActor(hostId, Guid.NewGuid(), serviceSid.Value, "fixture-public");
            using (var tx = writer.BeginTransaction())
            { identity.InitializeWithOwner(writer, tx, actor.LocalPrincipalId.ToString("D"), serviceSid.Value, actor.PublicVerificationKey); tx.Commit(); }
            owner = WindowsHostComposition.CreateNetworkTransitions(database, hostId, store, serviceSid, serviceSid, publisher, pipe,
                new(IPAddress.Loopback, 0), new(IPAddress.Loopback, 0), new RefusingPairing(), new RefusingActivation());
            await owner.StartAsync(ct);
            Check(owner.Phase == HostGenerationPhase.Serving && owner.Endpoints is not null, "Full generation did not start.");
            await Negotiate(hostId, pipe, reader, ct);
            string oldNative;
            using (var old = await cache.LoadAsync(references[0], ct))
            using (var key = (ECDsaCng)old.GetECDsaPrivateKey()!) oldNative = key.Key.KeyName!;
            var prepared = state.PrepareRoutineRotation(actor, Guid.NewGuid()); references.Add(prepared.NewReference);
            prepared = await new RoutineRotationMaterialCoordinator(state, material).PrepareAsync(actor, prepared.RotationId, ct);
            state.BeginRoutineRotationStaging(actor, prepared.RotationId); var proposal = state.PrepareRoutineRotationProposal(actor, prepared.RotationId);
            string nextNative;
            using (var next = await cache.LoadAsync(prepared.NewReference, ct))
            using (var key = (ECDsaCng)next.GetECDsaPrivateKey()!) nextNative = key.Key.KeyName!;
            publisher.FailCurrent = proposal.NewFingerprint;
            await SecureStoreTests.Reject<IOException>(() => owner.CutOverAsync(actor, prepared.RotationId, new Dictionary<Guid, Uri>(), ct));
            Check(owner.Phase == HostGenerationPhase.Quiesced && owner.Endpoints is null && publisher.Failed == 1, "Failure did not leave a closed generation.");
            Check(state.Read().CurrentReference == prepared.NewReference && state.Read().Rotations.Single().State == HostCredentialRotationState.CutOver,
                "Durable Current New was lost.");
            var staged = await reader.ReadAsync(ct);
            Check(staged.HostId == hostId && staged.CurrentFingerprint == oldPin && staged.PendingFingerprint == proposal.NewFingerprint && staged.PendingRotationId == prepared.RotationId,
                "Actual protected Old/Pending descriptor was not retained.");
            await SecureStoreTests.Reject<InvalidOperationException>(() => owner.CreateInvitationAsync(ct));
            publisher.FailCurrent = null; await owner.RecoverAsync(ct);
            var recovered = await reader.ReadAsync(ct);
            Check(owner.Phase == HostGenerationPhase.Serving && recovered.CurrentFingerprint == proposal.NewFingerprint && recovered.PendingFingerprint is null && recovered.PendingRotationId is null,
                "Actual New publication and generation did not recover.");
            await Negotiate(hostId, pipe, reader, ct);
            using (var next = await cache.LoadAsync(prepared.NewReference, ct))
            using (var key = (ECDsaCng)next.GetECDsaPrivateKey()!) Check(key.Key.KeyName == nextNative, "Native New key was regenerated.");
            Check(CngKey.Exists(oldNative, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey) &&
                CngKey.Exists(nextNative, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey), "Retained native key was deleted.");
            await material.ValidateAsync(references[0], oldPin, ct); await material.ValidateAsync(prepared.NewReference, proposal.NewFingerprint, ct);
            Check(state.Read().Credentials.All(c => !c.Retired) &&
                HostDatabase.QueryScalarLong(writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationCutOver';") == 1 &&
                HostDatabase.QueryScalarLong(writer, "SELECT COUNT(*) FROM HostCapabilityGrants;") == 0 &&
                HostDatabase.QueryScalarLong(writer, "SELECT COUNT(*) FROM ServerCapabilityGrants;") == 0, "Cutover changed retention, audit count or authority.");
            await owner.StopAsync(); Check(owner.Phase == HostGenerationPhase.Stopped && owner.Endpoints is null, "Final generation did not close.");
        }
        finally
        {
            // This deletes only disposable fixture material after complete listener/key cleanup.
            // Actual product old-key retirement still requires its separate acceptance policy.
            // A failed closure is not permission to delete possibly borrowed material. The
            // outer disposable-service harness can clean after the process has actually exited.
            if (owner is not null) await owner.StopAsync();
            await cache.ReconcileAsync([], CancellationToken.None);
            foreach (var reference in references) await store.DeleteAsync(reference, CancellationToken.None);
            writer.Dispose(); SqliteConnection.ClearPool(writer);
            Check((File.GetAttributes(ownedRoot) & FileAttributes.ReparsePoint) == 0, "Fixture root changed.");
            Directory.Delete(ownedRoot, true);
        }
    }
    private static async Task Negotiate(Guid hostId, string pipe, WindowsLocalHostTrustReader reader, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var client = new LocalSecurityRpcTests.Client(hostId, pipe, reader);
        var reply = await client.Negotiate();
        Check(reply.Initialized && reply.Host.HostId == hostId.ToString("D"), "Actual local TLS negotiation lost the Host identity.");
    }
}
