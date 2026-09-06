using System.ServiceProcess;
using PalworldServerManager.Host;

// Production composition root. Provisioning is a separate privileged caller of the platform
// seam; the running Host never modifies SCM permissions or chooses an installation layout.
using var lifetime = WindowsHostComposition.CreateLifetime();
ServiceBase.Run(lifetime);
return 0;
