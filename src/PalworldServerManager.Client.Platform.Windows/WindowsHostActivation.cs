using System.ComponentModel;
using System.ServiceProcess;
using PalworldServerManager.Client.Platform.Contracts;

namespace PalworldServerManager.Client.Platform.Windows;

/// <summary>
/// Windows <see cref="IHostActivation"/> (SS2a).
///
/// Uses ONLY ServiceController.Status and .Start() - i.e. exactly the SERVICE_QUERY_STATUS and
/// SERVICE_START rights the install-time DACL grants the dedicated activation group. It never
/// calls Stop/Pause/Continue, never changes configuration, never deletes, and never hands a raw
/// SCM handle back through the contract.
///
/// Works without elevation for an authorized group member.
/// </summary>
public sealed class WindowsHostActivation : IHostActivation
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorServiceDoesNotExist = 1060;

    private readonly string _serviceName;
    private readonly Func<string, IServiceControlHandle> _open;

    public WindowsHostActivation(string serviceName = "PalworldServerManagerHost")
        : this(serviceName, static name => new ServiceControllerHandle(name))
    {
    }

    /// <summary>Test seam: lets self-tests exercise result mapping without a real service.</summary>
    public WindowsHostActivation(string serviceName, Func<string, IServiceControlHandle> open)
    {
        _serviceName = serviceName;
        _open = open;
    }

    public Task<bool> IsHostRunningAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var handle = _open(_serviceName);
            return Task.FromResult(handle.Status == HostServiceRunState.Running);
        }
        catch (Exception ex) when (IsMissingService(ex) || IsAccessDenied(ex))
        {
            // "Not running" is the honest answer for both; RequestStartAsync reports the precise
            // reason. Deliberately not throwing: a dormant or unreachable Host is an ordinary
            // state for a client to observe.
            return Task.FromResult(false);
        }
    }

    public Task<HostActivationResult> RequestStartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            using var handle = _open(_serviceName);

            // Idempotent: never issue a second Start for an already running/starting service.
            switch (handle.Status)
            {
                case HostServiceRunState.Running:
                    return Task.FromResult(HostActivationResult.AlreadyRunning);
                case HostServiceRunState.StartPending:
                    return Task.FromResult(HostActivationResult.StartRequested);
            }

            handle.Start();
            return Task.FromResult(HostActivationResult.StartRequested);
        }
        catch (Exception ex) when (IsMissingService(ex))
        {
            return Task.FromResult(HostActivationResult.ServiceNotInstalled);
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return Task.FromResult(HostActivationResult.AccessDenied);
        }
        catch (Exception)
        {
            // Bounded: the caller learns the start failed, not the SCM internals.
            return Task.FromResult(HostActivationResult.StartFailed);
        }
    }

    internal static bool IsMissingService(Exception ex) => NativeErrorCode(ex) == ErrorServiceDoesNotExist;

    internal static bool IsAccessDenied(Exception ex) => NativeErrorCode(ex) == ErrorAccessDenied;

    private static int? NativeErrorCode(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception win32)
            {
                return win32.NativeErrorCode;
            }
        }

        return null;
    }
}

public enum HostServiceRunState
{
    Stopped,
    StartPending,
    Running,
    Other,
}

/// <summary>Bounded query/start surface. Deliberately exposes no stop/configure/delete.</summary>
public interface IServiceControlHandle : IDisposable
{
    HostServiceRunState Status { get; }

    void Start();
}

internal sealed class ServiceControllerHandle : IServiceControlHandle
{
    private readonly ServiceController _controller;

    public ServiceControllerHandle(string serviceName)
    {
        _controller = new ServiceController(serviceName);

        // Touch Status so a missing service / access denial surfaces here as a Win32Exception,
        // rather than later from an unrelated member.
        _ = _controller.Status;
    }

    public HostServiceRunState Status
    {
        get
        {
            _controller.Refresh();
            return _controller.Status switch
            {
                ServiceControllerStatus.Running => HostServiceRunState.Running,
                ServiceControllerStatus.StartPending => HostServiceRunState.StartPending,
                ServiceControllerStatus.Stopped => HostServiceRunState.Stopped,
                _ => HostServiceRunState.Other,
            };
        }
    }

    public void Start() => _controller.Start();

    public void Dispose() => _controller.Dispose();
}
