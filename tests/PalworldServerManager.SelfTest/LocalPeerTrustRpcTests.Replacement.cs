using Google.Protobuf;
using Grpc.Core;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host.Persistence;
using ActorRef=PalworldServerManager.Core.Authorization.ActorRef;
using HostCapability=PalworldServerManager.Core.Authorization.HostCapability;
using ServerCapability=PalworldServerManager.Core.Authorization.ServerCapability;
using ServerRef=PalworldServerManager.Core.Authorization.ServerRef;

namespace PalworldServerManager.SelfTest;

internal static partial class LocalPeerTrustRpcTests
{
    private static Task<LocalPeerReplacementPageReply> Replacements(LocalSecurityRpcTests.Client c,string after="",uint limit=0)
        =>c.Call<LocalPeerReplacementPageRequest,LocalPeerReplacementPageReply>("ReadPeerReplacements",new(){AfterReplacementId=after,Limit=limit});
    private static Task<LocalApprovePeerReplacementReply> Approve(LocalSecurityRpcTests.Client c,Guid replacement,long revision)
        =>c.Call<LocalApprovePeerReplacementRequest,LocalApprovePeerReplacementReply>("ApprovePeerReplacement",new(){ReplacementId=replacement.ToString("D"),ExpectedAuthorizationRevision=revision});
    private static Task<LocalHandshakeReply> ReplacementHello(LocalSecurityRpcTests.Client c)
        =>c.Call<Handshake,LocalHandshakeReply>("Negotiate",new(){Protocol=new(){Major=1,Minor=0},Capabilities={FeatureCapability.LocalPrincipalSecurity,FeatureCapability.LocalOwnerPeerReplacement}});
    private static async Task<LocalSecurityRpcTests.Client> ReplacementConnect(Rig r)
    {
        var c=r.F.Connect();try{await ReplacementHello(c);await c.Authenticate(r.F.State.HostId,r.Principal,r.Principal==r.F.State.Owner?r.F.State.OwnerKey:r.F.State.UserKey);return c;}
        catch{c.Dispose();throw;}
    }
    // Explicit verified-binding repository fixture. Native PAKE is separately qualified.
    private static async Task<Guid> Candidate(Rig r,string? pin=null)
        =>new PeerTrustRepository(r.F.State.Database,r.F.State.HostId,r.F.State.Time)
            .RecordOwnerVerifiedBinding(r.Owner,r.Peer,pin??new string('D',64),(await r.F.Trust().ReadAsync()).CurrentFingerprint).ReplacementId!.Value;
    public static async Task ReplacementFeatureAndIdentityBoundaries()
    {
        await ProtocolTests.SchemaEvolution();await using var r=new Rig();await r.Start();var id=await Candidate(r);
        using(var trustOnly=await r.Connect())
        {await Refused(Replacements(trustOnly),StatusCode.FailedPrecondition);await Refused(Approve(trustOnly,id,r.Revision),StatusCode.FailedPrecondition);}
        using(var anonymous=r.F.Connect())
        {
            await Refused(Replacements(anonymous),StatusCode.FailedPrecondition);var hello=await ReplacementHello(anonymous);
            Check(hello.Handshake.Protocol.Minor==0&&hello.Handshake.Capabilities.Contains(FeatureCapability.LocalOwnerPeerReplacement));
            await Refused(Replacements(anonymous),StatusCode.Unauthenticated);await Refused(Approve(anonymous,id,r.Revision),StatusCode.Unauthenticated);
        }
        using var c=await ReplacementConnect(r);Check((await Replacements(c)).Candidates.Single().ReplacementId==id.ToString("D"));
        Check((int)FeatureCapability.LocalOwnerPeerReplacement==18);
        var fields=LocalSecurityReflection.Descriptor.Services.Single().Methods.Single(m=>m.Name=="ApprovePeerReplacement").InputType.Fields.InDeclarationOrder();
        Check(fields.Select(f=>f.Name).SequenceEqual(new[]{"replacement_id","expected_authorization_revision"}));
        await using var f=new LocalSecurityRpcTests.Fixture();await f.Start();using var uninitialized=f.Connect();await ReplacementHello(uninitialized);await Refused(Replacements(uninitialized),StatusCode.Unauthenticated);
    }
    public static async Task ReplacementRequiresActualOwnerNotCapabilities()
    {
        await using var r=new Rig(true);await r.Start();var id=await Candidate(r);using var c=await ReplacementConnect(r);
        await Refused(Replacements(c),StatusCode.PermissionDenied);await Refused(Approve(c,id,r.Revision),StatusCode.PermissionDenied);
        r.Grant(r.Principal);r.Grant(r.Principal,HostCapability.ManagePermissions);
        await Refused(Replacements(c),StatusCode.PermissionDenied);await Refused(Approve(c,id,r.Revision),StatusCode.PermissionDenied);
        Check(r.Count("SELECT COUNT(*) FROM PeerReplacementCompletions;")==0&&r.Count("SELECT COUNT(*) FROM PendingCredentialReplacements WHERE ApprovedUtc IS NOT NULL;")==0);
    }
    public static async Task ReplacementRechecksOwnerAndVerifier()
    {
        foreach(var kind in new[]{"key","native","owner"})
        {
            await using var r=new Rig();var successor=kind=="owner"?r.F.State.Enroll("replacement-owner"):Guid.Empty;
            await r.Start();var id=await Candidate(r);using var c=await ReplacementConnect(r);await Replacements(c);
            if(kind=="owner")
            {
                r.F.State.Sql($"UPDATE LocalPrincipals SET IsOwner=0 WHERE LocalPrincipalId='{r.Principal:D}'; UPDATE LocalPrincipals SET IsOwner=1 WHERE LocalPrincipalId='{successor:D}';");
            }
            else r.F.State.Sql(kind=="key"?$"UPDATE LocalPrincipals SET PublicVerificationKey='{Convert.ToBase64String(r.F.State.UserKey.PublicKey)}' WHERE IsOwner=1;":"UPDATE LocalPrincipals SET OsPrincipalRef='different-native' WHERE IsOwner=1;");
            var expected=kind=="owner"?StatusCode.PermissionDenied:StatusCode.Unauthenticated;
            await Refused(Replacements(c),expected);await Refused(Approve(c,id,r.Revision),expected);
            Check(r.Count("SELECT COUNT(*) FROM PeerReplacementCompletions;")==0);
        }
    }
    public static async Task ReplacementAppliesFreshDefaultsAndPreservesOtherPeer()
    {
        await using var r=new Rig();await r.Start();var sibling=await Candidate(r,new string('E',64));var id=await Candidate(r);
        var old=Guid.NewGuid();var retained=Guid.NewGuid();var target=new ServerRef(r.F.State.HostId,Guid.NewGuid());
        r.Repo.IssueHost(r.Owner,r.Revision,old,ActorRef.RemoteManager(r.Peer),HostCapability.ManageHostSettings,r.F.State.HostId,new(false,false),null);
        r.Repo.IssueServer(r.Owner,r.Revision,retained,ActorRef.RemoteManager(r.Other),ServerCapability.ViewServer,target,new(false,false),null);
        r.Repo.ConfigureDefaults(r.Owner,r.Revision,new([new(HostCapability.CreateServer,new(false,false))],[]));
        using var c=await ReplacementConnect(r);var page=await Replacements(c);var audit=r.Audits;var result=await Approve(c,id,page.AuthorizationRevision);
        Check(result.Changed&&result.ReplacementId==id.ToString("D")&&result.PeerHostId==r.Peer.ToString("D")&&result.AuthorizationRevision==r.Revision&&result.InvalidatedGrants==1&&result.CreatedDefaultGrants==1&&r.Audits==audit+2);
        Check(r.Count("SELECT COUNT(*) FROM AuditEvents WHERE EventKind='PeerCredentialReplacementApproved';")==1&&r.Count("SELECT COUNT(*) FROM AuditEvents WHERE EventKind='DefaultGrantApplied';")==1);
        var snapshot=r.Repo.Read();Check(snapshot.HostGrants.Single(g=>g.GrantId==old).InvalidatedUtc is not null&&snapshot.ServerGrants.Single(g=>g.GrantId==retained).InvalidatedUtc is null);
        var fresh=snapshot.HostGrants.Single(g=>g.GrantId!=old);
        Check(fresh.GrantedByActor==ActorRef.LocalPrincipal(r.Principal)&&fresh.DerivedFromGrantId is null&&fresh.GranteeActor==ActorRef.RemoteManager(r.Peer)&&fresh.Capability==HostCapability.CreateServer);
        Check((await Replacements(c)).Candidates.Count==0&&r.Count("SELECT COUNT(*) FROM LocalPrincipals WHERE IsOwner=1 AND State='Active';")==1);
        await Refused(Approve(c,sibling,r.Revision),StatusCode.FailedPrecondition);
        Check(!(await Approve(c,id,r.Revision)).Changed&&r.Audits==audit+2);
    }
    public static async Task ReplacementRejectsChangedCandidateEvidence()
    {
        foreach(var kind in new[]{"expired","staged","revoked","evidence"})
        {
            await using var r=new Rig();await r.Start();var id=await Candidate(r);using var c=await ReplacementConnect(r);await Replacements(c);
            if(kind=="expired")r.F.State.Time.Now+=TimeSpan.FromMinutes(31);
            if(kind=="staged")r.F.State.Sql($"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{new string('F',64)}',PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{r.F.State.Time.Now.AddMinutes(5):O}' WHERE PeerHostId='{r.Peer:D}';");
            if(kind=="revoked"){var peer=r.Repo.ReadLocalPeerTrust(r.Owner,null,16).Peers.Single(p=>p.PeerHostId==r.Peer);r.Repo.RevokeLocalPeerTrust(r.Owner,r.Revision,r.Peer,peer.Incarnation);}
            if(kind=="evidence")r.F.State.Sql("DELETE FROM PeerReplacementBindingEvidence;");
            var before=r.Repo.ReadLocalPeerTrust(r.Owner,null,16);var audit=r.Audits;
            await Refused(Approve(c,id,r.Revision),StatusCode.FailedPrecondition);
            var after=r.Repo.ReadLocalPeerTrust(r.Owner,null,16);Check(before.Revision==after.Revision&&before.Peers.SequenceEqual(after.Peers)&&r.Audits==audit&&r.Count("SELECT COUNT(*) FROM PeerReplacementCompletions;")==0);
        }
    }
    public static async Task ReplacementPagesBoundedAndNeverApprovalPermits()
    {
        await using var r=new Rig();await r.Start();for(var i=1;i<=35;i++)await Candidate(r,i.ToString("X64"));using var c=await ReplacementConnect(r);
        var audit=r.Audits;var seen=new HashSet<string>();var cursor="";
        do
        {
            var page=await Replacements(c,cursor);Check(page.Candidates.Count<=16&&page.CalculateSize()<=16*1024&&page.AuthoritativeHostId==r.F.State.HostId.ToString("D"));
            foreach(var item in page.Candidates)Check(seen.Add(item.ReplacementId)&&item.PeerHostId==r.Peer.ToString("D")&&item.ExpectedState==LocalPeerTrustState.Active&&item.ExpectedFingerprint==new string('B',64)&&item.ProposedFingerprint.Length==64&&DateTimeOffset.Parse(item.ExpiresUtc)==DateTimeOffset.Parse(item.VerifiedUtc).AddMinutes(30));
            cursor=page.NextAfterReplacementId;
        }while(cursor!="");
        Check(seen.Count==35&&r.Audits==audit);await Refused(Replacements(c,limit:17),StatusCode.InvalidArgument);await Refused(Replacements(c,"invalid"),StatusCode.InvalidArgument);await Refused(Replacements(c,Guid.Empty.ToString("D")),StatusCode.InvalidArgument);
        var observed=await Replacements(c);r.F.State.Time.Now+=TimeSpan.FromMinutes(31);
        Check((await Replacements(c)).Candidates.Count==16);await Refused(Approve(c,Guid.Parse(observed.Candidates[0].ReplacementId),observed.AuthorizationRevision),StatusCode.FailedPrecondition);
    }
    public static async Task ReplacementStaleMalformedAndAuditRollback()
    {
        await using var r=new Rig();await r.Start();var id=await Candidate(r);using var c=await ReplacementConnect(r);var revision=r.Revision;
        r.Add(Guid.NewGuid());await Refused(Approve(c,id,revision),StatusCode.Aborted);
        await Refused(Approve(c,id,-1),StatusCode.InvalidArgument);await Refused(Approve(c,Guid.Empty,r.Revision),StatusCode.InvalidArgument);await Refused(Approve(c,Guid.NewGuid(),r.Revision),StatusCode.FailedPrecondition);
        var before=await Replacements(c);var audit=r.Audits;
        r.F.State.Sql("CREATE TRIGGER replacement_rpc_failure BEFORE INSERT ON AuditEvents BEGIN SELECT RAISE(ABORT,'secret-replacement-canary'); END;");
        try{await Approve(c,id,r.Revision);throw new Exception("Approval audit failure ignored.");}
        catch(RpcException ex){Check(ex.StatusCode==StatusCode.Internal&&ex.Status.Detail=="Local trust request failed.");}
        Check((await Replacements(c)).Equals(before)&&r.Audits==audit&&r.Count("SELECT COUNT(*) FROM PeerReplacementCompletions;")==0);
        r.F.State.Sql("DROP TRIGGER replacement_rpc_failure;");Check((await Approve(c,id,r.Revision)).Changed);
    }
    public static async Task ReplacementConcurrentApprovalHasOneWinner()
    {
        await using var r=new Rig();await r.Start();var id=await Candidate(r);using var a=await ReplacementConnect(r);using var b=await ReplacementConnect(r);var revision=r.Revision;var audit=r.Audits;
        async Task<bool> Attempt(LocalSecurityRpcTests.Client c){try{return(await Approve(c,id,revision)).Changed;}catch(RpcException ex){Check(ex.StatusCode==StatusCode.Aborted);return false;}}
        var results=await Task.WhenAll(Attempt(a),Attempt(b));Check(results.Count(v=>v)==1&&r.Audits==audit+1&&r.Count("SELECT COUNT(*) FROM PeerReplacementCompletions;")==1);
        Check(!(await Approve(a,id,r.Revision)).Changed&&r.Audits==audit+1);
    }
}
