using Google.Protobuf;
using PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Contracts;

public static class SettingsSnapshotValidation
{
    public const int MaximumRequestBytes = 16 * 1024;
    public const int MaximumResponseBytes = 4 * 1024 * 1024;
    // An RPC observation has reached the durable content reader: revision zero
    // remains valid for an internal pre-observation DTO, never a successful reply.
    public static bool IsValidReply(SettingsSnapshot? reply, Wire.ServerRef? requested)
        => SettingsWireValidation.IsValid(reply) && SettingsSchemaValidation.IsValidServer(requested) &&
           reply!.Revision != "0" && reply.Server.AuthoritativeHostId == requested!.AuthoritativeHostId &&
           reply.Server.ServerProfileId == requested.ServerProfileId && reply.CalculateSize() <= MaximumResponseBytes;
}
