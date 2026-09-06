using System.Security.Authentication;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Grpc.Core;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Client.Security;
using PalworldServerManager.Client.Avalonia.Shell;
using PalworldServerManager.Contracts;

namespace PalworldServerManager.Client.Avalonia;

public partial class MainWindow
{
    private readonly Func<CancellationToken, Task<LocalConnectionInfo>>? _connectLocal;
    private CancellationTokenSource? _connectionStop;
    private Button _connectButton = null!, _cancelConnection = null!;
    private readonly TextBlock _connectionStatus = Text("Connect to verify this PC's Host and your local access.", "muted");
    private readonly SelectableTextBlock _connectionIdentity = new() { Name = "LocalHostIdentity", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    public LocalConnectionInfo? VerifiedConnection { get; private set; }
    public bool IsConnecting => _connectionStop is not null;
    private Control CreateConnectionControls()
    {
        _connectionStatus.Name = "LocalConnectionStatus";
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(Text("This PC", "accent")); content.Children.Add(_connectionStatus); content.Children.Add(_connectionIdentity);
        _connectButton = Action("Connect to This PC", "ConnectLocal", button => { _ = ConnectLocalAsync(); });
        _connectButton.IsEnabled = _connectLocal is not null;
        _cancelConnection = Action("Cancel connection check", "CancelLocalConnection", _ => _connectionStop?.Cancel()); _cancelConnection.IsVisible = false;
        var actions = new WrapPanel(); _connectButton.Margin = new(0, 0, 8, 8); actions.Children.Add(_connectButton); actions.Children.Add(_cancelConnection); content.Children.Add(actions);
        if (_connectLocal is null) _connectionStatus.Text = "Local connection is unavailable in this view.";
        return Card(content);
    }
    public async Task ConnectLocalAsync()
    {
        if (_closed || IsConnecting || _connectLocal is null) return;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60)); _connectionStop = stop;
        VerifiedConnection = null; _connectionIdentity.Text = null; _connectionIdentity.IsVisible = false; BindState(null);
        _connectButton.IsEnabled = false; _cancelConnection.IsVisible = true; _connectionStatus.Text = "Verifying the local Host and your access…";
        try
        {
            var result = await _connectLocal(stop.Token); stop.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            if (result.HostId == Guid.Empty || result.Identity.LocalPrincipalId == Guid.Empty) throw new InvalidDataException();
            VerifiedConnection = result;
            // No inventory protocol exists yet. Empty here means unavailable, never no servers.
            BindState(new ShellState(new HostId(result.HostId), (_, _) => Task.FromResult(false)));
            _connectionIdentity.Text = "This PC · Host: " + result.HostId.ToString("D"); _connectionIdentity.IsVisible = true;
            AutomationProperties.SetName(_connectionIdentity, _connectionIdentity.Text);
            _connectionStatus.Text = "Host and local access verified for this request. Server inventory is unavailable. Verify again to check current access.";
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        { if (!_closed) _connectionStatus.Text = ConnectionFailure(failure); }
        finally
        {
            _connectionStop = null;
            if (!_closed)
            {
                var returnFocus = _cancelConnection.IsKeyboardFocusWithin;
                _connectButton.IsEnabled = true; _cancelConnection.IsVisible = false;
                if (returnFocus) _connectButton.Focus();
            }
        }
    }
    private static string ConnectionFailure(Exception failure)
    {
        if (LocalSecurityClient.FindCause<LocalHostAuthenticationException>(failure) is not null)
            return "Local Host authentication failed. Check the machine trust configuration.";
        return failure switch
        {
            OperationCanceledException => "Connection check canceled or timed out. The Host may still be running.",
            ClientActivationException { Status: HostActivationStatus.AccessDenied } => "Starting the local Host was refused. Check OS activation eligibility.",
            ClientActivationException { Status: HostActivationStatus.ServiceMissing } => "The local Host service is not installed.",
            ClientActivationException => "The local Host could not be started.",
            LocalHostTrustUnavailableException => "Machine bootstrap has not published local trust.",
            ProtocolCompatibilityException => "The local Host protocol is incompatible.",
            AuthenticationException or RpcException { StatusCode: StatusCode.Unauthenticated or StatusCode.PermissionDenied } => "Local access was not verified. An authorized Owner may need to enroll or reauthorize this OS user.",
            UnauthorizedAccessException => "Access to the local connection or credential was refused.",
            InvalidDataException or ArgumentException or InvalidOperationException => "The local Host returned unsupported or invalid security data.",
            _ => "The local connection failed. Check the Host and try again."
        };
    }
}
