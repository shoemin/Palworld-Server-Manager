using PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Contracts;

// Shape and expected semantic identities only. This never authenticates a connection.
public static class PeerUnpairWire
{
    private static Guid Id(string value) => Guid.TryParseExact(value,"D",out var id)&&id!=Guid.Empty
        ?id:throw new ArgumentException("Valid unpair Host identity required.");
    public static Guid ReceivingHost(PeerUnpairNotice notice)
    { ArgumentNullException.ThrowIfNull(notice);return Id(notice.ReceivingHostId); }
    public static void ValidateReply(PeerUnpairReply reply,Guid receivingHost,Guid unpairedHost)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if(receivingHost==Guid.Empty||unpairedHost==Guid.Empty||receivingHost==unpairedHost||
            Id(reply.ReceivingHostId)!=receivingHost||Id(reply.UnpairedHostId)!=unpairedHost||
            reply.Result is not (PeerUnpairResult.Recorded or PeerUnpairResult.AlreadyRecorded))
            throw new ArgumentException("Unpair reply does not match its authenticated exchange.");
    }
}
