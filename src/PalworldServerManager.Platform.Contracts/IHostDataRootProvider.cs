namespace PalworldServerManager.Platform.Contracts;

/// <summary>
/// OS-specific discovery of the machine-wide Host data root (SS6).
///
/// #40 deliberately left this OUT of Host.Persistence: that project accepts an injected root and
/// owns only the layout beneath it. This seam keeps the OS-specific discovery behind the platform
/// boundary so no ProgramData knowledge leaks into shared persistence code (PLATFORM-001).
/// </summary>
public interface IHostDataRootProvider
{
    /// <summary>Absolute path to the machine-wide Host data root.</summary>
    string GetMachineWideHostDataRoot();

    /// <summary>
    /// Creates the root if absent and applies the accepted restrictive ACL: the Host service
    /// identity plus SYSTEM/Administrators get access; the ordinary activation group gets NONE, so
    /// start eligibility never becomes direct SQLite authority (HOST-002, PERSIST-001).
    /// </summary>
    void EnsureCreatedWithHostStateAcl(string root, string serviceAccountName);
}
