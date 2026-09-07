using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Host.Persistence.Migrations;

namespace PalworldServerManager.SelfTest;

internal static class RotationPeerSetTests
{
    private static readonly string Local = new('A', 64), Peer = new('B', 64), Next = new('C', 64);
    private static void Check(bool value) { if (!value) throw new Exception("Rotation peer-set assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected peer-set refusal: " + typeof(T).Name); }
    private static HostCredentialStateRepository State(PeerTrustTests.Fixture f) => new(f.Database, f.HostId);
    private static LocalPrincipalMutationActor Owner(PeerTrustTests.Fixture f) => new(f.HostId, f.OwnerId, "native-owner", "fixture-public");
    private static Guid Proposal(PeerTrustTests.Fixture f)
    {
        var state = State(f); var prepared = state.PrepareRoutineRotation(Owner(f), Guid.NewGuid());
        state.RecordCreated(prepared.NewReference, Next); state.BeginRoutineRotationStaging(Owner(f), prepared.RotationId);
        state.PrepareRoutineRotationProposal(Owner(f), prepared.RotationId); return prepared.RotationId;
    }
    private static void Bind(PeerTrustTests.Fixture f)
    { f.Repository.RecordVerifiedBinding(f.PeerId, Peer, Local); }
    private static long Revision(PeerTrustTests.Fixture f) => HostDatabase.QueryScalarLong(f.Writer, "SELECT Revision FROM PeerTrustRevision WHERE Id=1;");
    public static Task UpgradePreservesRowsAndDoesNotInventEvidence()
    {
        using var f = new PeerTrustTests.Fixture(schemaVersion: 4); Bind(f); var id = Proposal(f);
        var before = f.Repository.Read(f.PeerId); var audits = f.Count("AuditEvents");
        Check(HostSchemaMigrationRunner.Default().Migrate(f.Writer) == 1 && HostSchemaMigrationRunner.Default().Migrate(f.Writer) == 0);
        Check(f.Repository.Read(f.PeerId) == before && f.Count("AuditEvents") == audits && Revision(f) == 0);
        Check(HostDatabase.QueryScalarLong(f.Writer, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE '%_Revision_%';") == 6);
        var snapshot = State(f).ReadRoutineRotationPeerSet(id);
        Check(snapshot.Revision == 0 && snapshot.Peers.Single().State == "PeerBound" && snapshot.Peers.Single().LocalBoundFingerprint == Local);
        Check(f.Count("HostCredentialRotationPeers") == 0 && f.Count("HostCapabilityGrants") == 0 && f.Count("ServerCapabilityGrants") == 0);
        return Task.CompletedTask;
    }
    public static Task TransactionRollbackAndIdenticalStateAba()
    {
        using var f = new PeerTrustTests.Fixture(); Bind(f); f.Execute("UPDATE TrustedManagers SET State='Active';"); var id = Proposal(f);
        var before = State(f).ReadRoutineRotationPeerSet(id); var audits = f.Count("AuditEvents");
        using (var tx = f.Writer.BeginTransaction())
        {
            HostDatabase.Execute(f.Writer, "UPDATE TrustedManagers SET PeerRecoveryRequired=1;", tx);
            using var read = f.Writer.CreateCommand(); read.Transaction = tx; read.CommandText = "SELECT Revision FROM PeerTrustRevision;";
            Check((long)read.ExecuteScalar()! == before.Revision + 1); tx.Rollback();
        }
        Check(Revision(f) == before.Revision && State(f).ReadRoutineRotationPeerSet(id).Peers.SequenceEqual(before.Peers));
        f.Execute($"UPDATE TrustedManagers SET State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL; UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{Peer}';");
        var after = State(f).ReadRoutineRotationPeerSet(id);
        Check(after.Revision == before.Revision + 2 && after.Peers.SequenceEqual(before.Peers));
        Check(before.Revision != after.Revision && f.Count("AuditEvents") == audits && f.Count("HostCredentialRotationPeers") == 0);
        Reject<NotSupportedException>(() => ((IList<RoutineRotationPeer>)after.Peers).Clear());
        return Task.CompletedTask;
    }
    public static async Task PairingWritesAndConcurrentMembershipAreTracked()
    {
        using var f = new PeerTrustTests.Fixture(); Bind(f); f.Execute("UPDATE TrustedManagers SET State='Active';"); var id = Proposal(f);
        var before = Revision(f);
        f.Execute("UPDATE TrustedManagerPairings SET LocalBoundPublicKeyFingerprint=NULL; DELETE FROM TrustedManagerPairings;");
        Check(Revision(f) == before + 2 && State(f).ReadRoutineRotationPeerSet(id).Peers.Single().LocalBoundFingerprint is null);
        f.Execute($"INSERT INTO TrustedManagerPairings (PeerHostId,LocalBoundPublicKeyFingerprint,BoundUtc,ExpiresUtc) VALUES ('{f.PeerId:D}','{Local}','{f.Time.Now:O}','{f.Time.Now.AddMinutes(30):O}');");
        Check(Revision(f) == before + 3); before = Revision(f);
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        await Task.WhenAll(ids.Select(peer => Task.Run(() => f.Repository.RecordVerifiedBinding(peer, Peer, Local))));
        var snapshot = State(f).ReadRoutineRotationPeerSet(id);
        Check(snapshot.Revision == before + 16 && snapshot.Peers.Count == 9 && snapshot.Peers.Count(p => p.State == "PeerBound") == 8);
        f.Execute($"DELETE FROM TrustedManagerPairings WHERE PeerHostId='{ids[0]:D}'; DELETE FROM TrustedManagers WHERE PeerHostId='{ids[0]:D}';");
        Check(Revision(f) == snapshot.Revision + 2 && State(f).ReadRoutineRotationPeerSet(id).Peers.Count == 8);
        Check(f.Count("HostCredentialRotationPeers") == 0 && f.Count("HostCapabilityGrants") == 0);
    }
    public static Task MissingAndExhaustedRevisionFailClosed()
    {
        using var f = new PeerTrustTests.Fixture(); Bind(f); var id = Proposal(f);
        f.Execute("UPDATE PeerTrustRevision SET Revision=9223372036854775807;");
        Reject<SqliteException>(() => f.Execute("UPDATE TrustedManagers SET State='Active';"));
        Check(Revision(f) == long.MaxValue && f.Repository.Read(f.PeerId)!.State == "PeerBound");
        Reject<SqliteException>(() => f.Execute("UPDATE PeerTrustRevision SET Revision=-1;"));
        Reject<SqliteException>(() => f.Execute("UPDATE PeerTrustRevision SET Revision=1.5;"));
        f.Execute("DELETE FROM PeerTrustRevision;");
        Reject<InvalidDataException>(() => State(f).ReadRoutineRotationPeerSet(id));
        Reject<SqliteException>(() => f.Repository.RecordVerifiedBinding(Guid.NewGuid(), Peer, Local));
        Check(f.Count("TrustedManagers") == 1 && f.Count("TrustedManagerPairings") == 1 && f.Count("HostCredentialRotationPeers") == 0);
        return Task.CompletedTask;
    }
    public static Task PendingRecoveryAndMalformedSnapshotGates()
    {
        using var f = new PeerTrustTests.Fixture(); Bind(f); var id = Proposal(f);
        f.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=1; UPDATE TrustedManagerPairings SET LocalBoundPublicKeyFingerprint=NULL;");
        f.Time.Now += TimeSpan.FromHours(1);
        var pending = State(f).ReadRoutineRotationPeerSet(id);
        Check(pending.Peers.Single().State == "PeerBound" && pending.Peers.Single().RecoveryRequired && pending.Peers.Single().LocalBoundFingerprint is null);
        Check(pending.Peers.Single().PairingExpiresUtc < f.Time.Now); // Read does not silently exclude an expired-but-unresolved row.
        f.Repository.MaintainPendingPairingTrust();
        var expired = State(f).ReadRoutineRotationPeerSet(id); Check(expired.Revision > pending.Revision && expired.Peers.Count == 0);
        f.Execute("UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='bad';");
        Reject<InvalidDataException>(() => State(f).ReadRoutineRotationPeerSet(id));
        f.Execute($"UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='{Peer}';");
        Check(State(f).ReadRoutineRotationPeerSet(id).Peers.Single().RecoveryRequired == false);
        State(f).AbortRoutineRotation(Owner(f), id);
        Reject<AuthenticationException>(() => State(f).ReadRoutineRotationPeerSet(id));
        Check(f.Count("HostCredentialRotationPeers") == 0 && f.Count("HostCapabilityGrants") == 0 && f.Count("ServerCapabilityGrants") == 0);
        return Task.CompletedTask;
    }
    public static Task RealObserverAuditRollbackAndHostScope()
    {
        using var f = new PeerTrustTests.Fixture(); Bind(f); f.Execute("UPDATE TrustedManagers SET State='Active';");
        Reject<AuthenticationException>(() => State(f).ReadRoutineRotationPeerSet(Guid.NewGuid()));
        var state = State(f); var rotation = state.PrepareRoutineRotation(Owner(f), Guid.NewGuid());
        state.RecordCreated(rotation.NewReference, Next); state.BeginRoutineRotationStaging(Owner(f), rotation.RotationId);
        Reject<InvalidOperationException>(() => state.ReadRoutineRotationPeerSet(rotation.RotationId));
        state.PrepareRoutineRotationProposal(Owner(f), rotation.RotationId);
        f.Execute($"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{Next}',PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{f.Time.Now.AddMinutes(30):O}';");
        var before = state.ReadRoutineRotationPeerSet(rotation.RotationId);
        f.Execute("CREATE TRIGGER FailObserver BEFORE INSERT ON AuditEvents WHEN NEW.EventKind='PeerCredentialPromoted' BEGIN SELECT RAISE(ABORT,'fixture observer audit failure'); END;");
        Reject<SqliteException>(() => f.Repository.ObserveActivePeerCredential(f.PeerId, Next));
        Check(Revision(f) == before.Revision && f.Repository.Read(f.PeerId)!.CurrentFingerprint == Peer);
        f.Execute("DROP TRIGGER FailObserver;"); f.Repository.ObserveActivePeerCredential(f.PeerId, Next);
        Check(Revision(f) == before.Revision + 1 && state.ReadRoutineRotationPeerSet(rotation.RotationId).Peers.Single().CurrentFingerprint == Next);
        f.Execute("UPDATE LocalPrincipals SET State='Revoked',PublicVerificationKey=NULL WHERE IsOwner=1;");
        Reject<InvalidDataException>(() => state.ReadRoutineRotationPeerSet(rotation.RotationId));
        f.Execute("UPDATE LocalPrincipals SET State='Active',PublicVerificationKey='fixture-public' WHERE IsOwner=1;");
        f.Execute($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{f.HostId:D}','Active','{Peer}','fixture');");
        Reject<InvalidDataException>(() => state.ReadRoutineRotationPeerSet(rotation.RotationId));
        Check(f.Count("HostCredentialRotationPeers") == 0 && f.Count("HostCapabilityGrants") == 0);
        return Task.CompletedTask;
    }
}
