using Grpc.Core;
using PalworldServerManager.Contracts.Wire;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using RawClient = PalworldServerManager.SelfTest.PeerSecurityRpcTests.RawClient;

namespace PalworldServerManager.SelfTest;

internal static partial class PeerRotationReceiptRpcTests
{
    public static async Task NegotiatedRelationshipCannotSurviveIdenticalStateAba()
    {
        await using var f=new Rotation();await f.Start();
        // Establish real New-key observation before constructing the authenticated receipt.
        using var connection=new RawClient(f.B,f.A,f.NextPin);
        await connection.Negotiate(PeerSecurityRpcRuntime.Hello(f.B.State.HostId));
        f.B.Runtime.Repository.ObserveActivePeerCredential(f.A.State.HostId,f.NextPin);
        var pending=f.B.Runtime.Repository.ReadPendingPeerRotationReceipt(f.A.State.HostId,f.NextPin,f.B.Pin)!;
        var request=PeerRotationReceiptWire.Wire(pending);
        var before=f.A.State.Repository.Read(f.B.State.HostId);
        f.A.State.Execute("UPDATE TrustedManagers SET PeerRecoveryRequired=1; UPDATE TrustedManagers SET PeerRecoveryRequired=0;");
        Check(f.A.State.Repository.Read(f.B.State.HostId)==before);
        await Refused(connection.Rpc.ConfirmRotationPromotionAsync(request,deadline:DateTime.UtcNow.AddSeconds(5)).ResponseAsync,StatusCode.Unauthenticated);
        Check(f.A.State.Count("HostRotationPromotionEvidence")==0 && f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId==pending.RotationId);
        Check(await f.Client.ConfirmAsync(f.A.State.HostId,f.A.Address)==PeerRotationReceiptExchange.Confirmed);
        Check(Count(f.A,"SELECT COUNT(*) FROM HostRotationPromotionEvidence e JOIN PeerRelationshipIncarnations i ON e.PeerHostId=i.PeerHostId AND e.Incarnation=i.Incarnation;")==1);
        Check(f.B.Runtime.Repository.Read(f.A.State.HostId)!.PendingRotationId is null);f.Preserved();
    }
}
