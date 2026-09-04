using System.ComponentModel;
using PalworldServerManager.Platform.Windows.Native;

namespace PalworldServerManager.Platform.Windows;

/// <summary>
/// Seam over the native local-group existence-check/create operations. Deliberately exposes NO
/// membership operation at all - "the activation group's membership is never changed by
/// provisioning" is therefore a property of this interface's shape, not just of what
/// <see cref="LocalGroupProvisioner"/> happens to call.
/// </summary>
public interface ILocalGroupNative
{
    bool GroupExists(string groupName);

    void CreateGroup(string groupName);
}

/// <summary>Production implementation over NetApi32 (SS2). Never shells out to net.exe/sc.exe.</summary>
public sealed class WindowsLocalGroupNative : ILocalGroupNative
{
    public bool GroupExists(string groupName)
    {
        var rc = NetApi32Native.NetLocalGroupGetInfo(null, groupName, 1, out var buffer);
        if (rc == NetApi32Native.NERR_Success)
        {
            NetApi32Native.NetApiBufferFree(buffer);
            return true;
        }

        if (rc == NetApi32Native.NERR_GroupNotFound)
        {
            return false;
        }

        throw new Win32Exception(rc, $"NetLocalGroupGetInfo('{groupName}') failed with code {rc}.");
    }

    public void CreateGroup(string groupName)
    {
        var info = new NetApi32Native.LOCALGROUP_INFO_0 { lgrpi0_name = groupName };
        var rc = NetApi32Native.NetLocalGroupAdd(null, 0, ref info, out _);

        if (rc is NetApi32Native.NERR_Success or NetApi32Native.NERR_GroupExists)
        {
            // A concurrent creator winning the race is treated as success - provisioning is
            // idempotent, not "exactly one creator may ever succeed".
            return;
        }

        throw new Win32Exception(rc, $"NetLocalGroupAdd('{groupName}') failed with code {rc}.");
    }
}

/// <summary>
/// Ensures the Host activation group exists (SS2). This is EXISTENCE-ONLY provisioning: it never
/// adds, removes, or enumerates members. Installing user, current user, and any intended Owner
/// are never added here - membership is a deliberate, separate administrative act, and no
/// application-level authority is ever inferred from it.
/// </summary>
public sealed class LocalGroupProvisioner
{
    private readonly ILocalGroupNative _native;

    public LocalGroupProvisioner(ILocalGroupNative? native = null)
    {
        _native = native ?? new WindowsLocalGroupNative();
    }

    /// <summary>Idempotent: a no-op when the group already exists.</summary>
    public void EnsureExists(string groupName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        if (_native.GroupExists(groupName))
        {
            return;
        }

        _native.CreateGroup(groupName);
    }
}
