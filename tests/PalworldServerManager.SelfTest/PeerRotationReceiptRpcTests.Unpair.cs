using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static partial class PeerRotationReceiptRpcTests
{
    public static async Task PreparationFeatureFailureRetainsObservedRotation()
    {
        await using var f=new Rotation();await f.Start();var entered=false;
        var old=f.B.State.Repository.Read(f.A.State.HostId)!;
        Check(old.CurrentFingerprint==f.A.Pin&&old.PendingFingerprint==f.NextPin);
        var legacy=new PeerReplyFaultTransport<PeerHello>(new WindowsPeerHttpTransportFactory(f.B.Certificate.Value),
            "Negotiate",PeerHello.Parser,reply=>reply.Handshake.Capabilities.Remove(FeatureCapability.PeerUnpair));
        try
        {
            await new PeerUnpairConnectionFactory(f.B.Runtime,legacy).WithConnection(f.A.State.HostId,f.A.Address,
                (held,ct)=>{entered=true;return Task.FromResult(true);});
            throw new Exception("Unnegotiated unpair preparation accepted.");
        }
        catch(InvalidOperationException){}
        var observed=f.B.State.Repository.Read(f.A.State.HostId)!;
        Check(!entered&&legacy.Altered==1&&observed.State=="Active"&&observed.CurrentFingerprint==f.NextPin&&
            observed.PendingRotationId==f.Proposal.RotationId&&!f.B.State.Repository.RecognizesTransportFingerprint(f.A.Pin));
        f.Preserved();
        var delivery=await WindowsHostComposition.CreatePeerUnpairConnectionFactory(f.B.Runtime,f.B.Certificate.Value)
            .WithConnection(f.A.State.HostId,f.A.Address,(held,ct)=>held.Send(LiveUnpairConnectionTests.Revoke(f.B,f.A)));
        Check(delivery==PeerUnpairDelivery.Confirmed&&f.A.State.Repository.Read(f.B.State.HostId)!.State=="Revoked"&&
            f.B.State.Repository.Read(f.A.State.HostId)!.State=="Revoked");
    }
}
