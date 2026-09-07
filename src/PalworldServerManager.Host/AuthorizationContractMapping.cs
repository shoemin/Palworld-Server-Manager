using Domain=PalworldServerManager.Core.Authorization;
using Protocol=PalworldServerManager.Contracts;
using Wire=PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.Host;

// Value mapping only, never a grant/authentication decision. No Core -> Contracts dependency.
internal static class AuthorizationContractMapping
{
    internal static Domain.ServerRef ToDomain(Protocol.ServerRef value)
    {
        ArgumentNullException.ThrowIfNull(value);return new(value.AuthoritativeHostId.Value,value.ServerProfileId);
    }
    internal static Protocol.ServerRef ToProtocol(Domain.ServerRef value)
    {
        ArgumentNullException.ThrowIfNull(value);return new(new Protocol.HostId(value.AuthoritativeHostId),value.ServerProfileId);
    }
    internal static Domain.HostCapability ToDomain(Wire.HostCapability value)=>value switch
    {
        Wire.HostCapability.CreateServer=>Domain.HostCapability.CreateServer,
        Wire.HostCapability.ManageHostSettings=>Domain.HostCapability.ManageHostSettings,
        Wire.HostCapability.ManageTrustedManagers=>Domain.HostCapability.ManageTrustedManagers,
        Wire.HostCapability.ManagePermissions=>Domain.HostCapability.ManagePermissions,
        Wire.HostCapability.ManageHostUpdates=>Domain.HostCapability.ManageHostUpdates,
        _=>throw new ArgumentException("Unknown Host capability.")
    };
    internal static Domain.ServerCapability ToDomain(Wire.ServerCapability value)=>value switch
    {
        Wire.ServerCapability.ViewServer=>Domain.ServerCapability.ViewServer,
        Wire.ServerCapability.StartStopRestart=>Domain.ServerCapability.StartStopRestart,
        Wire.ServerCapability.EditSettings=>Domain.ServerCapability.EditSettings,
        Wire.ServerCapability.ManageBackups=>Domain.ServerCapability.ManageBackups,
        Wire.ServerCapability.TransferExport=>Domain.ServerCapability.TransferExport,
        Wire.ServerCapability.DeleteServer=>Domain.ServerCapability.DeleteServer,
        Wire.ServerCapability.ManageServerSharing=>Domain.ServerCapability.ManageServerSharing,
        _=>throw new ArgumentException("Unknown server capability.")
    };
}
