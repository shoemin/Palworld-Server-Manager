using System.Security.AccessControl;
using System.Security.Principal;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

/// <summary>
/// Windows machine-wide Host data-root discovery (SS6).
///
/// v0.4 used LocalApplicationData (per-user). v0.5 Host state is machine-wide and must not live in
/// any user profile, so this resolves under CommonApplicationData instead. The distinct \Host
/// subdirectory keeps it clearly separate from per-user v0.4 data during a later migration (#51).
///
/// Host.Persistence stays unaware of ProgramData: it accepts the resolved root by injection
/// (PLATFORM-001).
/// </summary>
public sealed class WindowsHostDataRootProvider : IHostDataRootProvider
{
    public const string ProductDirectoryName = "PalworldServerManager";
    public const string HostDirectoryName = "Host";

    public string GetMachineWideHostDataRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ProductDirectoryName,
        HostDirectoryName);

    /// <summary>
    /// Creates the root and applies the accepted restrictive ACL.
    ///
    /// Inheritance is DISABLED and not copied, so the directory never silently inherits broader
    /// %ProgramData% rights - in particular, ordinary users must not reach Host state just because
    /// they can start the service. Activation-group membership is start eligibility only; it never
    /// confers direct SQLite authority (HOST-002, PERSIST-001).
    /// </summary>
    public void EnsureCreatedWithHostStateAcl(string root, string serviceAccountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceAccountName);

        var directory = Directory.CreateDirectory(root);
        var security = BuildHostStateSecurity(serviceAccountName);
        directory.SetAccessControl(security);
    }

    /// <summary>
    /// Builds the Host-state directory ACL. Separated from the filesystem call so it is unit
    /// testable without touching a real machine-wide directory.
    /// </summary>
    public static DirectorySecurity BuildHostStateSecurity(string serviceAccountName)
    {
        var security = new DirectorySecurity();

        // Drop inheritance entirely rather than copying inherited ACEs in.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        // Deliberately no explicit SetOwner call. This directory is created and ACL'd by the
        // Host process itself, running as the per-service virtual account - a non-Administrator
        // identity that is NOT allowed to assign ownership to a SID it does not hold
        // (Persist throws "The security identifier is not allowed to be the owner of this
        // object" for exactly this reason; confirmed against a real service in CI). Leaving the
        // owner at its OS-assigned default (the creating identity, i.e. the service SID itself)
        // does not weaken the intended posture: Administrators still gets a Full Control ACE
        // below, which is sufficient for it to take ownership/manage the object at any time.
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

        // The dedicated per-service virtual account, addressed by its DERIVED per-service SID
        // rather than by name: NTAccount.Translate can only resolve the NT SERVICE account once
        // the service exists, which would force this directory to be created strictly after
        // service creation. Deriving the SID removes that ordering dependency.
        security.AddAccessRule(new FileSystemAccessRule(
            ServiceSecurityIdentifier.ForServiceName(ExtractServiceName(serviceAccountName)),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

        // NOTE: no rule is added for the activation group, deliberately.
        return security;
    }

    /// <summary>Accepts either an NT SERVICE-qualified name or a bare service name.</summary>
    internal static string ExtractServiceName(string serviceAccountName)
    {
        var separator = serviceAccountName.LastIndexOf('\\');
        return separator >= 0 ? serviceAccountName[(separator + 1)..] : serviceAccountName;
    }
}
