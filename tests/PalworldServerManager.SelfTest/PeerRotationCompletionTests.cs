using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class PeerRotationCompletionTests
{
    private static readonly string Old = new('B', 64), New = new('C', 64), Local = new('A', 64);
    private static void Check(bool value) { if (!value) throw new Exception("Peer rotation completion assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected peer rotation refusal."); }
    // Explicit synthetic input from the later stage protocol. No production acceptance bypass.
    private static Guid Stage(PeerTrustTests.Fixture f, string? old = null, string? next = null)
    {
        var rotation = Guid.NewGuid(); f.Repository.RecordVerifiedBinding(f.PeerId, old ?? Old, Local);
        f.Execute($"""
            UPDATE TrustedManagers SET State='Active',PendingTrustedPublicKeyFingerprint='{next ?? New}',
                PendingRotationId='{rotation:D}',PendingRotationExpiresUtc='{f.Time.Now.AddMinutes(1):O}' WHERE PeerHostId='{f.PeerId:D}';
            INSERT INTO HostCapabilityGrants (GrantId,TargetHostId,Capability,GranteeActorKind,GranteePeerHostId,
                GrantedByActorKind,GrantedByLocalPrincipalId,CanDelegate,CanDelegateOnwardDelegation,CreatedUtc)
                VALUES ('retained-custom-grant','{f.HostId:D}','ViewHost','RemoteManager','{f.PeerId:D}',
                    'LocalPrincipal','{f.OwnerId:D}',1,0,'{f.Time.Now:O}');
            """);
        return rotation;
    }
    private static long Count(PeerTrustTests.Fixture f, string where) => HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM AuditEvents WHERE " + where + ";");
    private static void GrantUnchanged(PeerTrustTests.Fixture f)
    {
        Check(f.Count("HostCapabilityGrants") == 1 && f.Count("ServerCapabilityGrants") == 0);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM HostCapabilityGrants WHERE GrantId='retained-custom-grant' AND Capability='ViewHost' AND CanDelegate=1 AND CanDelegateOnwardDelegation=0 AND InvalidatedUtc IS NULL;") == 1);
    }
    public static async Task PromotionReceiptAndConcurrentReplay()
    {
        using var f = new PeerTrustTests.Fixture(); var rotation = Stage(f);
        Reject<AuthenticationException>(() => f.Repository.ConfirmPeerRotationReceipt(f.PeerId, Old, rotation));
        Reject<AuthenticationException>(() => f.Repository.ConfirmPeerRotationReceipt(f.PeerId, New, rotation));
        Check(f.Repository.Read(f.PeerId)!.CurrentFingerprint == Old);
        Check(new PeerTransportAuthentication(f.Repository, f.Time).AdmitHandshake(f.PeerId, New, PeerTrafficPurpose.OrdinaryManagement));
        Check(f.Repository.Read(f.PeerId)!.CurrentFingerprint == Old && f.Count("TrustedManagerCredentialHistory") == 0);
        var observations = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => f.Repository.ObserveActivePeerCredential(f.PeerId, New))));
        Check(observations.Count(o => o.Promoted) == 1 && f.Count("TrustedManagerCredentialHistory") == 1);
        var read = new PeerTrustRepository(f.Database, f.HostId, f.Time).Read(f.PeerId)!;
        Check(read.CurrentFingerprint == New && read.PendingFingerprint is null && read.PendingRotationId == rotation && read.PendingRotationExpiresUtc is null && !read.PendingReconfirmationRequired);
        Check(HostDatabase.QueryScalarLong(f.Writer, $"SELECT COUNT(*) FROM TrustedManagerCredentialHistory WHERE PriorPublicKeyFingerprint='{Old}' AND RotatedUtc='{f.Time.Now:O}';") == 1);
        Check(Count(f, "EventKind='PeerCredentialPromoted' AND ActorKind='RemoteManager' AND ActorPeerHostId IS NOT NULL") == 1);
        Check(!f.Repository.RecognizesTransportFingerprint(Old) && f.Repository.RecognizesTransportFingerprint(New));
        var auth = new PeerTransportAuthentication(f.Repository, f.Time);
        Reject<AuthenticationException>(() => auth.Authenticate(f.PeerId, Old, PeerTrafficPurpose.OrdinaryManagement));
        Check(!auth.Authenticate(f.PeerId, New, PeerTrafficPurpose.OrdinaryManagement).PromotedCredential);
        Reject<AuthenticationException>(() => f.Repository.ConfirmPeerRotationReceipt(f.PeerId, New, Guid.NewGuid()));
        Reject<AuthenticationException>(() => f.Repository.ConfirmPeerRotationReceipt(f.PeerId, Old, rotation));
        Check(f.Repository.ConfirmPeerRotationReceipt(f.PeerId, New, rotation));
        Check(!f.Repository.ConfirmPeerRotationReceipt(f.PeerId, New, rotation) && f.Repository.Read(f.PeerId)!.PendingRotationId is null);
        Check(Count(f, "EventKind='PeerRotationReceiptConfirmed'") == 1); GrantUnchanged(f);
    }
    public static Task LapseAndTransactionalRollback()
    {
        using var f = new PeerTrustTests.Fixture(); var rotation = Stage(f);
        f.Time.Now += TimeSpan.FromSeconds(59); f.Repository.MaintainPendingPairingTrust(); Check(!f.Repository.Read(f.PeerId)!.PendingReconfirmationRequired);
        f.Time.Now += TimeSpan.FromSeconds(1);
        void FailAudit() => f.Execute("CREATE TRIGGER FailRotation BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT,'fixture rotation audit failure'); END;");
        void AllowAudit() => f.Execute("DROP TRIGGER FailRotation;");
        FailAudit(); Reject<SqliteException>(() => f.Repository.MaintainPendingPairingTrust());
        Check(!f.Repository.Read(f.PeerId)!.PendingReconfirmationRequired); AllowAudit();
        f.Repository.MaintainPendingPairingTrust(); f.Repository.MaintainPendingPairingTrust();
        Check(f.Repository.Read(f.PeerId)!.PendingReconfirmationRequired);
        Check(Count(f, "EventKind='PeerRotationReconfirmationRequired' AND ActorKind IS NULL AND ActorPeerHostId IS NULL AND ActorLocalPrincipalId IS NULL") == 1);
        f.Time.Now += TimeSpan.FromDays(4);
        var auth = new PeerTransportAuthentication(f.Repository, f.Time);
        Check(auth.Authenticate(f.PeerId, Old, PeerTrafficPurpose.OrdinaryManagement).TrustState == "Active");
        Check(f.Repository.RecognizesTransportFingerprint(Old) && f.Repository.RecognizesTransportFingerprint(New));
        FailAudit(); Reject<SqliteException>(() => auth.Authenticate(f.PeerId, New, PeerTrafficPurpose.OrdinaryManagement));
        Check(f.Repository.Read(f.PeerId)!.CurrentFingerprint == Old && f.Count("TrustedManagerCredentialHistory") == 0); AllowAudit();
        Check(auth.Authenticate(f.PeerId, New, PeerTrafficPurpose.OrdinaryManagement).PromotedCredential);
        FailAudit(); Reject<SqliteException>(() => f.Repository.ConfirmPeerRotationReceipt(f.PeerId, New, rotation));
        Check(f.Repository.Read(f.PeerId)!.PendingRotationId == rotation); AllowAudit();
        f.Repository.ConfirmPeerRotationReceipt(f.PeerId, New, rotation); GrantUnchanged(f); return Task.CompletedTask;
    }
    public static Task InvalidAndRecoveryStates()
    {
        using var f = new PeerTrustTests.Fixture(); f.Repository.RecordVerifiedBinding(f.PeerId, Old, Local);
        Reject<AuthenticationException>(() => f.Repository.ObserveActivePeerCredential(f.PeerId, Old));
        f.Execute($"UPDATE TrustedManagers SET State='Active',PendingTrustedPublicKeyFingerprint='{New}' WHERE PeerHostId='{f.PeerId:D}';");
        Reject<InvalidDataException>(() => f.Repository.ObserveActivePeerCredential(f.PeerId, New));
        f.Execute($"UPDATE TrustedManagers SET PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{f.Time.Now.AddMinutes(1):O}',PeerRecoveryRequired=1 WHERE PeerHostId='{f.PeerId:D}';");
        Reject<AuthenticationException>(() => f.Repository.ObserveActivePeerCredential(f.PeerId, Old));
        Reject<AuthenticationException>(() => f.Repository.ObserveActivePeerCredential(f.PeerId, New));
        Reject<AuthenticationException>(() => f.Repository.ObserveActivePeerCredential(Guid.NewGuid(), New));
        Reject<AuthenticationException>(() => f.Repository.ObserveActivePeerCredential(f.HostId, New));
        Check(f.Count("TrustedManagerCredentialHistory") == 0 && f.Repository.Read(f.PeerId)!.CurrentFingerprint == Old);
        return Task.CompletedTask;
    }
    public static async Task ActualTlsPresentationPromotes()
    {
        using var f = new PeerTrustTests.Fixture(); using var old = new PeerTlsTests.Certificate(); using var next = new PeerTlsTests.Certificate(); using var client = new PeerTlsTests.Certificate();
        var oldPin = WindowsPeerTls.PublicFingerprint(old.Value); var nextPin = WindowsPeerTls.PublicFingerprint(next.Value); var clientPin = WindowsPeerTls.PublicFingerprint(client.Value);
        var rotation = Stage(f, oldPin, nextPin); f.Time.Now += TimeSpan.FromHours(2); f.Repository.MaintainPendingPairingTrust();
        var auth = new PeerTransportAuthentication(f.Repository, f.Time); var observed = false;
        await PeerTlsTests.Exchange(old.Value, client.Value, p => p == clientPin, f.Repository.RecognizesTransportFingerprint, true,
            observedByClient: p => Check(!auth.Authenticate(f.PeerId, p, PeerTrafficPurpose.OrdinaryManagement).PromotedCredential));
        Check(f.Repository.Read(f.PeerId)!.CurrentFingerprint == oldPin);
        await PeerTlsTests.Exchange(next.Value, client.Value, p => p == clientPin, f.Repository.RecognizesTransportFingerprint, true,
            observedByClient: p => { Check(auth.Authenticate(f.PeerId, p, PeerTrafficPurpose.OrdinaryManagement).PromotedCredential); observed = true; });
        Check(observed && f.Repository.Read(f.PeerId)!.PendingRotationId == rotation);
        await PeerTlsTests.Exchange(old.Value, client.Value, p => p == clientPin, f.Repository.RecognizesTransportFingerprint, false);
        GrantUnchanged(f);
    }
}
