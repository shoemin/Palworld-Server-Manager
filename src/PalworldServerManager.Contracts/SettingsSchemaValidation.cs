using Google.Protobuf;
using Wire = PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Contracts;

public static class SettingsSchemaValidation
{
    public const int MaximumBytes = 16 * 1024;
    public static bool IsValidServer(Wire.ServerRef? server) => server is not null &&
        Canonical(server.AuthoritativeHostId) && Canonical(server.ServerProfileId);
    private static bool Canonical(string text) => Guid.TryParseExact(text, "D", out var id) &&
        id != Guid.Empty && id.ToString("D") == text;
    public static bool IsValid(Wire.SettingsSchemaReply? reply)
    {
        if (reply is null || !IsValidServer(reply.Server) || reply.Definitions.Count > SettingsWireValidation.MaximumEntries ||
            reply.CalculateSize() > MaximumBytes) return false;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return reply.Definitions.All(d => SettingsWireValidation.Definition(d) && keys.Add(d.Key));
    }
}
