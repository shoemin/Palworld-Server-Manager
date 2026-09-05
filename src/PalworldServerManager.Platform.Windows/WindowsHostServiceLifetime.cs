using System.ServiceProcess;

namespace PalworldServerManager.Platform.Windows;

// Resource ownership follows the SCM lifetime; the callback establishes resources synchronously
// before OnStart succeeds, and its lease is disposed before OnStop returns.
public sealed class WindowsHostServiceLifetime : ServiceBase
{
    private readonly Func<CancellationToken, IDisposable> _start;
    private CancellationTokenSource? _stop;
    private IDisposable? _runtime;
    public WindowsHostServiceLifetime(string serviceName, Func<CancellationToken, IDisposable> start)
    { ServiceName = serviceName; _start = start; CanStop = true; CanShutdown = true; AutoLog = false; }
    protected override void OnStart(string[] args)
    {
        _stop = new CancellationTokenSource();
        try { _runtime = _start(_stop.Token); }
        catch { _stop.Dispose(); _stop = null; throw; }
    }
    protected override void OnStop() => ReleaseRuntime();
    protected override void OnShutdown() => ReleaseRuntime();
    private void ReleaseRuntime()
    {
        try { _stop?.Cancel(); }
        finally
        {
            try { _runtime?.Dispose(); }
            finally { _runtime = null; _stop?.Dispose(); _stop = null; }
        }
    }
    protected override void Dispose(bool disposing)
    { if (disposing) ReleaseRuntime(); base.Dispose(disposing); }
}
