using PalworldServerManager.Host;
using PalworldServerManager.Platform.Windows;

// Host composition root (#39 SS1, #41).
//
// This is the ONE place that selects the Windows platform implementations (PLATFORM-001) - no
// OS branching exists anywhere in shared Host or persistence code.

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
