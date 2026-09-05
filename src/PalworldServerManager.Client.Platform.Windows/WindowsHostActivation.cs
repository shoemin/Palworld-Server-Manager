using System.ComponentModel;
using System.ServiceProcess;
using PalworldServerManager.Client.Platform.Contracts;

namespace PalworldServerManager.Client.Platform.Windows;

public interface IWindowsActivationService : IDisposable
{
    ServiceControllerStatus Query();
    void Start();
}

public sealed class WindowsHostActivation : IHostActivation
{
    public const string ProductServiceName = "PalworldServerManagerHost";
    private readonly Func<IWindowsActivationService> _open;
    public WindowsHostActivation() : this(() => new ActivationService(ProductServiceName)) { }
    public WindowsHostActivation(Func<IWindowsActivationService> open) => _open = open;
    public Task<HostActivationResult> IsHostRunningAsync(CancellationToken ct = default) => Execute(false, ct);
    public Task<HostActivationResult> RequestStartAsync(CancellationToken ct = default) => Execute(true, ct);

    private Task<HostActivationResult> Execute(bool start, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var service = _open();
            var status = service.Query();
            var result = status switch
            {
                ServiceControllerStatus.Running => HostActivationStatus.AlreadyRunning,
                ServiceControllerStatus.StartPending => HostActivationStatus.StartRequested,
                ServiceControllerStatus.Stopped => HostActivationStatus.Stopped,
                _ => HostActivationStatus.Failed,
            };
            if (start && status == ServiceControllerStatus.Stopped)
            {
                service.Start();
                result = HostActivationStatus.StartRequested;
            }
            return Task.FromResult(new HostActivationResult(result));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            var native = ex as Win32Exception ?? ex.InnerException as Win32Exception;
            var status = native?.NativeErrorCode switch
            {
                5 => HostActivationStatus.AccessDenied,
                1060 => HostActivationStatus.ServiceMissing,
                1056 when start => HostActivationStatus.AlreadyRunning, // concurrent start won
                _ => HostActivationStatus.Failed,
            };
            return Task.FromResult(new HostActivationResult(status));
        }
    }

    public sealed class ActivationService(string serviceName) : IWindowsActivationService
    {
        private readonly ServiceController _service = new(serviceName, ".");
        public ServiceControllerStatus Query() { _service.Refresh(); return _service.Status; }
        public void Start() => _service.Start();
        public void Dispose() => _service.Dispose();
    }
}
