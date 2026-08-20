namespace PalworldServerManager.Core.Models;

public sealed class ServerProcessLifetimeEndedEventArgs : EventArgs
{
    public required Guid ServerId { get; init; }
    public required string ServerName { get; init; }
    public required bool ExpectedStop { get; init; }
    public required IReadOnlyList<ServerProcessExitInfo> ProcessExits { get; init; }
    public required string Message { get; init; }

    /// <summary>True when the server is known to have stopped (e.g. it exited while the Manager was restarting to apply its own update) but no process handle survived to capture an exit code.</summary>
    public bool ExitCodeUnavailable { get; init; }

    public bool HasNonZeroExitCode => ProcessExits.Any(x => x.ExitCode != 0);

    public int? PrimaryExitCode
    {
        get
        {
            var shipping = ProcessExits.LastOrDefault(x => x.ProcessName.Contains("Shipping", StringComparison.OrdinalIgnoreCase));
            if (shipping is not null) return shipping.ExitCode;
            return ProcessExits.LastOrDefault()?.ExitCode;
        }
    }
}

public sealed record ServerProcessExitInfo(int ProcessId, string ProcessName, int ExitCode);
