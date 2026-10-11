using System.Security.Authentication;
using Grpc.Core;
using PalworldServerManager.Contracts;
using PalworldServerManager.Contracts.Wire;
using ServerCapability = PalworldServerManager.Core.Authorization.ServerCapability;

namespace PalworldServerManager.Host;

public sealed class LocalSettingsRpcService(LocalSecurityRpcRuntime runtime) : LocalSettingsProtocol.LocalSettingsProtocolBase
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

    public override Task<SettingsSnapshot> GetRemoteServerSettings(LocalRemoteSettingsSnapshotRequest request, ServerCallContext context)
        => new LocalSecurityRpcService(runtime).Dispatch(context, false, false, session => SettingsSnapshotRpcBoundary.Invoke(() =>
        {
            if (!session.BelongsTo(runtime)) throw new AuthenticationException();
            session.Protocol!.Require(FeatureCapability.LocalRemoteSettingsSnapshot);
            var generation = runtime.Pairing ?? throw new InvalidOperationException();
            var target = SettingsSchemaReader.Target(request.Server);
            if (request.Destination is null || !Guid.TryParseExact(request.Destination.PeerHostId, "D", out var peer) ||
                peer != target.AuthoritativeHostId || request.Destination.PeerPort is 0 or > 65535) throw new ArgumentException();
            var address = HostReachableAddress.Parse(request.Destination.ReachableHost, (int)request.Destination.PeerPort);
            return runtime.SnapshotNetwork.Read(generation, session.Authentication, target, address, context.CancellationToken);
        }));
}
