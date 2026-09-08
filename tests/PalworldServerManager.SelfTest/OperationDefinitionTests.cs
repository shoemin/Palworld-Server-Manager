using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Operations;

namespace PalworldServerManager.SelfTest;

internal static class OperationDefinitionTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Operation definition assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected operation refusal: " + typeof(T).Name); }

    public static Task FixedConflictHierarchyAndIndependentTargets()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var x = Guid.NewGuid(); var y = Guid.NewGuid();
        OperationLockScope[] scopes = [new HostScope(a), new ServerScope(new(a, x)), new ServerScope(new(a, y)),
            new HostScope(b), new ServerScope(new(b, x)), new ServerScope(new(b, y))];
        bool[,] expected = {
            { true, true, true, false, false, false }, { true, true, false, false, false, false },
            { true, false, true, false, false, false }, { false, false, false, true, true, true },
            { false, false, false, true, true, false }, { false, false, false, true, false, true }
        };
        for (var i = 0; i < scopes.Length; i++) for (var j = 0; j < scopes.Length; j++)
            Check(OperationLockPolicy.Conflicts(scopes[i], scopes[j]) == expected[i, j]);
        OperationTarget[] targets = [new HostTarget(a), new ServerTarget(new(a, x)), new HostTarget(b), new ServerTarget(new(b, x))];
        foreach (var target in targets)
        {
            Check(OperationLockPolicy.RequiredScope(target, LockRequirement.None) is null);
            Check(OperationLockPolicy.RequiredScope(target, LockRequirement.HostExclusive) == new HostScope(target.AuthoritativeHostId));
            if (target is ServerTarget server)
                Check(OperationLockPolicy.RequiredScope(target, LockRequirement.ServerExclusive) == new ServerScope(server.Server));
            else Reject<ArgumentException>(() => OperationLockPolicy.RequiredScope(target, LockRequirement.ServerExclusive));
            OperationLockPolicy.RequireAuthoritativeTarget(target.AuthoritativeHostId, target);
            Reject<ArgumentException>(() => OperationLockPolicy.RequireAuthoritativeTarget(Guid.NewGuid(), target));
        }
        Check(targets.Distinct().Count() == 4 && scopes.Distinct().Count() == 6);
        Check(new ServerTarget(new(a, x)) == targets[1] && new ServerTarget(new(b, x)) != targets[1]);
        return Task.CompletedTask;
    }

    public static Task ClosedIdentityAndLockInputs()
    {
        var host = Guid.NewGuid(); var target = new HostTarget(host); var scope = new HostScope(host);
        Reject<ArgumentException>(() => new HostTarget(Guid.Empty)); Reject<ArgumentException>(() => new HostScope(Guid.Empty));
        Reject<ArgumentNullException>(() => new ServerTarget(null!)); Reject<ArgumentNullException>(() => new ServerScope(null!));
        Reject<ArgumentException>(() => new ServerRef(host, Guid.Empty));
        Reject<ArgumentException>(() => OperationLockPolicy.Conflicts(null!, scope));
        Reject<ArgumentException>(() => OperationLockPolicy.Conflicts(scope, null!));
        Reject<ArgumentException>(() => OperationLockPolicy.RequiredScope(null!, LockRequirement.None));
        Reject<ArgumentException>(() => OperationLockPolicy.RequireAuthoritativeTarget(Guid.Empty, target));
        foreach (var invalid in new[] { -1, 0, 4, int.MaxValue })
            Reject<ArgumentException>(() => OperationLockPolicy.RequiredScope(target, (LockRequirement)invalid));
        Check(Enum.GetValues<LockRequirement>().Length == 3);
        // No init property can change a cloned record's Host independently of its qualified identity.
        foreach (var type in new[] { typeof(HostTarget), typeof(ServerTarget), typeof(HostScope), typeof(ServerScope) })
            Check(type.GetProperties().All(p => p.SetMethod is null));
        Check((target with { }) == target && (scope with { }) == scope);
        return Task.CompletedTask;
    }

    public static Task ExplicitPerKindRecoveryAndTransitions()
    {
        var dispositions = Enum.GetValues<RecoveryDisposition>(); Check(dispositions.Length == 4);
        foreach (var disposition in dispositions)
        {
            var definition = new OperationDefinition("Fixture_" + disposition, LockRequirement.HostExclusive, "Prepared",
                [OperationPhase.Active("Prepared", disposition, "Applied", "Failed"),
                 OperationPhase.Active("Applied", RecoveryDisposition.RequiresManualReview, "Completed", "Failed"),
                 OperationPhase.Terminal("Completed"), OperationPhase.Terminal("Failed")]);
            Check(definition.GetPhase("Prepared").Recovery == disposition);
            Check(definition.GetPhase("Applied").Recovery == RecoveryDisposition.RequiresManualReview);
            Check(definition.CanTransition("Prepared", "Applied") && definition.CanTransition("Prepared", "Failed"));
            Check(!definition.CanTransition("Prepared", "Completed") && !definition.CanTransition("Applied", "Prepared"));
            foreach (var terminal in new[] { "Completed", "Failed" })
            {
                Check(definition.GetPhase(terminal).IsTerminal && definition.GetPhase(terminal).Recovery is null);
                foreach (var destination in definition.Phases.Keys) Check(!definition.CanTransition(terminal, destination));
            }
            Reject<ArgumentException>(() => definition.GetPhase("Unknown"));
            Reject<ArgumentException>(() => definition.CanTransition("prepared", "Applied"));
            Reject<ArgumentException>(() => definition.CanTransition("Prepared", "Unknown"));
        }
        // Explicit retry loops are allowed when they still declare a resolution path.
        var retry = new OperationDefinition("RetryFixture", LockRequirement.None, "Attempt",
            [OperationPhase.Active("Attempt", RecoveryDisposition.SafeToRetryFromStart, "Attempt", "Done"), OperationPhase.Terminal("Done")]);
        Check(retry.CanTransition("Attempt", "Attempt"));
        return Task.CompletedTask;
    }

    public static Task InvalidDefinitionsAndCallerMutationCannotSupplyFallbacks()
    {
        var next = new[] { "Done" };
        var phases = new[] { OperationPhase.Active("Start", RecoveryDisposition.SafeToDiscard, next), OperationPhase.Terminal("Done") };
        var definition = new OperationDefinition("Fixture", LockRequirement.ServerExclusive, "Start", phases);
        next[0] = "Invented"; phases[0] = OperationPhase.Terminal("Other");
        Check(definition.CanTransition("Start", "Done") && definition.GetPhase("Start").Recovery == RecoveryDisposition.SafeToDiscard);
        Reject<NotSupportedException>(() => ((IList<string>)definition.GetPhase("Start").NextPhases)[0] = "Other");
        Reject<NotSupportedException>(() => ((IDictionary<string, OperationPhase>)definition.Phases).Clear());
        var good = new[] { OperationPhase.Active("Start", RecoveryDisposition.RequiresManualReview, "Done"), OperationPhase.Terminal("Done") };
        OperationDefinition Build(IEnumerable<OperationPhase> values, string initial = "Start") => new("Fixture", LockRequirement.None, initial, values);
        foreach (var bad in new[] { "", " ", "0Start", "../../file", "Has space", "Has\nnewline", "éclair", new string('A', 65), null! })
        {
            Reject<ArgumentException>(() => OperationPhase.Terminal(bad));
            Reject<ArgumentException>(() => new OperationDefinition(bad, LockRequirement.None, "Start", good));
        }
        foreach (var bad in new[] { -1, 0, 5, int.MaxValue })
            Reject<ArgumentException>(() => OperationPhase.Active("Start", (RecoveryDisposition)bad, "Done"));
        foreach (var bad in new[] { -1, 0, 4, int.MaxValue })
            Reject<ArgumentException>(() => new OperationDefinition("Fixture", (LockRequirement)bad, "Start", good));
        Reject<ArgumentException>(() => OperationPhase.Active("Start", RecoveryDisposition.RequiresManualReview));
        Reject<ArgumentException>(() => OperationPhase.Active("Start", RecoveryDisposition.RequiresManualReview, "Done", "Done"));
        Reject<ArgumentNullException>(() => OperationPhase.Active("Start", RecoveryDisposition.RequiresManualReview, null!));
        Reject<ArgumentException>(() => Build([])); Reject<ArgumentNullException>(() => Build(null!));
        Reject<ArgumentException>(() => Build([null!])); Reject<ArgumentException>(() => Build([good[0], good[0], good[1]]));
        Reject<ArgumentException>(() => Build(good, "Unknown")); Reject<ArgumentException>(() => Build(good, "Done"));
        Reject<ArgumentException>(() => Build([good[0]]));
        Reject<ArgumentException>(() => Build([OperationPhase.Active("Start", RecoveryDisposition.SafeToResumeFromPhase, "Unknown"), good[1]]));
        Reject<ArgumentException>(() => Build([OperationPhase.Active("Start", RecoveryDisposition.SafeToResumeFromPhase, "Loop"),
            OperationPhase.Active("Loop", RecoveryDisposition.SafeToResumeFromPhase, "Start"), good[1]]));
        return Task.CompletedTask;
    }
}
