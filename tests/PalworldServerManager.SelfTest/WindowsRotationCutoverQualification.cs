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

// Disposable service workload, under its existing machine lease, before any main listener.
// Distinct fixture identity/cache and database; synthetic Owner, no peers or live ceremony.
internal static class WindowsRotationCutoverQualification
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("Service rotation qualification: " + message); }
    private sealed class NoActivation : IPeerActivationHook
    {
        public void Apply(SqliteConnection c, SqliteTransaction tx, PeerActivationContext activation)
            => throw new Exception("Empty-peer qualification unexpectedly activated trust.");
    }
    private sealed class FailFinalPublication(ILocalHostTrustPublisher actual, string next) : ILocalHostTrustPublisher
    {
        internal int Staged, Failed;
        public async Task PublishAsync(LocalHostTrustPublication p, CancellationToken ct = default)
        {
            if (p.CurrentHostCredentialFingerprint == next && p.PendingHostCredentialFingerprint is null)
            { Failed++; throw new IOException("Injected post-commit publication failure."); }
            await actual.PublishAsync(p, ct); Staged++;
        }
    }

    internal static async Task Run(string root, Guid hostId, SecurityIdentifier serviceSid,
        string publicDirectory, CancellationToken ct)
    {
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var ownedRoot = Path.GetFullPath(Path.Combine(parent, "rotation-cutover-" + hostId.ToString("N")));
        Check(ownedRoot.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Unsafe fixture root.");
        Check(!Directory.Exists(ownedRoot) && !File.Exists(ownedRoot), "Refusing to adopt a prior fixture database.");
        var database = new HostDatabase(new HostDataRoot(ownedRoot));
        var store = new WindowsSecureCredentialStore(root, serviceSid);
        var material = new WindowsHostCredentialMaterial(store);
        var cache = new WindowsHostTlsCredentialCache(hostId, serviceSid, store);
        var publisher = new WindowsLocalHostTrustPublisher(publicDirectory, serviceSid);
        var reader = new WindowsLocalHostTrustReader(publicDirectory, serviceSid);
        var references = new List<string> { "tls-cutover-" + hostId.ToString("N") + "-old" };
        using var writer = database.OpenConnection();
        try
        {
            new HostSchemaMigrationRunner(HostSchema.AllMigrations()).Migrate(writer);
            var identity = new HostIdentityRepository(database);
            identity.EnsureHostIdentity(writer, hostIdFactory: () => hostId.ToString("D"));
            var state = new HostCredentialStateRepository(database, hostId);
            state.PlanCredential(references[0]);
            var oldPin = await material.CreateAsync(hostId, references[0], ct);
            state.RecordCreated(references[0], oldPin); state.InstallInitial(references[0]);
            var owner = new LocalPrincipalMutationActor(hostId, Guid.NewGuid(), serviceSid.Value, "fixture-public");
            using (var tx = writer.BeginTransaction())
            { identity.InitializeWithOwner(writer, tx, owner.LocalPrincipalId.ToString("D"), serviceSid.Value, "fixture-public"); tx.Commit(); }

            await publisher.PublishAsync(new(hostId, oldPin), ct);
            using (var oldCertificate = await cache.LoadAsync(references[0], ct))
            {
                await Negotiate(database, hostId, serviceSid, store, oldCertificate, reader, ct);
                // Reserve first so cleanup knows the exact name even if material preparation fails.
                var prepared = state.PrepareRoutineRotation(owner, Guid.NewGuid());
                references.Add(prepared.NewReference);
                prepared = await new RoutineRotationMaterialCoordinator(state, material).PrepareAsync(owner, prepared.RotationId, ct);
                state.BeginRoutineRotationStaging(owner, prepared.RotationId);
                var proposal = state.PrepareRoutineRotationProposal(owner, prepared.RotationId);
                Check(proposal.OldFingerprint == oldPin && proposal.NewFingerprint != oldPin, "Prepared key did not change.");
                using var oldKey = (ECDsaCng)oldCertificate.GetECDsaPrivateKey()!;
                string nextKeyName;
                using (var preparedCertificate = await cache.LoadAsync(prepared.NewReference, ct))
                using (var preparedKey = (ECDsaCng)preparedCertificate.GetECDsaPrivateKey()!) nextKeyName = preparedKey.Key.KeyName!;
                var collector = WindowsHostComposition.CreateRotationAcceptanceCollector(new(database, hostId, new NoActivation()), oldCertificate);
                var collection = await collector.CollectAsync(prepared.RotationId, new Dictionary<Guid, Uri>(), ct);
                Check(collection.Snapshot.Peers.Count == 0 && collector.Recheck(collection).PeerAcknowledgementsReady, "Empty membership was not ready.");
                var fault = new FailFinalPublication(publisher, proposal.NewFingerprint);
                await SecureStoreTests.Reject<IOException>(() => new RoutineRotationCutoverCoordinator(state, collector, material, fault)
                    .CutOverWhileQuiescedAsync(owner, collection, ct));
                Check(fault.Staged == 1 && fault.Failed == 1, "Wrong publication boundary failed.");
                var pending = await reader.ReadAsync(ct);
                Check(pending.HostId == hostId && pending.CurrentFingerprint == oldPin && pending.PendingFingerprint == proposal.NewFingerprint &&
                    pending.PendingRotationId == prepared.RotationId, "Actual protected staging descriptor was not retained.");
                Check(state.Read().CurrentReference == prepared.NewReference && state.Read().Rotations.Single().State == HostCredentialRotationState.CutOver,
                    "Post-commit failure lost durable New.");
                // Fresh repository and material adapters, no retained coordinator/round needed.
                var reopened = new HostCredentialStateRepository(new HostDatabase(new HostDataRoot(ownedRoot)), hostId);
                var recoveredMaterial = new WindowsHostCredentialMaterial(store);
                var plan = await new HostTrustReconciler(reopened.Read,
                    (p, token) => publisher.PublishAsync(new(p.HostId, p.CurrentFingerprint, p.PendingFingerprint, p.PendingRotationId), token),
                    cache.ReconcileAsync, store.DeleteAsync, reopened.RecordRetired).ReconcileAsync(ct);
                Check(plan.Retire.Count == 0 && plan.Retained.Count == 2 && references.All(plan.Retained.Contains), "Reconciliation discarded a live key.");
                var recovered = await reader.ReadAsync(ct);
                Check(recovered.HostId == hostId && recovered.CurrentFingerprint == proposal.NewFingerprint && recovered.PendingFingerprint is null &&
                    recovered.PendingRotationId is null, "Actual recovered descriptor was not Current New.");
                Check(CngKey.Exists(oldKey.Key.KeyName!, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey) &&
                    CngKey.Exists(nextKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey), "Reconciliation deleted a retained native key.");
                await recoveredMaterial.ValidateAsync(references[0], oldPin, ct);
                await recoveredMaterial.ValidateAsync(prepared.NewReference, proposal.NewFingerprint, ct);
                using var nextCertificate = await cache.LoadAsync(prepared.NewReference, ct);
                using var nextKey = (ECDsaCng)nextCertificate.GetECDsaPrivateKey()!;
                Check(nextKey.Key.KeyName == nextKeyName, "Reconciliation regenerated the native New key.");
                Check(WindowsPeerTls.PublicFingerprint(nextCertificate) == proposal.NewFingerprint, "Native New key mismatched metadata.");
                await Negotiate(database, hostId, serviceSid, store, oldCertificate, reader, ct, expectedSuccess: false);
                await Negotiate(database, hostId, serviceSid, store, nextCertificate, reader, ct);
                Check(HostDatabase.QueryScalarLong(writer, "SELECT COUNT(*) FROM AuditEvents WHERE EventKind='HostRoutineRotationCutOver';") == 1 &&
                    HostDatabase.QueryScalarLong(writer, "SELECT COUNT(*) FROM HostCapabilityGrants;") == 0 &&
                    HostDatabase.QueryScalarLong(writer, "SELECT COUNT(*) FROM ServerCapabilityGrants;") == 0, "Cutover duplicated audit or manufactured grants.");
            }
        }
        finally
        {
            // Listener/client and borrowed certificate scopes have ended before native deletion.
            // This removes only disposable fixture material; it is not product retirement proof.
            await cache.ReconcileAsync([], CancellationToken.None);
            foreach (var reference in references) await store.DeleteAsync(reference, CancellationToken.None);
            writer.Dispose(); SqliteConnection.ClearPool(writer);
            Check((File.GetAttributes(ownedRoot) & FileAttributes.ReparsePoint) == 0, "Fixture cleanup root changed.");
            Directory.Delete(ownedRoot, true);
        }
    }

    private static async Task Negotiate(HostDatabase database, Guid hostId, SecurityIdentifier serviceSid,
        ISecureCredentialStore store, X509Certificate2 certificate, WindowsLocalHostTrustReader reader, CancellationToken ct, bool expectedSuccess = true)
    {
        var pipe = "PSMAstraRotation" + Guid.NewGuid().ToString("N");
        var delivered = 0;
        var runtime = new LocalSecurityRpcRuntime(database, hostId, store,
            context => { Interlocked.Increment(ref delivered); return WindowsLocalTlsEndpoint.ReadNativePrincipal(context); },
            _ => throw new Exception("Unexpected local authentication failure."));
        await using var app = WindowsHostComposition.BuildLocalApplication(runtime, serviceSid, serviceSid, certificate, pipe);
        await app.StartAsync(ct);
        try
        {
            using var client = new LocalSecurityRpcTests.Client(hostId, pipe, reader);
            try
            {
                var reply = await client.Negotiate();
                Check(expectedSuccess && reply.Initialized && reply.Host.HostId == hostId.ToString("D") && delivered > 0, "Actual negotiated TLS lost Host identity or accepted Old.");
            }
            catch (Grpc.Core.RpcException ex) when (!expectedSuccess)
            {
                var authenticationFailure = false;
                for (Exception? cause = ex.Status.DebugException; cause is not null; cause = cause.InnerException)
                    if (cause is System.Security.Authentication.AuthenticationException) authenticationFailure = true;
                Check(authenticationFailure && delivered == 0, "Old was not refused at TLS before request delivery.");
            }
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }
}
