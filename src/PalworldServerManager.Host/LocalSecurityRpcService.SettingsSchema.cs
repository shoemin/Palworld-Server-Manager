using System.Security.Authentication;
using Grpc.Core;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using ServerCapability = PalworldServerManager.Core.Authorization.ServerCapability;

namespace PalworldServerManager.Host;

public sealed partial class LocalSecurityRpcService
{
    public override Task<SettingsSchemaReply> GetSettingsSchema(SettingsSchemaRequest request, ServerCallContext context)
        => SettingsSchemaRpcBoundary.Invoke(() => runtime.Permissions.Invoke(context.GetHttpContext(),
            call => runtime.SettingsSchema.Read(request.Server, server => call.RequireServer(ServerCapability.ViewServer, server), context.CancellationToken),
            context.CancellationToken, FeatureCapability.SettingsSchema));

    public override Task<SettingsSchemaReply> GetRemoteSettingsSchema(LocalRemoteSettingsSchemaRequest request, ServerCallContext context)
        => Dispatch(context, false, false, session => SettingsSchemaRpcBoundary.Invoke(() =>
        {
            if (!session.BelongsTo(runtime)) throw new AuthenticationException();
            session.Protocol!.Require(FeatureCapability.LocalRemoteSettingsSchema);
            var generation = runtime.Pairing ?? throw new InvalidOperationException();
            var target = SettingsSchemaReader.Target(request.Server);
            if (request.Destination is null || Id(request.Destination.PeerHostId) != target.AuthoritativeHostId ||
                request.Destination.PeerPort is 0 or > 65535) throw new ArgumentException();
            var address = HostReachableAddress.Parse(request.Destination.ReachableHost, (int)request.Destination.PeerPort);
            return runtime.SettingsNetwork.Read(generation, session.Authentication, target, address, context.CancellationToken);
        }));
}
