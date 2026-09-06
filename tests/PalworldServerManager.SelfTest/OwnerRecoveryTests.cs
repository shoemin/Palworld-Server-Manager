using System.Security.Authentication;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Client.Platform.Windows;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using static PalworldServerManager.SelfTest.LocalEnrollmentTests;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static class OwnerRecoveryTests
{
    private static void Denied(Action action)
    { try { action(); throw new Exception("Expected recovery rejection."); } catch (AuthenticationException) { } }
    private static void AuditFailure(Action action)
    { try { action(); throw new Exception("Expected transaction rollback."); } catch (SqliteException) { } }
    private static LocalEnrollmentVerifier Proof(Fixture f, Guid id, bool rehome = false, byte[]? secret = null) =>
        LocalEnrollmentVerifier.Compute(f.Key, f.HostId, rehome ? LocalEnrollmentPurpose.OwnerRehome : LocalEnrollmentPurpose.OwnerRotation, id, secret ?? [4, 7, 2, 9]);
    private static Guid Rotation(Fixture f)
    { var id = Guid.NewGuid(); using var proof = Proof(f, id); f.Repository.PrepareOfflineOwnerRotation(id, proof, f.Time.Now.AddMinutes(15)); return id; }
    private static Guid Rehome(Fixture f, string native)
    { var id = Guid.NewGuid(); using var proof = Proof(f, id, true); f.Repository.PrepareOfflineOwnerRehome(id, native, proof, f.Time.Now.AddMinutes(15)); return id; }

    public static async Task RotationAndRetry()
    {
        using var f = new Fixture(); using var previousConnection = f.Authenticate(f.Owner, "owner", f.OwnerKey);
        var id = Rotation(f); using var proof = Proof(f, id); using var wrong = Proof(f, id, secret: [0]);
        Denied(() => f.Repository.CompleteOwnerRotation(id, "other", proof, Public(f.UserKey)));
        Denied(() => f.Repository.CompleteOwnerRotation(id, "owner", wrong, Public(f.UserKey)));
        Denied(() => f.Repository.CompleteOwnerRotation(id, "owner", proof, "not-a-public-key"));
        Denied(() => f.Repository.CompleteOwnerRotation(id, "owner", proof, Public(f.OwnerKey)));
        var before = f.Count("SELECT COUNT(*) FROM AuditEvents;");
        f.Sql("CREATE TRIGGER fail_audit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        AuditFailure(() => f.Repository.CompleteOwnerRotation(id, "owner", proof, Public(f.UserKey)));
        Check(f.Text("SELECT PublicVerificationKey FROM LocalPrincipals WHERE IsOwner=1;") == Public(f.OwnerKey) &&
            f.Count("SELECT COUNT(*) FROM PendingOwnerCredentialRotations WHERE ConsumedUtc IS NOT NULL;") == 0, "Rotation audit failure changed key/ticket.");
        f.Sql("DROP TRIGGER fail_audit;");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => f.Repository.CompleteOwnerRotation(id, "owner", proof, Public(f.UserKey)))));
        Check(results.All(x => x == f.Owner) && HostIdentityRepository.CountActiveOwners(f.Writer) == 1, "Rotation changed Owner identity/cardinality.");
        Denied(() => previousConnection.GetCurrentPrincipal());
        using var recovered = f.Authenticate(f.Owner, "owner", f.UserKey);
        Check(recovered.GetCurrentPrincipal().IsOwner, "Rotated client key could not authenticate Owner.");
        f.Time.Now = f.Time.Now.AddDays(2);
        Check(f.Repository.CompleteOwnerRotation(id, "owner", proof, "ignored-on-retry") == f.Owner && f.Count("SELECT COUNT(*) FROM AuditEvents;") == before + 1,
            "Consumed rotation retry expired or repeated effects/audit.");
        Denied(() => f.Repository.CompleteOwnerRotation(id, "owner", wrong, Public(f.OwnerKey)));
        var expired = Rotation(f); using var ep = Proof(f, expired); f.Time.Now = f.Time.Now.AddMinutes(15);
        Denied(() => f.Repository.CompleteOwnerRotation(expired, "owner", ep, Public(f.OwnerKey)));
        using var noOwner = new Fixture(false); var ticket = Guid.NewGuid(); using var np = Proof(noOwner, ticket);
        Denied(() => noOwner.Repository.PrepareOfflineOwnerRotation(ticket, np, noOwner.Time.Now.AddMinutes(15)));
    }

    public static Task RehomeTargetsAndGrantForest()
    {
        foreach (var targetState in new[] { "new", "active", "revoked" })
        {
            using var f = new Fixture(); using var prior = f.Authenticate(f.Owner, "owner", f.OwnerKey);
            Guid? target = targetState == "new" ? null : f.Enroll("successor");
            if (targetState == "revoked") f.Repository.RevokePrincipal(f.Actor, target!.Value);
            var independent = f.Enroll("independent");
            foreach (var table in new[] { "HostCapabilityGrants", "ServerCapabilityGrants" })
            {
                var columns = table == "HostCapabilityGrants" ? "TargetHostId" : "AuthoritativeHostId,ServerProfileId";
                var values = table == "HostCapabilityGrants" ? $"'{f.HostId:D}'" : $"'{f.HostId:D}','server'";
                void Grant(string name, Guid grantee, string? parent) => f.Sql($"""
                    INSERT INTO {table} (GrantId,{columns},Capability,GranteeActorKind,GranteeLocalPrincipalId,
                        GrantedByActorKind,GrantedByLocalPrincipalId,CanDelegate,CanDelegateOnwardDelegation,DerivedFromGrantId,CreatedUtc)
                    VALUES ('{name}',{values},'test','LocalPrincipal','{grantee:D}','LocalPrincipal','{f.Owner:D}',1,1,
                        {(parent is null ? "NULL" : "'" + parent + "'")},'test');
                    """);
                Grant("held-by-old-owner", f.Owner, null); Grant("descendant", independent, "held-by-old-owner");
                Grant("independent-owner-issued-root", independent, null);
            }
            var id = Rehome(f, "successor"); using var proof = Proof(f, id, true); using var wrong = Proof(f, id, true, [0]);
            Denied(() => f.Repository.CompleteOwnerRehome(id, "successor", wrong, Public(f.UserKey)));
            Denied(() => f.Repository.CompleteOwnerRehome(id, "owner", proof, Public(f.UserKey)));
            Denied(() => f.Repository.CompleteOwnerRehome(id, "successor", proof, "invalid"));
            if (targetState == "active") Denied(() => f.Repository.CompleteOwnerRehome(id, "successor", proof, Public(f.OwnerKey)));
            var audit = f.Count("SELECT COUNT(*) FROM AuditEvents;");
            f.Sql("CREATE TRIGGER fail_audit BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
            AuditFailure(() => f.Repository.CompleteOwnerRehome(id, "successor", proof, Public(f.UserKey)));
            Check(f.Text("SELECT LocalPrincipalId FROM LocalPrincipals WHERE IsOwner=1;") == f.Owner.ToString("D") &&
                f.Count("SELECT COUNT(*) FROM PendingOwnerRehomes WHERE ConsumedUtc IS NOT NULL;") == 0 &&
                f.Count("SELECT COUNT(*) FROM HostCapabilityGrants WHERE InvalidatedUtc IS NOT NULL;") == 0, "Failed re-home audit left partial Owner/cascade/consumption.");
            f.Sql("DROP TRIGGER fail_audit;");
            var result = f.Repository.CompleteOwnerRehome(id, "successor", proof, Public(f.UserKey));
            Check((target is null || result == target) && result != f.Owner && HostIdentityRepository.CountActiveOwners(f.Writer) == 1,
                "Re-home duplicated or lost Owner identity.");
            Check(f.Count($"SELECT COUNT(*) FROM LocalPrincipals WHERE LocalPrincipalId='{f.Owner:D}' AND IsOwner=0 AND State='Revoked' AND PublicVerificationKey IS NULL;") == 1,
                "Prior Owner was not fully tombstoned.");
            Denied(() => prior.GetCurrentPrincipal()); using var next = f.Authenticate(result, "successor", f.UserKey);
            Check(next.GetCurrentPrincipal().IsOwner, "Re-homed key did not authenticate as Owner.");
            foreach (var table in new[] { "HostCapabilityGrants", "ServerCapabilityGrants" })
                Check(f.Count($"SELECT COUNT(*) FROM {table} WHERE InvalidatedUtc IS NOT NULL;") == 2 &&
                    f.Count($"SELECT COUNT(*) FROM {table} WHERE GrantId='independent-owner-issued-root' AND InvalidatedUtc IS NULL;") == 1,
                    "Owner re-home cleared independent issued roots or missed held-grant descendants.");
            f.Time.Now = f.Time.Now.AddDays(2);
            Check(f.Repository.CompleteOwnerRehome(id, "successor", proof, "ignored-on-retry") == result && f.Count("SELECT COUNT(*) FROM AuditEvents;") == audit + 1,
                "Re-home retry expired or repeated effects/audit.");
            Denied(() => f.Repository.CompleteOwnerRehome(id, "successor", wrong, Public(f.UserKey)));
        }
        return Task.CompletedTask;
    }

    public static async Task StaleSnapshotsAndAba()
    {
        using var f = new Fixture(); var staleRotation = Rotation(f); var staleRehome = Rehome(f, "successor");
        using var sr = Proof(f, staleRotation); using var sh = Proof(f, staleRehome, true);
        var first = Rotation(f); using var fp = Proof(f, first); f.Repository.CompleteOwnerRotation(first, "owner", fp, Public(f.UserKey));
        var back = Rotation(f); using var bp = Proof(f, back); f.Repository.CompleteOwnerRotation(back, "owner", bp, Public(f.OwnerKey));
        // The tuple equals the original again, but both older tickets were permanently invalidated.
        Denied(() => f.Repository.CompleteOwnerRotation(staleRotation, "owner", sr, Public(f.UserKey)));
        Denied(() => f.Repository.CompleteOwnerRehome(staleRehome, "successor", sh, Public(f.UserKey)));
        Check(f.Repository.CompleteOwnerRotation(first, "owner", fp, Public(f.UserKey)) == f.Owner &&
            f.Text("SELECT PublicVerificationKey FROM LocalPrincipals WHERE IsOwner=1;") == Public(f.OwnerKey), "Consumed old rotation overwrote a later recovery.");
        var target = f.Enroll("target"); var beforeRevocation = Rehome(f, "target"); using var br = Proof(f, beforeRevocation, true);
        f.Repository.RevokePrincipal(f.Actor, target);
        Denied(() => f.Repository.CompleteOwnerRehome(beforeRevocation, "target", br, Public(f.UserKey)));
        var beforeAba = Rehome(f, "target"); using var ba = Proof(f, beforeAba, true);
        Check(f.Enroll("target") == target, "Fixture reactivation changed identity."); f.Repository.RevokePrincipal(f.Actor, target);
        Denied(() => f.Repository.CompleteOwnerRehome(beforeAba, "target", ba, Public(f.UserKey)));
        var missingTarget = Rehome(f, "new-target"); using var mt = Proof(f, missingTarget, true);
        f.Enroll("new-target"); Denied(() => f.Repository.CompleteOwnerRehome(missingTarget, "new-target", mt, Public(f.UserKey)));
        var x = Rehome(f, "X"); var y = Rehome(f, "Y"); using var xp = Proof(f, x, true); using var yp = Proof(f, y, true);
        var result = f.Repository.CompleteOwnerRehome(y, "Y", yp, Public(f.UserKey));
        Denied(() => f.Repository.CompleteOwnerRehome(x, "X", xp, Public(f.OwnerKey)));
        Check(f.Text("SELECT LocalPrincipalId FROM LocalPrincipals WHERE IsOwner=1;") == result.ToString("D"), "Stale re-home displaced newer Owner.");
        // A consumed result remains available even after another recovery displaces its recipient.
        var z = Rehome(f, "Z"); using var zp = Proof(f, z, true); var newest = f.Repository.CompleteOwnerRehome(z, "Z", zp, Public(f.OwnerKey));
        Check(f.Repository.CompleteOwnerRehome(y, "Y", yp, "ignored") == result &&
            f.Text("SELECT LocalPrincipalId FROM LocalPrincipals WHERE IsOwner=1;") == newest.ToString("D"), "Old consumed re-home reinstalled prior Owner.");
        var same = Guid.NewGuid(); using var sp = Proof(f, same, true);
        Denied(() => f.Repository.PrepareOfflineOwnerRehome(same, "Z", sp, f.Time.Now.AddMinutes(15)));
        var exp = Rehome(f, "expired"); using var ep = Proof(f, exp, true); f.Time.Now = f.Time.Now.AddMinutes(15);
        Denied(() => f.Repository.CompleteOwnerRehome(exp, "expired", ep, Public(f.UserKey)));
        using var concurrent = new Fixture(); var left = Rehome(concurrent, "left"); var right = Rehome(concurrent, "right");
        using var lp = Proof(concurrent, left, true); using var rp = Proof(concurrent, right, true);
        var wins = 0;
        await Task.WhenAll(new[] { (left, "left", lp), (right, "right", rp) }.Select(attempt => Task.Run(() =>
        {
            try { concurrent.Repository.CompleteOwnerRehome(attempt.Item1, attempt.Item2, attempt.Item3, Public(concurrent.UserKey)); Interlocked.Increment(ref wins); }
            catch (AuthenticationException) { }
        })));
        Check(wins == 1 && HostIdentityRepository.CountActiveOwners(concurrent.Writer) == 1 &&
            concurrent.Count("SELECT COUNT(*) FROM PendingOwnerRehomes WHERE ConsumedUtc IS NOT NULL;") == 1,
            "Concurrent different re-homes both completed or lost Owner cardinality.");
    }

    public static async Task OnlineCompletionBoundary()
    {
        using var f = new Fixture(); var id = Rotation(f); byte[] secret = [4, 7, 2, 9];
        Check(await f.Service.CompleteOwnerRotationAsync(id, "owner", secret, Public(f.UserKey)) == f.Owner, "Online rotation failed without old client key.");
        Check(f.Secrets.Writes == 0 && f.Secrets.LastRead!.All(b => b == 0), "Recovery completion wrote/retained verifier key.");
        var home = Rehome(f, "replacement");
        var result = await f.Service.CompleteOwnerRehomeAsync(home, "replacement", secret, Public(f.OwnerKey));
        using var auth = f.Authenticate(result, "replacement", f.OwnerKey); Check(auth.GetCurrentPrincipal().IsOwner, "Online re-home did not bind new Owner.");
        var rawVerifier = f.Text("SELECT SecretVerifier FROM PendingOwnerRehomes LIMIT 1;");
        var audit = f.Text("SELECT group_concat(Summary || EventKind,' ') FROM AuditEvents;");
        Check(!audit.Contains(rawVerifier) && !audit.Contains(Convert.ToBase64String(secret)), "Recovery audit exposed bearer/verifier.");
        Check(f.Count("SELECT COUNT(*) FROM AuditEvents WHERE EventKind IN ('OwnerCredentialRotationPrepared','OwnerRehomePrepared') AND IsOfflineRecovery=1 AND ActorKind='OfflineRecovery';") == 2,
            "Recovery preparation was not distinctly audited.");
        Check(f.Count("SELECT COUNT(*) FROM AuditEvents WHERE EventKind IN ('OwnerCredentialRotationCompleted','OwnerRehomeCompleted') AND IsOfflineRecovery=0 AND ActorKind='LocalPrincipal';") == 2,
            "Recovery completion was not distinctly audited.");
    }
}
