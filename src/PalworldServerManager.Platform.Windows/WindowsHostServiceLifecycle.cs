using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows.Native;

namespace PalworldServerManager.Platform.Windows;

/// <summary>
/// Privileged Windows SCM lifecycle for the Host service (SS2, SS2a). Requires Administrator.
///
/// Deliberately separate from the ordinary-client IHostActivation seam, which gets only the two
/// bounded rights this class grants to the activation group.
/// </summary>
public sealed class WindowsHostServiceLifecycle : IHostServiceLifecycle, IBootStartPlatform
{
    public const string DefaultServiceName = "PalworldServerManagerHost";
    public const string DefaultDisplayName = "Palworld Server Manager Host";
    public const string DefaultActivationGroupName = "PalworldServerManager Users";

    private readonly string _serviceName;

    public WindowsHostServiceLifecycle(string serviceName = DefaultServiceName)
    {
        _serviceName = serviceName;
    }

    /// <summary>
    /// The per-service virtual account. No managed password, a stable per-service SID usable in
    /// filesystem ACLs, and no broad machine/network authority - unlike the shared LocalService /
    /// NetworkService accounts, which would not be a DEDICATED identity at all (SS2).
    /// </summary>
    public string ServiceAccountName => $@"NT SERVICE\{_serviceName}";

    public string ServiceName => _serviceName;

    public Task<HostServiceStatus> QueryStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var scm = ServiceControlManagerNative.OpenSCManager(null, null, ServiceControlManagerNative.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            throw ServiceControlManagerNative.LastError("OpenSCManager");
        }

        try
        {
            var service = ServiceControlManagerNative.OpenService(
                scm, _serviceName, ServiceControlManagerNative.SERVICE_QUERY_CONFIG | ServiceControlManagerNative.SERVICE_QUERY_STATUS);

            if (service == IntPtr.Zero)
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (err == ServiceControlManagerNative.ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    return Task.FromResult(new HostServiceStatus(HostServiceState.NotInstalled, HostServiceStartMode.Manual, null));
                }

                throw new Win32Exception(err);
            }

            ServiceControlManagerNative.CloseServiceHandle(service);
        }
        finally
        {
            ServiceControlManagerNative.CloseServiceHandle(scm);
        }

        // Installed: read live state/start mode through the managed wrapper.
        using var controller = new ServiceController(_serviceName);
        var state = controller.Status switch
        {
            ServiceControllerStatus.Stopped => HostServiceState.Stopped,
            ServiceControllerStatus.StartPending => HostServiceState.StartPending,
            ServiceControllerStatus.Running => HostServiceState.Running,
            ServiceControllerStatus.StopPending => HostServiceState.StopPending,
            _ => HostServiceState.Other,
        };
        var mode = controller.StartType switch
        {
            ServiceStartMode.Automatic => HostServiceStartMode.Automatic,
            ServiceStartMode.Disabled => HostServiceStartMode.Disabled,
            _ => HostServiceStartMode.Manual,
        };

        return Task.FromResult(new HostServiceStatus(state, mode, ServiceAccountName));
    }

    public Task InstallAsync(HostServiceInstallOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        // Caller-supplied executable path; #41 never chooses an install directory or copies files.
        var binaryPath = ServiceBinaryPath.Build(options.ExecutablePath, options.Arguments);
        var startType = ToNativeStartType(options.StartMode);

        var scm = ServiceControlManagerNative.OpenSCManager(
            null, null, ServiceControlManagerNative.SC_MANAGER_CONNECT | ServiceControlManagerNative.SC_MANAGER_CREATE_SERVICE);
        if (scm == IntPtr.Zero)
        {
            throw ServiceControlManagerNative.LastError("OpenSCManager");
        }

        try
        {
            var service = ServiceControlManagerNative.CreateService(
                scm,
                _serviceName,
                options.ActivationGroupName is null ? DefaultDisplayName : DefaultDisplayName,
                ServiceControlManagerNative.SERVICE_CHANGE_CONFIG | ServiceControlManagerNative.SERVICE_QUERY_CONFIG
                    | ServiceControlManagerNative.READ_CONTROL | ServiceControlManagerNative.WRITE_DAC,
                ServiceControlManagerNative.SERVICE_WIN32_OWN_PROCESS,
                startType,
                ServiceControlManagerNative.SERVICE_ERROR_NORMAL,
                binaryPath,
                null,
                IntPtr.Zero,
                null,
                ServiceAccountName,   // virtual account
                null);                // virtual accounts take a null password

            if (service == IntPtr.Zero)
            {
                throw ServiceControlManagerNative.LastError("CreateService");
            }

            try
            {
                // Give the service its own SID so the per-service identity can be used in ACLs.
                var sidInfo = new ServiceControlManagerNative.SERVICE_SID_INFO
                {
                    dwServiceSidType = ServiceControlManagerNative.SERVICE_SID_TYPE_UNRESTRICTED,
                };
                if (!ServiceControlManagerNative.ChangeServiceConfig2(
                        service, ServiceControlManagerNative.SERVICE_CONFIG_SERVICE_SID_INFO, ref sidInfo))
                {
                    throw ServiceControlManagerNative.LastError("ChangeServiceConfig2(SERVICE_SID_INFO)");
                }

                if (options.ActivationGroupName is { Length: > 0 } groupName)
                {
                    ApplyActivationGroupAce(service, groupName);
                }
            }
            finally
            {
                ServiceControlManagerNative.CloseServiceHandle(service);
            }
        }
        finally
        {
            ServiceControlManagerNative.CloseServiceHandle(scm);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Grants the activation group EXACTLY SERVICE_START + SERVICE_QUERY_STATUS, preserving every
    /// existing ACE (SYSTEM/Administrators maintenance rights included).
    /// </summary>
    private static void ApplyActivationGroupAce(IntPtr service, string groupName)
    {
        var sid = ResolveGroupSid(groupName);

        if (!ServiceControlManagerNative.QueryServiceObjectSecurity(
                service, ServiceControlManagerNative.DACL_SECURITY_INFORMATION, null, 0, out var needed))
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (err != ServiceControlManagerNative.ERROR_INSUFFICIENT_BUFFER)
            {
                throw new Win32Exception(err, "QueryServiceObjectSecurity(size probe) failed.");
            }
        }

        var buffer = new byte[needed];
        if (!ServiceControlManagerNative.QueryServiceObjectSecurity(
                service, ServiceControlManagerNative.DACL_SECURITY_INFORMATION, buffer, needed, out _))
        {
            throw ServiceControlManagerNative.LastError("QueryServiceObjectSecurity");
        }

        var existing = new RawSecurityDescriptor(buffer, 0);
        var updated = ServiceDaclBuilder.AddActivationGroupAce(existing, sid);

        if (!ServiceControlManagerNative.SetServiceObjectSecurity(
                service, ServiceControlManagerNative.DACL_SECURITY_INFORMATION, ServiceDaclBuilder.ToBinaryForm(updated)))
        {
            throw ServiceControlManagerNative.LastError("SetServiceObjectSecurity");
        }
    }

    private static SecurityIdentifier ResolveGroupSid(string groupName)
    {
        var account = groupName.Contains('\\') ? new NTAccount(groupName) : new NTAccount(Environment.MachineName, groupName);
        return (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        using var controller = new ServiceController(_serviceName);
        if (controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            return;
        }

        controller.Start();
        await WaitForStatusAsync(controller, ServiceControllerStatus.Running, ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        using var controller = new ServiceController(_serviceName);
        if (controller.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
        {
            return;
        }

        controller.Stop();
        await WaitForStatusAsync(controller, ServiceControllerStatus.Stopped, ct).ConfigureAwait(false);
    }

    private static async Task WaitForStatusAsync(ServiceController controller, ServiceControllerStatus target, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            controller.Refresh();
            if (controller.Status == target)
            {
                return;
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        throw new System.TimeoutException($"Service '{controller.ServiceName}' did not reach {target} within the timeout.");
    }

    /// <summary>
    /// Removes the SERVICE REGISTRATION only. It deliberately does not touch the Host data root,
    /// the database, or a pre-existing activation group.
    /// </summary>
    public Task UninstallAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var scm = ServiceControlManagerNative.OpenSCManager(null, null, ServiceControlManagerNative.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            throw ServiceControlManagerNative.LastError("OpenSCManager");
        }

        try
        {
            var service = ServiceControlManagerNative.OpenService(scm, _serviceName, ServiceControlManagerNative.DELETE);
            if (service == IntPtr.Zero)
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (err == ServiceControlManagerNative.ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    return Task.CompletedTask;
                }

                throw new Win32Exception(err);
            }

            try
            {
                if (!ServiceControlManagerNative.DeleteService(service))
                {
                    throw ServiceControlManagerNative.LastError("DeleteService");
                }
            }
            finally
            {
                ServiceControlManagerNative.CloseServiceHandle(service);
            }
        }
        finally
        {
            ServiceControlManagerNative.CloseServiceHandle(scm);
        }

        return Task.CompletedTask;
    }

    // ---- IBootStartPlatform: maps ONLY onto the Windows service start type ----

    public async Task<bool> IsBootStartEnabledAsync(CancellationToken ct = default)
        => (await QueryStatusAsync(ct).ConfigureAwait(false)).StartMode == HostServiceStartMode.Automatic;

    public Task SetBootStartEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SetStartType(enabled ? HostServiceStartMode.Automatic : HostServiceStartMode.Manual);
        return Task.CompletedTask;
    }

    private void SetStartType(HostServiceStartMode mode)
    {
        var scm = ServiceControlManagerNative.OpenSCManager(null, null, ServiceControlManagerNative.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            throw ServiceControlManagerNative.LastError("OpenSCManager");
        }

        try
        {
            var service = ServiceControlManagerNative.OpenService(scm, _serviceName, ServiceControlManagerNative.SERVICE_CHANGE_CONFIG);
            if (service == IntPtr.Zero)
            {
                throw ServiceControlManagerNative.LastError("OpenService");
            }

            try
            {
                if (!ServiceControlManagerNative.ChangeServiceConfig(
                        service,
                        ServiceControlManagerNative.SERVICE_NO_CHANGE,
                        ToNativeStartType(mode),
                        ServiceControlManagerNative.SERVICE_NO_CHANGE,
                        null, null, IntPtr.Zero, null, null, null, null))
                {
                    throw ServiceControlManagerNative.LastError("ChangeServiceConfig(startType)");
                }
            }
            finally
            {
                ServiceControlManagerNative.CloseServiceHandle(service);
            }
        }
        finally
        {
            ServiceControlManagerNative.CloseServiceHandle(scm);
        }
    }

    public static uint ToNativeStartType(HostServiceStartMode mode) => mode switch
    {
        HostServiceStartMode.Automatic => ServiceControlManagerNative.SERVICE_AUTO_START,
        HostServiceStartMode.Disabled => ServiceControlManagerNative.SERVICE_DISABLED,
        _ => ServiceControlManagerNative.SERVICE_DEMAND_START,   // desktop default: boot-start OFF
    };
}
