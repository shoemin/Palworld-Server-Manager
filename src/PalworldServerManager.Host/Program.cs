using System.ServiceProcess;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Windows;

// Production composition root. Provisioning is a separate privileged caller of the platform
// seam; the running Host never modifies SCM permissions or chooses an installation layout.
using var lifetime = new WindowsHostServiceLifetime(WindowsHostPlatform.ProductServiceName,
    ct => HostServiceRuntime.Start(new HostDataRoot(new WindowsHostPlatform().GetHostDataRoot()), ct));
ServiceBase.Run(lifetime);
return 0;
