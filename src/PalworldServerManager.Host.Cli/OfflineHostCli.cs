using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.Host.Cli;

// Trusted composition parameters for isolated actual-Windows tests, absent from CLI/RPC input.
// The shipped executable always selects the one product Host.
public sealed record OfflineHostLocation(string ServiceName, string ActivationGroup, string HostRoot,
    string PublicTrustRoot, string HandoffRoot, string MutexName)
{
    public static OfflineHostLocation Product()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PalworldServerManager");
        return new(WindowsHostPlatform.ProductServiceName, WindowsHostPlatform.ProductActivationGroup,
            Path.Combine(root, "Host"), Path.Combine(root, "PublicTrust"), Path.Combine(root, "OwnerHandoffs"), HostExclusivityLock.DefaultMutexName);
    }
}

public static class OfflineHostCli
{
    // Single Windows composition root. No online Host/Contracts/client path.
    public static async Task<int> RunAsync(string[] args, OfflineHostLocation? trustedLocation = null,
        TextWriter? output = null, TextWriter? error = null, CancellationToken ct = default)
    {
        output ??= Console.Out; error ??= Console.Error;
        try
        {
            var command = OfflineCommand.Parse(args);
            WindowsHostPlatform.RequireOfflineElevation(ct);
            var requestedRecipient = command.IntendedSid is null ? null : new SecurityIdentifier(command.IntendedSid);
            if (requestedRecipient is not null) WindowsOwnerHandoffWriter.ValidateRecipient(requestedRecipient);
            var location = trustedLocation ?? OfflineHostLocation.Product();
            var platform = new WindowsHostPlatform(location.ServiceName, location.ActivationGroup, location.HostRoot);
            async Task Stopped()
            {
                if (await platform.GetStateAsync(ct).ConfigureAwait(false) != HostServiceState.Stopped)
                    throw new InvalidOperationException("Stop the Host service before offline preparation or recovery.");
            }
            await Stopped().ConfigureAwait(false);
            using var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, location.MutexName)
                ?? throw new InvalidOperationException("The machine Host lease is held; offline access refused.");
            await Stopped().ConfigureAwait(false); // concurrent service startup still cannot obtain our lease
            var serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", location.ServiceName).Translate(typeof(SecurityIdentifier));
            platform.ValidateOfflineDataRoot(serviceSid, ct);
            ct.ThrowIfCancellationRequested();
            var database = new HostDatabase(new HostDataRoot(location.HostRoot));
            HostIdentityRecord identity;
            using (var connection = database.OpenConnection())
            {
                try
                {
                    HostSchemaMigrationRunner.Default().Migrate(connection);
                    ct.ThrowIfCancellationRequested();
                    identity = new HostIdentityRepository(database).EnsureHostIdentity(connection);
                }
                finally { SqliteConnection.ClearPool(connection); }
            }
            var hostId = Guid.ParseExact(identity.HostId, "D");
            var state = new HostCredentialStateRepository(database, hostId);
            var enrollment = new LocalEnrollmentRepository(database, hostId);
            var store = new WindowsSecureCredentialStore(location.HostRoot, serviceSid);
            var material = new WindowsHostCredentialMaterial(store);
            var native = new WindowsHostTlsCredentialCache(hostId, serviceSid, store);
            WindowsLocalHostTrustPublisher.Provision(location.PublicTrustRoot, serviceSid);
            var publisher = new WindowsLocalHostTrustPublisher(location.PublicTrustRoot, serviceSid);
            var reconciler = new HostTrustReconciler(state.Read,
                (p, token) => publisher.PublishAsync(new(p.HostId, p.CurrentFingerprint, p.PendingFingerprint, p.PendingRotationId), token),
                native.ReconcileAsync, store.DeleteAsync, state.RecordRetired);
            try { await reconciler.ReconcileAsync(ct).ConfigureAwait(false); }
            catch (HostTrustMetadataUnavailableException) when (command.Kind == "recover-machine")
            {
                // Check attempted; only explicit fresh recovery may repair missing legacy public
                // metadata. Never read Old's private key or infer its fingerprint from the file.
                await error.WriteLineAsync("Existing public metadata unavailable; continuing explicit fresh machine recovery.").ConfigureAwait(false);
            }
            WindowsNativeTlsProvisioning.EnsureCreatePermission(serviceSid);
            async Task<string> CreateCredential()
            {
                var reference = "host-tls-" + Guid.NewGuid().ToString("N"); state.PlanCredential(reference);
                var fingerprint = await material.CreateAsync(hostId, reference, ct).ConfigureAwait(false);
                state.RecordCreated(reference, fingerprint); return reference;
            }
            Task CommitCredential(Action commit) => OfflinePublicationBarrier.CommitAndCompleteAsync(commit,
                () => reconciler.ReconcileAsync(CancellationToken.None),
                () => error.WriteLine("Credential metadata committed. Retaining the machine lease while publication/cleanup is retried."), ct);

            if (command.Kind == "recover-machine")
            {
                var reference = await CreateCredential().ConfigureAwait(false);
                await CommitCredential(() => state.ReplaceOffline(reference, command.RecoveryReason == "loss" ? MachineCredentialRecoveryReason.CredentialLoss : MachineCredentialRecoveryReason.SuspectedCompromise)).ConfigureAwait(false);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { hostId, result = "MachineCredentialRecovered", reason = command.RecoveryReason })).ConfigureAwait(false);
                return 0;
            }
            if (state.Read().CurrentReference is null)
            {
                if (command.Kind != "bootstrap") throw new InvalidOperationException("Initial machine bootstrap is required.");
                var reference = await CreateCredential().ConfigureAwait(false);
                await CommitCredential(() => state.InstallInitial(reference)).ConfigureAwait(false);
            }
            var current = HostTrustPlanning.Build(state.Read()).Publication!;
            await material.ValidateAsync(state.Read().CurrentReference!, current.CurrentFingerprint, ct).ConfigureAwait(false);
            await material.EnsureEnrollmentKeyAsync(hostId, state.HasEnrollmentHistory(), ct).ConfigureAwait(false);
            var recipient = requestedRecipient ?? new SecurityIdentifier(enrollment.ReadOfflineOwnerIdentity().OsPrincipalRef);
            var purpose = command.Kind switch
            {
                "bootstrap" => LocalEnrollmentPurpose.InitialOwner,
                "rotate-owner" => LocalEnrollmentPurpose.OwnerRotation,
                "rehome-owner" => LocalEnrollmentPurpose.OwnerRehome,
                _ => throw new InvalidOperationException("Unknown offline command.")
            };
            var handoff = new WindowsOwnerHandoffWriter(location.HandoffRoot); var handoffCreated = false;
            var ticketId = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32); byte[]? verifierKey = null;
            try
            {
                verifierKey = await store.RetrieveAsync(LocalEnrollmentVerifier.KeyName(hostId), ct).ConfigureAwait(false)
                    ?? throw new CryptographicException("Enrollment verifier key unavailable.");
                using var verifier = LocalEnrollmentVerifier.Compute(verifierKey, hostId, purpose, ticketId, secret);
                await handoff.WriteAsync(hostId, ticketId, purpose, recipient, secret, ct).ConfigureAwait(false); handoffCreated = true;
                ct.ThrowIfCancellationRequested(); var expires = DateTimeOffset.UtcNow.AddMinutes(15);
                switch (purpose)
                {
                    case LocalEnrollmentPurpose.InitialOwner: enrollment.PrepareOfflineBootstrap(ticketId, recipient.Value, verifier, expires); break;
                    case LocalEnrollmentPurpose.OwnerRotation: enrollment.PrepareOfflineOwnerRotation(ticketId, verifier, expires); break;
                    case LocalEnrollmentPurpose.OwnerRehome: enrollment.PrepareOfflineOwnerRehome(ticketId, recipient.Value, verifier, expires); break;
                }
                handoffCreated = false; // committed; intended client owns durable completion
                await output.WriteLineAsync(JsonSerializer.Serialize(new { hostId, ticketId, purpose = command.Kind, intendedSid = recipient.Value, expires })).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret); if (verifierKey is not null) CryptographicOperations.ZeroMemory(verifierKey);
                if (handoffCreated) handoff.DeletePrepared(ticketId, recipient);
            }
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never copy input payloads, exception messages or private data to diagnostics.
            await error.WriteLineAsync("Offline command refused or failed: " + ex.GetType().Name + ".").ConfigureAwait(false);
            return 1;
        }
    }
}
