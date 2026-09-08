using PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Contracts;

// Shape and correlation only. Actual TLS keys and durable approval supply authority.
public static class PeerRecoveryOfferWire
{
    private static Guid Id(string value)=>Guid.TryParseExact(value,"D",out var id)&&id!=Guid.Empty
        ?id:throw new ArgumentException("Valid recovery identity required.");
    public static Guid ValidateRequest(PeerRecoveryOfferRequest request)
    { ArgumentNullException.ThrowIfNull(request);return Id(request.OfferingHostId); }
    public static void ValidateOffer(PeerRecoveryOfferReply reply,Guid offeringHost,Guid receivingHost)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if(offeringHost==Guid.Empty||receivingHost==Guid.Empty||offeringHost==receivingHost||Id(reply.OfferingHostId)!=offeringHost)
            throw new ArgumentException("Recovery offer Host mismatch.");
        if(reply.Acknowledgment is { } ack&&PeerRecoveryCompletionWire.ValidateRequest(ack).ReceivingHost!=receivingHost)
            throw new ArgumentException("Recovery offer recipient mismatch.");
        // A well-formed but different fingerprint goes through canonical receipt mismatch auditing.
    }
    public static Guid ValidatePositiveReceipt(PeerRecoveryCompletionReply receipt,Guid offeringHost,Guid receivingHost,string receivingFingerprint)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var ack=new PeerRecoveryCompletionRequest {ReceivingHostId=receivingHost.ToString("D"),ApprovalId=receipt.ApprovalId,
            AcknowledgedFingerprint=receivingFingerprint};
        PeerRecoveryCompletionWire.ValidateReply(receipt,ack,offeringHost);
        if(receipt.Result is not (PeerRecoveryCompletionResult.Recorded or PeerRecoveryCompletionResult.AlreadyRecorded))
            throw new ArgumentException("Positive recovery receipt required.");
        return Id(receipt.ApprovalId);
    }
    public static void ValidateConfirmation(PeerRecoveryOfferConfirmationReply reply,PeerRecoveryCompletionReply receipt)
    {
        ArgumentNullException.ThrowIfNull(reply);ArgumentNullException.ThrowIfNull(receipt);
        ValidatePositiveReceipt(receipt,Id(receipt.ApprovingHostId),Id(receipt.ReceivingHostId),receipt.AcknowledgedFingerprint);
        if(reply.Receipt is null||!reply.Receipt.Equals(receipt)||
            reply.Result is not (PeerRecoveryOfferConfirmationResult.Confirmed or PeerRecoveryOfferConfirmationResult.AlreadyConfirmed))
            throw new ArgumentException("Recovery confirmation mismatch.");
    }
}
