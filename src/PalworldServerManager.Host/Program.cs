using PalworldServerManager.Host;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

// Host composition root (#39 SS1, #41).
//
// This is the ONE place that selects the Windows platform implementations (PLATFORM-001) - no
// OS branching exists anywhere in shared Host or persistence code.

// "--integration-service" is TEST-ONLY plumbing for the #41 privileged Windows integration
// harness (WindowsIntegrationTests). It is never used by ordinary installation/activation, and it
// is the ONLY way this executable ever uses anything other than the fixed production service
// name, the %ProgramData% data root, and the default #40 exclusivity mutex - see below.
if (args.Length > 0 && string.Equals(args[0], "--integration-service", StringComparison.OrdinalIgnoreCase))
{
    var options = IntegrationServiceArgs.Parse(args);
    var integrationLifecycle = new WindowsHostServiceLifecycle(options.ServiceName);
    var integrationRuntime = new HostRuntime(
        new FixedHostDataRootProvider(options.DataRoot),
        integrationLifecycle.ServiceAccountName,
        options.MutexName);

    WindowsHostServiceRuntime.Run(options.ServiceName, integrationRuntime.RunAsync);
    return 0;
}

var lifecycle = new WindowsHostServiceLifecycle();
var runtime = new HostRuntime(new WindowsHostDataRootProvider(), lifecycle.ServiceAccountName);

// "--console" runs the same bounded runtime in the foreground for development and integration
// testing, without registering with SCM. Anything else means SCM started us as a service.
if (args.Length > 0 && string.Equals(args[0], "--console", StringComparison.OrdinalIgnoreCase))
{
    using var stopping = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopping.Cancel();
    };

    await runtime.RunAsync(stopping.Token).ConfigureAwait(false);
    return 0;
}

WindowsHostServiceRuntime.Run(lifecycle.ServiceName, runtime.RunAsync);
return 0;

/// <summary>
/// Parses the TEST-ONLY "--integration-service --service-name X --data-root Y --mutex-name Z"
/// arguments. Not a product configuration surface - production invocation never passes these.
/// </summary>
internal sealed record IntegrationServiceArgs(string ServiceName, string DataRoot, string MutexName)
{
    public static IntegrationServiceArgs Parse(string[] args)
    {
        string? serviceName = null;
        string? dataRoot = null;
        string? mutexName = null;

        for (var i = 1; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--service-name":
                    serviceName = args[++i];
                    break;
                case "--data-root":
                    dataRoot = args[++i];
                    break;
                case "--mutex-name":
                    mutexName = args[++i];
                    break;
            }
        }

        if (serviceName is null || dataRoot is null || mutexName is null)
        {
            throw new ArgumentException(
                "--integration-service requires --service-name, --data-root, and --mutex-name.");
        }

        return new IntegrationServiceArgs(serviceName, dataRoot, mutexName);
    }
}

/// <summary>
/// TEST-ONLY <see cref="IHostDataRootProvider"/> pointing at a caller-supplied root instead of the
/// fixed %ProgramData% production path. ACL application still delegates to the real
/// <see cref="WindowsHostDataRootProvider"/> logic, so only the ROOT PATH differs from production
/// - not the security policy applied to it.
/// </summary>
internal sealed class FixedHostDataRootProvider(string root) : IHostDataRootProvider
{
    public string GetMachineWideHostDataRoot() => root;

    public void EnsureCreatedWithHostStateAcl(string rootPath, string serviceAccountName)
        => new WindowsHostDataRootProvider().EnsureCreatedWithHostStateAcl(rootPath, serviceAccountName);
}
