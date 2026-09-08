using System.Collections.ObjectModel;

namespace PalworldServerManager.Core.Operations;

public enum RecoveryDisposition
{
    SafeToRetryFromStart = 1, SafeToResumeFromPhase = 2, RequiresManualReview = 3, SafeToDiscard = 4
}

// Trusted executor code supplies these declarations, never client request metadata. This
// describes legal transitions and recovery; it cannot execute, resume or release anything.
public sealed class OperationPhase
{
    public string Name { get; }
    public bool IsTerminal { get; }
    public RecoveryDisposition? Recovery { get; }
    public IReadOnlyList<string> NextPhases { get; }

    private OperationPhase(string name, bool terminal, RecoveryDisposition? recovery, IEnumerable<string> next)
    {
        Name = OperationDefinition.RequireName(name);
        ArgumentNullException.ThrowIfNull(next);
        var names = next.Select(OperationDefinition.RequireName).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            (terminal ? recovery is not null || names.Length != 0 : recovery is null || !Enum.IsDefined(recovery.Value) || names.Length == 0))
            throw new ArgumentException("Each phase requires an explicit valid lifecycle declaration.");
        IsTerminal = terminal; Recovery = recovery; NextPhases = Array.AsReadOnly(names);
    }

    public static OperationPhase Active(string name, RecoveryDisposition recovery, params string[] nextPhases)
        => new(name, false, recovery, nextPhases);
    public static OperationPhase Terminal(string name) => new(name, true, null, []);
}

public sealed class OperationDefinition
{
    public string Kind { get; }
    public LockRequirement LockRequirement { get; }
    public string InitialPhase { get; }
    public IReadOnlyDictionary<string, OperationPhase> Phases { get; }

    public OperationDefinition(string kind, LockRequirement lockRequirement, string initialPhase, IEnumerable<OperationPhase> phases)
    {
        Kind = RequireName(kind); InitialPhase = RequireName(initialPhase);
        if (!Enum.IsDefined(lockRequirement)) throw new ArgumentException("Unknown operation lock requirement.");
        ArgumentNullException.ThrowIfNull(phases);
        var entries = new Dictionary<string, OperationPhase>(StringComparer.Ordinal);
        foreach (var phase in phases)
        {
            if (phase is null || !entries.TryAdd(phase.Name, phase)) throw new ArgumentException("Duplicate or missing operation phase.");
        }
        if (!entries.TryGetValue(initialPhase, out var initial) || initial.IsTerminal || !entries.Values.Any(p => p.IsTerminal) ||
            entries.Values.Any(p => p.NextPhases.Any(next => !entries.ContainsKey(next))))
            throw new ArgumentException("Operation phases require a nonterminal initial phase, terminal resolution and known transitions.");
        var resolvable = entries.Values.Where(p => p.IsTerminal).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var phase in entries.Values)
                if (phase.NextPhases.Any(resolvable.Contains)) changed |= resolvable.Add(phase.Name);
        } while (changed);
        if (resolvable.Count != entries.Count) throw new ArgumentException("Every phase needs a declared path to terminal resolution.");
        LockRequirement = lockRequirement; Phases = new ReadOnlyDictionary<string, OperationPhase>(entries);
    }

    public OperationPhase GetPhase(string name)
        => Phases.TryGetValue(RequireName(name), out var phase) ? phase : throw new ArgumentException("Unknown operation phase.");

    public bool CanTransition(string from, string to)
    {
        var source = GetPhase(from); var destination = GetPhase(to);
        return source.NextPhases.Contains(destination.Name, StringComparer.Ordinal);
    }

    internal static string RequireName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || !char.IsAsciiLetter(name[0]) ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-'))
            throw new ArgumentException("Operation metadata requires a bounded code-defined name.");
        return name;
    }
}
