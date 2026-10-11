using System.Collections.ObjectModel;

namespace PalworldServerManager.Platform.Contracts;

// Host-only OS observation, not a wire DTO, retained process handle or execution permit.
public sealed class ObservedServerProcess
{
    public int ProcessId { get; }
    public DateTimeOffset CreatedUtc { get; }
    public string ImagePath { get; }
    public ObservedServerProcess(int processId, DateTimeOffset createdUtc, string imagePath)
    {
        if (processId <= 0 || createdUtc.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(imagePath) || imagePath.Contains('\0'))
            throw new ArgumentException("Valid process observation required.");
        ProcessId = processId; CreatedUtc = createdUtc; ImagePath = imagePath;
    }
}

public sealed class ServerProcessInspection
{
    public bool IsComplete { get; }
    public IReadOnlyList<ObservedServerProcess> Processes { get; }
    public ServerProcessInspection(bool isComplete, IEnumerable<ObservedServerProcess> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var copy = processes.ToArray();
        if (copy.Any(p => p is null) || copy.Select(p => p.ProcessId).Distinct().Count() != copy.Length)
            throw new ArgumentException("Distinct process observations required.");
        IsComplete = isComplete; Processes = new ReadOnlyCollection<ObservedServerProcess>(copy);
    }
}

public interface IProcessInspector
{
    // Enumerates candidate game processes across this machine. The Host must map them to
    // qualified servers and combine them with its own retained/durable lifetime state.
    // Even a complete empty snapshot is NOT proof of a safely finalized server lifetime.
    ServerProcessInspection Capture(CancellationToken ct = default);
}
