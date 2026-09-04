using System.ServiceProcess;

namespace PalworldServerManager.Platform.Windows;

/// <summary>
/// The SCM process lifetime for the Host (SS2).
///
/// Deliberately a bounded ServiceBase-derived shell rather than the Generic Host: #41's Host holds
/// an exclusivity lock and an open database and nothing else. Adopting
/// Microsoft.Extensions.Hosting here would pull ~31 transitive packages before any consumer that
/// justifies them exists; #42/#44 (local IPC, remote transport) are the slices that can make that
/// choice with knowledge of their own requirements. Nothing here forecloses it - the runtime
/// callbacks below are ordinary delegates.
///
/// The actual startup-readiness and deterministic-shutdown logic lives in
/// <see cref="HostServiceLifetime"/>, which has no ServiceBase dependency and is unit-testable on
/// its own; this class is a thin adapter onto the three ServiceBase lifecycle callbacks.
/// </summary>
public sealed class WindowsHostServiceRuntime : ServiceBase
{
    private readonly HostServiceLifetime _lifetime;

    public WindowsHostServiceRuntime(string serviceName, Func<CancellationToken, TaskCompletionSource<bool>, Task> runAsync)
    {
        ServiceName = serviceName;
        _lifetime = new HostServiceLifetime(runAsync);

        // The ordinary activation path only ever needs start; the Host still accepts an
        // administrative stop through SCM's own rights, which the activation group is not granted.
        CanStop = true;
        CanShutdown = true;
        CanPauseAndContinue = false;
    }

    /// <summary>Registers this executable with SCM and blocks for the service lifetime.</summary>
    public static void Run(string serviceName, Func<CancellationToken, TaskCompletionSource<bool>, Task> runAsync)
        => ServiceBase.Run(new WindowsHostServiceRuntime(serviceName, runAsync));

    protected override void OnStart(string[] args) => _lifetime.Start();

    protected override void OnStop() => _lifetime.StopAndWait();

    protected override void OnShutdown() => _lifetime.StopAndWait();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetime.Dispose();
        }

        base.Dispose(disposing);
    }
}
