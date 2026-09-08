using System.Security.Authentication;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

internal static partial class PeerTrustRevocationTests
{
    private static void PermitRemote(Rig r,Guid? target=null)
        =>r.Repo.IssueHost(r.Owner,r.Revision,Guid.NewGuid(),ActorRef.RemoteManager(r.F.PeerId),
            HostCapability.ManageTrustedManagers,target??r.F.HostId,Use,null);
    private static long OtherIncarnation(Rig r)=>HostDatabase.QueryScalarLong(r.F.Writer,
        $"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{r.Other:D}';");
    private static PeerTrustRevocationResult RevokeOther(Rig r,PeerGrantMutationActor? actor=null)
        =>r.Repo.RevokeRemotePeerTrust(actor??r.Peer,r.Revision,r.Other,OtherIncarnation(r));

    public static Task RemoteAdministratorTargetsOnlyItsSelectedPeer()
    {
        using var r=new Rig();PermitRemote(r);var original=r.F.Repository.Read(r.F.PeerId);var result=RevokeOther(r);
        Check(result.Changed&&result.InvalidatedGrants==4&&result.InvalidatedReplacements==1);
        Check(r.F.Repository.Read(r.Other)!.State=="Revoked"&&r.F.Repository.Read(r.F.PeerId)==original);
        Check(r.Repo.Read().Policy.CanUseHost(ActorRef.RemoteManager(r.F.PeerId),HostCapability.ManageTrustedManagers,r.F.HostId));
        var snapshot=r.Snapshot();Check(!RevokeOther(r).Changed&&r.Snapshot()==snapshot);
        using var cmd=r.F.Writer.CreateCommand();cmd.CommandText="SELECT ActorKind,ActorLocalPrincipalId,ActorPeerHostId,AffectedHostId,Summary FROM AuditEvents WHERE EventKind='PeerTrustRevoked';";
        using var reader=cmd.ExecuteReader();Check(reader.Read()&&reader.GetString(0)=="RemoteManager"&&reader.IsDBNull(1)&&reader.GetString(2)==r.F.PeerId.ToString("D")&&reader.GetString(3)==r.F.HostId.ToString("D"));
        Check(reader.GetString(4).Contains(r.Other.ToString("D"))&&!reader.GetString(4).Contains(new string('B',64))&&!reader.Read());
        return Task.CompletedTask;
    }
    public static Task RemoteSelfRevocationCannotReuseItsOldProof()
    {
        using var r=new Rig();PermitRemote(r);var proof=r.Peer;var result=r.Repo.RevokeRemotePeerTrust(proof,r.Revision,r.F.PeerId,r.Incarnation);
        Check(result.Changed&&result.InvalidatedGrants==6&&result.Incarnation>proof.Incarnation&&r.F.Repository.Read(r.F.PeerId)!.State=="Revoked");
        Check(r.Repo.Read().Policy.IsOwner(ActorRef.LocalPrincipal(r.F.OwnerId)));
        var snapshot=r.Snapshot();Reject<AuthenticationException>(()=>r.Repo.RevokeRemotePeerTrust(proof,r.Revision,r.F.PeerId,r.Incarnation));Check(r.Snapshot()==snapshot);
        // Artificial later restoration does not make this old authenticated session current.
        r.F.Execute($"UPDATE TrustedManagers SET State='Active',CurrentTrustedPublicKeyFingerprint='{new string('B',64)}' WHERE PeerHostId='{r.F.PeerId:D}';");
        snapshot=r.Snapshot();Reject<AuthenticationException>(()=>RevokeOther(r,proof));Check(r.Snapshot()==snapshot);
        return Task.CompletedTask;
    }
    public static Task RemoteProofAndExactCapabilityRefusals()
    {
        using(var r=new Rig())
        {
            var revision=r.Revision;Reject<UnauthorizedAccessException>(()=>RevokeOther(r));Check(r.Revision==revision);
            PermitRemote(r,r.Other);Reject<UnauthorizedAccessException>(()=>RevokeOther(r));
            PermitRemote(r);var actor=r.Peer;
            foreach(var bad in new[]{actor with{HostId=r.Other},actor with{PeerHostId=r.Other},actor with{PeerFingerprint=new('F',64)},
                actor with{LocalFingerprint=new('F',64)},actor with{Incarnation=actor.Incarnation+1000}})
            {var snapshot=r.Snapshot();Reject<AuthenticationException>(()=>RevokeOther(r,bad));Check(r.Snapshot()==snapshot);}
            var saved=r.Snapshot();Reject<ArgumentException>(()=>r.Repo.RevokeRemotePeerTrust(actor,r.Revision,r.F.HostId,1));Check(r.Snapshot()==saved);
        }
        foreach(var state in new[]{"State='PeerBound'","State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL","PeerRecoveryRequired=1"})
        {
            using var r=new Rig();PermitRemote(r);
            r.F.Execute($"UPDATE TrustedManagers SET {state} WHERE PeerHostId='{r.F.PeerId:D}';");
            var snapshot=r.Snapshot();Reject<AuthenticationException>(()=>RevokeOther(r));Check(r.Snapshot()==snapshot);
        }
        return Task.CompletedTask;
    }
    public static Task RemoteLateIdentityAndAuditEffectsRollback()
    {
        var faults=new[]{
            "UPDATE TrustedManagers SET CurrentTrustedPublicKeyFingerprint='$pin' WHERE PeerHostId='$actor';",
            "UPDATE TrustedManagers SET PeerRecoveryRequired=1 WHERE PeerHostId='$actor';",
            "UPDATE TrustedManagers SET State='Revoked',CurrentTrustedPublicKeyFingerprint=NULL,RevokedUtc='changed' WHERE PeerHostId='$actor';",
            "DELETE FROM PeerRelationshipIncarnations WHERE PeerHostId='$actor'; INSERT INTO PeerRelationshipIncarnations (PeerHostId) VALUES ('$actor');",
            "UPDATE SecureCredentialReferences SET PublicKeyFingerprint='$pin' WHERE CredentialRef='current';",
            "UPDATE AuditEvents SET ActorPeerHostId='$target' WHERE AuditEventId=NEW.AuditEventId;",
            "UPDATE AuditEvents SET ActorKind='LocalPrincipal',ActorPeerHostId=NULL,ActorLocalPrincipalId='$owner' WHERE AuditEventId=NEW.AuditEventId;",
            "DELETE FROM AuditEvents WHERE AuditEventId=NEW.AuditEventId;"
        };
        foreach(var self in new[]{false,true})foreach(var fault in faults)
        {
            using var r=new Rig();PermitRemote(r);var target=self?r.F.PeerId:r.Other;
            // The actor-as-target case must still reject every changed expected tombstone.
            var sql=fault.Replace("$actor",r.F.PeerId.ToString("D")).Replace("$target",r.Other.ToString("D"))
                .Replace("$owner",r.F.OwnerId.ToString("D")).Replace("$pin",new string('F',64));
            r.F.Execute($"CREATE TRIGGER RemoteRevokeFault AFTER INSERT ON AuditEvents WHEN NEW.EventKind='PeerTrustRevoked' BEGIN {sql} END;");
            var snapshot=r.Snapshot();var refused=false;
            try{r.Repo.RevokeRemotePeerTrust(r.Peer,r.Revision,target,self?r.Incarnation:OtherIncarnation(r));}
            catch(Exception e)when(e is AuthenticationException or InvalidOperationException or InvalidDataException or SqliteException or StaleAuthorizationRevisionException){refused=true;}
            if(!refused||r.Snapshot()!=snapshot)throw new Exception("Remote revocation fault did not roll back: "+fault);
        }
        return Task.CompletedTask;
    }
    public static Task RemoteRevocationMayRemoveItsOwnDelegatedCapability()
    {
        using var r=new Rig(false);var root=Guid.NewGuid();
        r.Repo.IssueHost(r.Owner,r.Revision,root,ActorRef.RemoteManager(r.Other),HostCapability.ManageTrustedManagers,r.F.HostId,Onward,null);
        var source=new PeerGrantMutationActor(r.F.HostId,r.Other,new('C',64),new('A',64),OtherIncarnation(r));
        r.Repo.IssueRemoteHost(source,r.Revision,Guid.NewGuid(),ActorRef.RemoteManager(r.F.PeerId),HostCapability.ManageTrustedManagers,r.F.HostId,Use,root);
        var result=RevokeOther(r);Check(result.InvalidatedGrants==2&&r.F.Repository.Read(r.F.PeerId)!.State=="Active");
        Check(!r.Repo.Read().Policy.CanUseHost(ActorRef.RemoteManager(r.F.PeerId),HostCapability.ManageTrustedManagers,r.F.HostId));
        Check(r.Repo.Read().Policy.IsOwner(ActorRef.LocalPrincipal(r.F.OwnerId)));return Task.CompletedTask;
    }
}
