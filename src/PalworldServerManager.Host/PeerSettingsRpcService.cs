using Grpc.Core;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using ServerCapability = PalworldServerManager.Core.Authorization.ServerCapability;

namespace PalworldServerManager.Host;

public sealed class PeerSettingsRpcService(PeerSecurityRpcRuntime runtime) : PeerSettingsProtocol.PeerSettingsProtocolBase
{
    public override Task<SettingsSnapshot> GetServerSettings(SettingsSnapshotRequest request, ServerCallContext context)
        => SettingsSnapshotRpcBoundary.Invoke(() => runtime.Permissions.Invoke(context.GetHttpContext(), call =>
        {
            var reader = runtime.SettingsReader ?? throw new InvalidOperationException();
            var result = reader.Read(request.Server, server => call.RequireServer(ServerCapability.ViewServer, server),
                (c, tx, server) => call.ValidateServer(c, tx, ServerCapability.ViewServer, server), context.CancellationToken);
            if (!SettingsSnapshotValidation.IsValidReply(result, request.Server)) throw new InvalidOperationException();
            return result;
        }, context.CancellationToken, FeatureCapability.SettingsSnapshot));
}
