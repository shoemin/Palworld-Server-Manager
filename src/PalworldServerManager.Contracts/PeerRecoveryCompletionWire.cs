using PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Contracts;

// Shape/correlation only, never a replacement for actual authenticated TLS evidence.
public static class PeerRecoveryCompletionWire
{
    private static Guid Id(string value)=>Guid.TryParseExact(value,"D",out var id)&&id!=Guid.Empty
        ?id:throw new ArgumentException("Valid recovery identity required.");
    private static void Fingerprint(string value)
    {
        if(value.Length!=64||value.Any(c=>c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new ArgumentException("Valid recovery key fingerprint required.");
    }
    public static (Guid ReceivingHost,Guid ApprovalId) ValidateRequest(PeerRecoveryCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);Fingerprint(request.AcknowledgedFingerprint);
        return (Id(request.ReceivingHostId),Id(request.ApprovalId));
    }
    public static void ValidateReply(PeerRecoveryCompletionReply reply,PeerRecoveryCompletionRequest request,Guid approvingHost)
    {
        ArgumentNullException.ThrowIfNull(reply);var expected=ValidateRequest(request);
        Fingerprint(reply.AcknowledgedFingerprint);
        if(approvingHost==Guid.Empty||approvingHost==expected.ReceivingHost||Id(reply.ReceivingHostId)!=expected.ReceivingHost||
            Id(reply.ApprovingHostId)!=approvingHost||Id(reply.ApprovalId)!=expected.ApprovalId||
            reply.AcknowledgedFingerprint!=request.AcknowledgedFingerprint||
            reply.Result is not (PeerRecoveryCompletionResult.Recorded or PeerRecoveryCompletionResult.AlreadyRecorded or PeerRecoveryCompletionResult.KeyMismatch))
            throw new ArgumentException("Recovery reply does not match its authenticated exchange.");
    }
}
