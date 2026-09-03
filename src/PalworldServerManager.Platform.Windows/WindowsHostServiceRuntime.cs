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
/// Resource lifetime is owned by the SERVICE LIFECYCLE, not by an arbitrary background thread:
/// OnStop signals cancellation and then waits for the started work to finish releasing, so the
/// #40 database and HostExclusivityLock are released before OnStop returns.
/// </summary>
public sealed class WindowsHostServiceRuntime : ServiceBase
{
    private readonly Func<CancellationToken, Task> _runAsync;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _running;

    public WindowsHostServiceRuntime(string serviceName, Func<CancellationToken, Task> runAsync)
    {
        ServiceName = serviceName;
        _runAsync = runAsync ?? throw new ArgumentNullException(nameof(runAsync));

        // The ordinary activation path only ever needs start; the Host still accepts an
        // administrative stop through SCM's own rights, which the activation group is not granted.
        CanStop = true;
        CanShutdown = true;
        CanPauseAndContinue = false;
    }

    /// <summary>Registers this executable with SCM and blocks for the service lifetime.</summary>
    public static void Run(string serviceName, Func<CancellationToken, Task> runAsync)
        => ServiceBase.Run(new WindowsHostServiceRuntime(serviceName, runAsync));

    protected override void OnStart(string[] args)
    {
        _running = Task.Run(async () =>
        {
            try
            {
                await _runAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // Ordinary stop.
            }
        });

        // Surface an immediate startup failure to SCM rather than reporting a healthy service
        // that already died (e.g. the exclusivity lock was already held).
        if (_running.IsFaulted)
        {
            throw _running.Exception!.GetBaseException();
        }
    }

    protected override void OnStop() => ShutDownDeterministically();

    protected override void OnShutdown() => ShutDownDeterministically();

    private void ShutDownDeterministically()
    {
        _stopping.Cancel();

        // Wait for the runtime to actually finish releasing the database and the exclusivity lock
        // before OnStop completes - a lock still held after "stopped" would block the next start.
        try
        {
            _running?.Wait(TimeSpan.FromSeconds(30));
        }
        catch (AggregateException)
        {
            // Faults already surfaced through the service's own error handling.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }
}
