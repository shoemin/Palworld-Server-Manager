using System.Globalization;
using Google.Protobuf;
using Grpc.Core;
using PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Host;

public sealed partial class LocalSecurityRpcService
{
    public override Task<LocalPeerReplacementPageReply> ReadPeerReplacements(LocalPeerReplacementPageRequest request,ServerCallContext context)
        =>DispatchTrust(context,call=>
        {
            if(request.Limit>16)throw new ArgumentException();
            var page=call.ReadPeerReplacements(request.AfterReplacementId.Length==0?null:Id(request.AfterReplacementId),request.Limit==0?16:(int)request.Limit);
            var reply=new LocalPeerReplacementPageReply {AuthoritativeHostId=page.HostId.ToString("D"),AuthorizationRevision=page.Revision,
                NextAfterReplacementId=page.NextAfterReplacement?.ToString("D")??""};
            foreach(var candidate in page.Candidates)reply.Candidates.Add(new LocalPeerReplacementItem {
                ReplacementId=candidate.ReplacementId.ToString("D"),PeerHostId=candidate.PeerHostId.ToString("D"),
                ExpectedState=candidate.ExpectedState switch {"PeerBound"=>LocalPeerTrustState.PeerBound,"Active"=>LocalPeerTrustState.Active,"Revoked"=>LocalPeerTrustState.Revoked,_=>throw new InvalidOperationException()},
                ExpectedFingerprint=candidate.ExpectedFingerprint??"",ProposedFingerprint=candidate.ProposedFingerprint,
                VerifiedUtc=candidate.VerifiedUtc.ToString("O",CultureInfo.InvariantCulture),ExpiresUtc=candidate.ExpiresUtc.ToString("O",CultureInfo.InvariantCulture)});
            if(reply.CalculateSize()>MaximumMessageBytes)throw new InvalidOperationException();return reply;
        },FeatureCapability.LocalOwnerPeerReplacement);
    public override Task<LocalApprovePeerReplacementReply> ApprovePeerReplacement(LocalApprovePeerReplacementRequest request,ServerCallContext context)
        =>DispatchTrust(context,call=>
        {
            if(request.ExpectedAuthorizationRevision<0)throw new ArgumentException();
            var result=call.ApprovePeerReplacement(request.ExpectedAuthorizationRevision,Id(request.ReplacementId));
            return new LocalApprovePeerReplacementReply {PeerHostId=result.PeerHostId.ToString("D"),ReplacementId=result.ReplacementId.ToString("D"),
                AuthorizationRevision=result.Revision,Incarnation=result.Incarnation,Changed=result.Changed,
                InvalidatedGrants=result.InvalidatedGrants,CreatedDefaultGrants=result.CreatedDefaultGrants};
        },FeatureCapability.LocalOwnerPeerReplacement);
}
