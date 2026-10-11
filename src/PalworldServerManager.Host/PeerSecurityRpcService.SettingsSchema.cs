using Grpc.Core;
using PalworldServerManager.Contracts.Wire;
using ServerCapability = PalworldServerManager.Core.Authorization.ServerCapability;

namespace PalworldServerManager.Host;

public sealed partial class PeerSecurityRpcService
{
    public override Task<SettingsSchemaReply> GetSettingsSchema(SettingsSchemaRequest request, ServerCallContext context)
        => SettingsSchemaRpcBoundary.Invoke(() => runtime.Permissions.Invoke(context.GetHttpContext(),
            call => runtime.SettingsSchema.Read(request.Server, server => call.RequireServer(ServerCapability.ViewServer, server), context.CancellationToken),
            context.CancellationToken, FeatureCapability.SettingsSchema));
}
