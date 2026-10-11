namespace PalworldServerManager.Platform.Contracts;

public enum ProcessAttachmentState { Unknown, Attached, Gone, Changed }
public enum ObservedProcessExitState { Unknown, Running, Exited }

public sealed class ObservedProcessExit
{
    public ObservedProcessExitState State { get; }
    public int? ExitCode { get; }
    private ObservedProcessExit(ObservedProcessExitState state, int? code = null) { State = state; ExitCode = code; }
    public static ObservedProcessExit Unknown { get; } = new(ObservedProcessExitState.Unknown);
    public static ObservedProcessExit Running { get; } = new(ObservedProcessExitState.Running);
    public static ObservedProcessExit Exited(int? exitCode) => new(ObservedProcessExitState.Exited, exitCode);
}

// A read-only observation lease. It neither owns the game's lifecycle nor permits control.
public interface IObservedProcessLease : IDisposable
{
    ObservedServerProcess Identity { get; }
    ObservedProcessExit ReadExit();
}

public sealed class ProcessAttachment
{
    public ProcessAttachmentState State { get; }
    public IObservedProcessLease? Lease { get; }
    private ProcessAttachment(ProcessAttachmentState state, IObservedProcessLease? lease = null) { State = state; Lease = lease; }
    public static ProcessAttachment Unknown { get; } = new(ProcessAttachmentState.Unknown);
    public static ProcessAttachment Gone { get; } = new(ProcessAttachmentState.Gone);
    public static ProcessAttachment Changed { get; } = new(ProcessAttachmentState.Changed);
    public static ProcessAttachment Attached(IObservedProcessLease lease)
    { ArgumentNullException.ThrowIfNull(lease); return new(ProcessAttachmentState.Attached, lease); }
}

public interface IProcessAttachment
{
    // Trusted Host input from its process observation/reconciliation, never a client PID.
    ProcessAttachment Attach(ObservedServerProcess expected, CancellationToken ct = default);
}
