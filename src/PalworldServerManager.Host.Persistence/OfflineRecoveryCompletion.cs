using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PalworldServerManager.Host.Persistence;

public sealed partial class HostCredentialStateRepository
{
    // Private expected effects of this one existing offline transaction. Raw row snapshots
    // preserve historical metadata fixtures; they are never ordinary authorization policy.
    private sealed class OfflineRecoveryExpectation
    {
        private static readonly string[] Names=["HostIdentity","SecureCredentialReferences","HostCredentialRotations",
            "LocalPrincipals","TrustedManagers","TrustedManagerPairings","PeerRelationshipIncarnations",
            "PendingCredentialReplacements","PeerReplacementBindingEvidence","PeerLocalBindingEvidence",
            "PeerReplacementCompletions","PeerRecoveryCompletionReceipts","PeerUnpairReceipts",
            "HostCapabilityGrants","ServerCapabilityGrants","AuthorizationRevision",
            "DefaultGrantTemplateState","HostDefaultGrants","ServerDefaultGrants"];
        private readonly Dictionary<string,RecoveryTable> tables;
        private readonly Dictionary<string,(long Incarnation,bool Advances)> peers=new(StringComparer.Ordinal);
        private readonly HashSet<string> carry=new(StringComparer.Ordinal);
        internal OfflineRecoveryExpectation(SqliteConnection c,SqliteTransaction tx,string reference,string stamp)
        {
            tables=Names.ToDictionary(name=>name,name=>RecoveryTable.Read(c,tx,name));
            var trust=tables["TrustedManagers"];var incarnations=tables["PeerRelationshipIncarnations"];
            var markers=tables["PeerReplacementCompletions"];
            foreach(var (peer,row) in trust.Rows)
            {
                if(trust.Get(row,"State") is not ("Active" or "PeerBound"))continue;
                var source=Convert.ToInt64(incarnations.Get(incarnations.Rows[peer],"Incarnation"));
                peers.Add(peer,(source,Equals(trust.Get(row,"PeerRecoveryRequired"),0L)));
                if(Equals(trust.Get(row,"State"),"Active")&&markers.Rows.TryGetValue(peer,out var marker)&&
                    markers.Get(marker,"InvalidatedUtc") is DBNull&&Equals(markers.Get(marker,"CurrentIncarnation"),source)&&
                    Equals(markers.Get(marker,"ApprovedPeerFingerprint"),trust.Get(row,"CurrentTrustedPublicKeyFingerprint")))carry.Add(peer);
                trust.Set(row,"PeerRecoveryRequired",1L);
            }
            var identity=tables["HostIdentity"];identity.Set(identity.Rows["1"],"CurrentCredentialRef",reference);
            var credentials=tables["SecureCredentialReferences"];credentials.Set(credentials.Rows[reference],"ActivatedUtc",stamp);
            var rotations=tables["HostCredentialRotations"];
            foreach(var row in rotations.Rows.Values)
                if(rotations.Get(row,"State") is "Prepared" or "Staging" or "ReadyForCutover" or "CutOver")
                {rotations.Set(row,"State","Aborted");rotations.Set(row,"CompletedUtc",stamp);rotations.Set(row,"RetirementAuthorized",0L);}
            var candidates=tables["PendingCredentialReplacements"];
            foreach(var row in candidates.Rows.Values)
                if(candidates.Get(row,"InvalidatedUtc") is DBNull)candidates.Set(row,"InvalidatedUtc",stamp);
            var revision=tables["AuthorizationRevision"];var value=Convert.ToInt64(revision.Get(revision.Rows["1"],"Revision"));
            // Existing triggers count every eligible trust UPDATE, even recovery1->1.
            revision.Set(revision.Rows["1"],"Revision",checked(value+1+peers.Count));
        }
        internal void Carry(SqliteConnection c,SqliteTransaction tx)
        {
            var actual=RecoveryTable.Read(c,tx,"PeerRelationshipIncarnations");
            var expected=tables["PeerRelationshipIncarnations"];var markers=tables["PeerReplacementCompletions"];
            foreach(var (peer,prior) in peers)
            {
                if(!actual.Rows.TryGetValue(peer,out var row))throw Changed();
                var current=Convert.ToInt64(actual.Get(row,"Incarnation"));
                if(prior.Advances?current<=prior.Incarnation:current!=prior.Incarnation)throw Changed();
                expected.Rows[peer]=row;
                if(prior.Advances)
                {tables["PeerUnpairReceipts"].Rows.Remove(peer);tables["PeerRecoveryCompletionReceipts"].Rows.Remove(peer);}
                if(!carry.Contains(peer))continue;
                Execute(c,tx,"UPDATE PeerReplacementCompletions SET CurrentIncarnation=$inc WHERE PeerHostId=$peer;",("$inc",current),("$peer",peer));
                markers.Set(markers.Rows[peer],"CurrentIncarnation",current);
            }
        }
        internal void Validate(SqliteConnection c,SqliteTransaction tx)
        {
            foreach(var (name,expected) in tables)
            {
                var actual=RecoveryTable.Read(c,tx,name);
                if(!expected.Columns.SequenceEqual(actual.Columns)||expected.Rows.Count!=actual.Rows.Count||
                    expected.Rows.Any(p=>!actual.Rows.TryGetValue(p.Key,out var row)||!p.Value.SequenceEqual(row)))throw Changed();
            }
        }
        private static InvalidOperationException Changed()=>new("Offline recovery effects changed before commit.");
    }
    private sealed record RecoveryTable(string[] Columns,Dictionary<string,object[]> Rows)
    {
        private int Column(string name)
        {var index=Array.IndexOf(Columns,name);return index>=0?index:throw new InvalidDataException("Offline recovery schema unavailable.");}
        internal object Get(object[] row,string name)=>row[Column(name)];
        internal void Set(object[] row,string name,object value)=>row[Column(name)]=value;
        internal static RecoveryTable Read(SqliteConnection c,SqliteTransaction tx,string table)
        {
            // Table names are exclusively the fixed owned-table list above, never input.
            using var cmd=Command(c,tx,$"SELECT * FROM {table};");using var reader=cmd.ExecuteReader();
            var names=Enumerable.Range(0,reader.FieldCount).Select(reader.GetName).ToArray();
            var keys=table switch
            {
                "PeerRelationshipIncarnations"=>new[]{reader.GetOrdinal("PeerHostId")},
                "ServerDefaultGrants"=>new[]{reader.GetOrdinal("AuthoritativeHostId"),reader.GetOrdinal("ServerProfileId"),reader.GetOrdinal("Capability")},
                _=>new[]{0}
            };
            var rows=new Dictionary<string,object[]>(StringComparer.Ordinal);
            while(reader.Read())
            {
                var values=new object[reader.FieldCount];reader.GetValues(values);
                rows.Add(string.Join("|",keys.Select(i=>Convert.ToString(values[i],CultureInfo.InvariantCulture))),values);
            }
            return new(names,rows);
        }
    }
    private Action WriteOfflineRecoveryAudit(SqliteConnection c,SqliteTransaction tx,string kind,string stamp)
    {
        var id=Guid.NewGuid().ToString("D");var host=_hostId.ToString("D");
        var args=new (string,object?)[]{("$id",id),("$now",stamp),("$kind",kind),("$host",host)};
        Execute(c,tx,"""
            INSERT INTO AuditEvents (AuditEventId,OccurredUtc,EventKind,ActorKind,AffectedHostId,IsOfflineRecovery,Summary)
            VALUES ($id,$now,$kind,'OfflineRecovery',$host,1,'Machine identity credential metadata changed.');
            """,args);
        return ()=>
        {
            using var cmd=Command(c,tx,"""
                SELECT COUNT(*) FROM AuditEvents WHERE AuditEventId=$id AND OccurredUtc=$now AND EventKind=$kind
                    AND ActorKind='OfflineRecovery' AND ActorLocalPrincipalId IS NULL AND ActorPeerHostId IS NULL
                    AND AffectedHostId=$host AND AffectedServerProfileId IS NULL AND IsOfflineRecovery=1
                    AND Summary='Machine identity credential metadata changed.';
                """,args);
            if(Convert.ToInt32(cmd.ExecuteScalar())!=1)throw new InvalidOperationException("Offline recovery audit changed before commit.");
        };
    }
}
