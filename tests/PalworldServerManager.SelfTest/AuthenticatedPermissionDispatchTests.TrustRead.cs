using System.Security.Authentication;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;

namespace PalworldServerManager.SelfTest;

internal static partial class AuthenticatedPermissionDispatchTests
{
    public static async Task ReplacementRequiresOwnedSessionAndFeature()
    {
        await using var r=new Rig();var context=await r.LocalContext(true);
        await RejectAsync<InvalidOperationException>(()=>r.Local.Permissions.Invoke(context,c=>c.ReadPeerReplacements(null,16),default,FeatureCapability.LocalOwnerPeerReplacement));
        var hello=new Handshake{Protocol=new(){Major=1,Minor=0},Capabilities={FeatureCapability.LocalPrincipalSecurity,FeatureCapability.LocalOwnerPeerReplacement}};
        var connection=context.Features.Get<LocalSecurityRpcConnection>()!;
        await connection.Invoke("owner",s=>{s.Protocol=NegotiatedProtocol.Negotiate(hello,hello);return Task.FromResult(true);},default);
        Check((await r.Local.Permissions.Invoke(context,c=>c.ReadPeerReplacements(null,16),default,FeatureCapability.LocalOwnerPeerReplacement)).Candidates.Count==0);
        await RejectAsync<AuthenticationException>(()=>r.NewLocal().Permissions.Invoke(context,c=>c.ReadPeerReplacements(null,16),default,FeatureCapability.LocalOwnerPeerReplacement));
        context.Request.Scheme="http";await RejectAsync<AuthenticationException>(()=>r.Local.Permissions.Invoke(context,c=>c.ReadPeerReplacements(null,16),default,FeatureCapability.LocalOwnerPeerReplacement));
    }
    public static async Task ReplacementCancellationAndFacadeLifetime()
    {
        await using var r=new Rig();var context=await r.LocalContext(true);LocalPermissionCall? retained=null;var audit=r.Count("AuditEvents");
        await r.Local.Permissions.Invoke(context,c=>{retained=c;return c.ReadPeerReplacements(null,16);},default);
        Reject<ObjectDisposedException>(()=>retained!.ReadPeerReplacements(null,16));Reject<ObjectDisposedException>(()=>retained!.ApprovePeerReplacement(0,Guid.NewGuid()));
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await RejectAsync<OperationCanceledException>(()=>r.Local.Permissions.Invoke(context,c=>c.ReadPeerReplacements(null,16),canceled.Token));
        context.RequestAborted=canceled.Token;await RejectAsync<OperationCanceledException>(()=>r.Local.Permissions.Invoke(context,c=>c.ApprovePeerReplacement(0,Guid.NewGuid()),default));
        Check(r.Count("AuditEvents")==audit);Reject<ArgumentException>(()=>r.Repo.ReadLocalPeerReplacements(r.F.Actor,null,17));Reject<ArgumentException>(()=>r.Repo.ReadLocalPeerReplacements(r.F.Actor,Guid.Empty,1));
    }
    public static async Task TrustReadRequiresOwnedSessionAndFeature()
    {
        await using var r=new Rig();var context=await r.LocalContext(true);
        await RejectAsync<InvalidOperationException>(()=>r.Local.Permissions.Invoke(context,c=>c.ReadPeerTrust(null,16),default,FeatureCapability.LocalPeerTrustManagement));
        var hello=new Handshake{Protocol=new(){Major=1,Minor=0},Capabilities={FeatureCapability.LocalPrincipalSecurity,FeatureCapability.LocalPeerTrustManagement}};
        var connection=context.Features.Get<LocalSecurityRpcConnection>()!;
        await connection.Invoke("owner",s=>{s.Protocol=NegotiatedProtocol.Negotiate(hello,hello);return Task.FromResult(true);},default);
        Check((await r.Local.Permissions.Invoke(context,c=>c.ReadPeerTrust(null,16),default,FeatureCapability.LocalPeerTrustManagement)).Peers.Single().PeerHostId==r.PeerId);
        await RejectAsync<AuthenticationException>(()=>r.NewLocal().Permissions.Invoke(context,c=>c.ReadPeerTrust(null,16),default,FeatureCapability.LocalPeerTrustManagement));
        context.Request.Scheme="http";await RejectAsync<AuthenticationException>(()=>r.Local.Permissions.Invoke(context,c=>c.ReadPeerTrust(null,16),default,FeatureCapability.LocalPeerTrustManagement));
        context.Request.Scheme="https";context.Request.Protocol="HTTP/1.1";await RejectAsync<AuthenticationException>(()=>r.Local.Permissions.Invoke(context,c=>c.ReadPeerTrust(null,16),default,FeatureCapability.LocalPeerTrustManagement));
    }
    public static async Task TrustReadCancellationAndFacadeLifetime()
    {
        await using var r=new Rig();var context=await r.LocalContext(true);LocalPermissionCall? retained=null;
        var count=r.Count("AuditEvents");
        await r.Local.Permissions.Invoke(context,c=>{retained=c;return c.ReadPeerTrust(null,16);},default);
        Reject<ObjectDisposedException>(()=>retained!.ReadPeerTrust(null,16));
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await RejectAsync<OperationCanceledException>(()=>r.Local.Permissions.Invoke(context,c=>c.ReadPeerTrust(null,16),canceled.Token));
        context.RequestAborted=canceled.Token;
        await RejectAsync<OperationCanceledException>(()=>r.Local.Permissions.Invoke(context,c=>c.ReadPeerTrust(null,16),default));
        Check(r.Count("AuditEvents")==count);
        Reject<ArgumentException>(()=>r.Repo.ReadLocalPeerTrust(r.F.Actor,null,17));
        Reject<ArgumentException>(()=>r.Repo.ReadLocalPeerTrust(r.F.Actor,Guid.Empty,1));
    }
}
