using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Cli;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static partial class WindowsIntegration
{
    private static OfflineHostLocation OfflineLocation(string service, string group, string root) =>
        new(service, group, Path.Combine(root, "OfflineHost"), Path.Combine(root, "OfflinePublic"), Path.Combine(root, "OfflineHandoffs"), @"Global\" + service);
    private static async Task OfflineDeniedProbe(string service, string group, string root)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var output = new StringWriter(); using var error = new StringWriter();
        Check(await OfflineHostCli.RunAsync(["bootstrap", "--owner-sid", identity.User!.Value], OfflineLocation(service, group, root), output, error) == 1,
            "Non-admin entered production offline composition.");
        Check(output.ToString().Length == 0 && error.ToString().Contains("UnauthorizedAccessException"), "Non-admin refusal did not occur at privilege gate.");
    }
    private static async Task OfflineSuite(WindowsHostPlatform servicePlatform, string service, string group, string root, string executable,
        string userA, string userB, string password, string sidA, string sidB, string shared, SecurityIdentifier serviceSid)
    {
        var location = OfflineLocation(service, group, root);
        new DirectoryInfo(location.HostRoot).Create(WindowsHostPlatform.BuildHostDirectoryAcl(serviceSid));
        var database = new HostDatabase(new HostDataRoot(location.HostRoot));
        async Task<string> Run(int expected, params string[] command)
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            var result = await OfflineHostCli.RunAsync(command, location, output, error);
            Check(result == expected, "Offline command result disagreed: " + command[0] + "; " + error);
            return output.ToString();
        }
        RunUser(executable, userA, password, "offline-denied", service, group, root, shared);
        Check(!File.Exists(database.DatabasePath), "Non-admin refusal opened Host state.");
        await Run(1, "bootstrap", "--owner-sid", "S-1-5-32-545");
        await Run(1, "recover-machine", "--reason", "unknown");
        Check(!File.Exists(database.DatabasePath), "Invalid target or command wrote Host state.");
        await servicePlatform.StartAsync(); await NativeTlsServiceFixture.WaitReady(Path.Combine(root, "Host"));
        await Run(1, "bootstrap", "--owner-sid", sidA);
        Check(!File.Exists(database.DatabasePath), "Running-service refusal opened Host state.");
        await servicePlatform.StopAsync();
        using (var contender = HostExclusivityLock.TryAcquire(TimeSpan.Zero, location.MutexName))
        {
            Check(contender is not null, "Could not hold offline contention fixture lease.");
            await Run(1, "bootstrap", "--owner-sid", sidA);
            Check(!File.Exists(database.DatabasePath), "Held-lease refusal opened Host state.");
        }
        var bootstrap = await Run(0, "bootstrap", "--owner-sid", sidA);
        using var result = JsonDocument.Parse(bootstrap); var hostId = result.RootElement.GetProperty("hostId").GetGuid();
        var firstTicket = result.RootElement.GetProperty("ticketId").GetGuid();
        RunUser(executable, userA, password, "handoff-prepared", hostId.ToString("D") + ":" + firstTicket.ToString("D"),
            Path.Combine(location.HandoffRoot, sidA, firstTicket.ToString("N") + ".bin"), location.HandoffRoot, shared);
        var state = new HostCredentialStateRepository(database, hostId);
        using var connection = database.OpenConnection();
        try
        {
            Check(HostIdentityRepository.CountActiveOwners(connection) == 0 && !state.Read().Initialized, "Offline bootstrap prematurely created Owner authority.");
            var databaseFile = new FileInfo(database.DatabasePath); var savedDatabaseAcl = databaseFile.GetAccessControl().GetSecurityDescriptorBinaryForm();
            var beforeUnsafeAttempt = state.Read().CurrentReference;
            var unsafeDatabaseAcl = databaseFile.GetAccessControl();
            unsafeDatabaseAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            databaseFile.SetAccessControl(unsafeDatabaseAcl);
            await Run(1, "recover-machine", "--reason", "loss");
            Check(state.Read().CurrentReference == beforeUnsafeAttempt, "Unsafe database ACL refusal changed machine authority.");
            var restoredDatabaseAcl = new FileSecurity(); restoredDatabaseAcl.SetSecurityDescriptorBinaryForm(savedDatabaseAcl, AccessControlSections.Access);
            databaseFile.SetAccessControl(restoredDatabaseAcl);
            var count = Directory.GetFiles(location.HandoffRoot, "*.bin", SearchOption.AllDirectories).Length;
            await Run(1, "bootstrap", "--owner-sid", sidB);
            Check(Directory.GetFiles(location.HandoffRoot, "*.bin", SearchOption.AllDirectories).Length == count &&
                HostDatabase.QueryScalarText(connection, "SELECT OsPrincipalRef FROM PendingOwnerEnrollments;") == sidA, "Live bootstrap retargeted or failed handoff was left behind.");
            // Deliberate fixture-only initialization; final online bootstrap belongs to 42d3.
            using var ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256); var ownerId = Guid.NewGuid();
            using (var transaction = connection.BeginTransaction())
            {
                new HostIdentityRepository(database).InitializeWithOwner(connection, transaction, ownerId.ToString("D"), sidA,
                    Convert.ToBase64String(ownerKey.ExportSubjectPublicKeyInfo())); transaction.Commit();
            }
            await Run(0, "rotate-owner");
            Check(HostDatabase.QueryScalarText(connection, "SELECT OsPrincipalRef FROM PendingOwnerCredentialRotations;") == sidA, "Rotation handoff targeted someone other than persisted Owner.");
            count = Directory.GetFiles(location.HandoffRoot, "*.bin", SearchOption.AllDirectories).Length;
            HostDatabase.Execute(connection, "CREATE TRIGGER fail_offline_audit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT,'fixture'); END;");
            await Run(1, "rehome-owner", "--owner-sid", sidB);
            Check(Directory.GetFiles(location.HandoffRoot, "*.bin", SearchOption.AllDirectories).Length == count &&
                HostDatabase.QueryScalarLong(connection, "SELECT COUNT(*) FROM PendingOwnerRehomes;") == 0, "Failed ticket audit left a handoff or authority row.");
            HostDatabase.Execute(connection, "DROP TRIGGER fail_offline_audit;");
            var rehome = await Run(0, "rehome-owner", "--owner-sid", sidB); using var rehomeResult = JsonDocument.Parse(rehome);
            var rehomeTicket = rehomeResult.RootElement.GetProperty("ticketId").GetGuid();
            RunUser(executable, userB, password, "handoff-prepared", hostId.ToString("D") + ":" + rehomeTicket.ToString("D"),
                Path.Combine(location.HandoffRoot, sidB, rehomeTicket.ToString("N") + ".bin"), location.HandoffRoot, shared);
            var store = new WindowsSecureCredentialStore(location.HostRoot, serviceSid); var cache = new WindowsHostTlsCredentialCache(hostId, serviceSid, store);
            var hmac = await store.RetrieveAsync(LocalEnrollmentVerifier.KeyName(hostId)) ?? throw new Exception("Missing HMAC fixture key.");
            try
            {
                foreach (var reason in new[] { "loss", "compromise", "legacy-loss" })
                {
                    var old = state.Read().CurrentReference!; string nativeName;
                    using (var certificate = await cache.LoadAsync(old)) using (var key = (ECDsaCng)certificate.GetECDsaPrivateKey()!) nativeName = key.Key.KeyName!;
                    if (reason != "compromise") await store.DeleteAsync(old);
                    if (reason == "legacy-loss") HostDatabase.Execute(connection, "UPDATE SecureCredentialReferences SET PublicKeyFingerprint=NULL WHERE CredentialRef=(SELECT CurrentCredentialRef FROM HostIdentity);");
                    await Run(0, "recover-machine", "--reason", reason == "compromise" ? "compromise" : "loss");
                    Check(state.Read().CurrentReference != old && await store.RetrieveAsync(old) is null && !CngKey.Exists(nativeName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey),
                        "Machine recovery retained old private/native material.");
                    var retainedHmac = await store.RetrieveAsync(LocalEnrollmentVerifier.KeyName(hostId));
                    try { Check(retainedHmac is not null && hmac.SequenceEqual(retainedHmac), "Machine recovery replaced independent HMAC key."); }
                    finally { if (retainedHmac is not null) CryptographicOperations.ZeroMemory(retainedHmac); }
                    var anchor = await new WindowsLocalHostTrustReader(location.PublicTrustRoot, serviceSid).ReadAsync();
                    Check(anchor.HostId == hostId && anchor.CurrentFingerprint == HostTrustPlanning.Build(state.Read()).Publication!.CurrentFingerprint && anchor.PendingFingerprint is null,
                        "Recovery publication did not match authoritative current metadata.");
                }
                await new WindowsLocalHostTrustPublisher(location.PublicTrustRoot, serviceSid).PublishAsync(new(hostId, new string('A', 64)));
                await Run(0, "rotate-owner");
                Check((await new WindowsLocalHostTrustReader(location.PublicTrustRoot, serviceSid).ReadAsync()).CurrentFingerprint == HostTrustPlanning.Build(state.Read()).Publication!.CurrentFingerprint,
                    "Unrelated offline invocation failed to reconcile stale publication at startup.");
                Check(HostIdentityRepository.CountActiveOwners(connection) == 1 && HostDatabase.QueryScalarText(connection, "SELECT LocalPrincipalId FROM LocalPrincipals WHERE IsOwner=1;") == ownerId.ToString("D"),
                    "Offline preparation or machine recovery changed Owner authority.");
            }
            finally { CryptographicOperations.ZeroMemory(hmac); await cache.ReconcileAsync([]); }
            Console.WriteLine("PASS integration: production offline command handler, elevation/running-service/lease denial, protected ticket preparation and rollback, lost/compromised/legacy machine recovery and startup reconciliation");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection); }
    }
}
