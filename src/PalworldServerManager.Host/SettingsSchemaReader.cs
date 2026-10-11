using PalworldServerManager.Contracts;
using PalworldServerManager.Core.Settings;
using PalworldServerManager.Host.Persistence;
using Wire = PalworldServerManager.Contracts.Wire;
using ServerRef = PalworldServerManager.Core.Authorization.ServerRef;

namespace PalworldServerManager.Host;

internal sealed class SettingsSchemaReader(HostDatabase database, Guid host)
{
    private readonly ServerInventoryRepository inventory = new(database, host);
    internal static ServerRef Target(Wire.ServerRef? server)
    {
        if (!SettingsSchemaValidation.IsValidServer(server)) throw new ArgumentException("Qualified server required.");
        return new(Guid.Parse(server!.AuthoritativeHostId), Guid.Parse(server.ServerProfileId));
    }
    internal Wire.SettingsSchemaReply Read(Wire.ServerRef? requested, Action<ServerRef> authorize, CancellationToken ct)
    {
        var server = Target(requested);
        if (server.AuthoritativeHostId != host) throw new ArgumentException("Local server required.");
        ct.ThrowIfCancellationRequested(); authorize(server);
        if (inventory.Read(server, ct) is null) throw new InvalidOperationException("Server is not registered.");
        var reply = new Wire.SettingsSchemaReply { Server = new() { AuthoritativeHostId = host.ToString("D"), ServerProfileId = server.ServerProfileId.ToString("D") } };
        reply.Definitions.AddRange(PalworldSettingCatalog.KnownDefinitions().Select(SettingsContractMapping.Definition));
        if (!SettingsSchemaValidation.IsValid(reply)) throw new InvalidOperationException("Schema cannot be represented safely.");
        // Reads do not turn a prior policy observation into an execution permit.
        authorize(server); ct.ThrowIfCancellationRequested(); return reply;
    }
}
