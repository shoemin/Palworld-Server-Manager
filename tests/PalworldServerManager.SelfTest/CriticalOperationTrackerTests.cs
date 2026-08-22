using PalworldServerManager.Core.Services;

namespace PalworldServerManager.SelfTest;

internal static class CriticalOperationTrackerTests
{
    public static Task TestBeginTracksAnOperationUntilDisposed()
    {
        var tracker = new CriticalOperationTracker();
        True(!tracker.IsBusy, "a fresh tracker must not be busy");

        var lease = tracker.Begin(CriticalOperationKind.Backup, "Test Server");
        True(tracker.IsBusy, "tracker must report busy while a lease is held");
        True(tracker.ActiveOperations.Any(x => x.Contains("Backup") && x.Contains("Test Server")), "active operations must describe the kind and detail");

        lease.Dispose();
        True(!tracker.IsBusy, "tracker must not be busy after the lease is disposed");
        return Task.CompletedTask;
    }

    public static Task TestMultipleConcurrentLeasesAreAllTracked()
    {
        var tracker = new CriticalOperationTracker();
        using var a = tracker.Begin(CriticalOperationKind.Backup, "A");
        using var b = tracker.Begin(CriticalOperationKind.LanTransferReceive, "B");

        Equal(2, tracker.ActiveOperations.Count);
        True(tracker.IsBusy, "tracker must be busy while two independent leases are held");
        return Task.CompletedTask;
    }

    public static Task TestDisposingALeaseTwiceIsSafe()
    {
        var tracker = new CriticalOperationTracker();
        var lease = tracker.Begin(CriticalOperationKind.SettingsWrite);
        lease.Dispose();
        lease.Dispose(); // must not throw or double-release someone else's slot
        True(!tracker.IsBusy, "double-dispose must not corrupt tracker state");
        return Task.CompletedTask;
    }

    public static Task TestLeaseReleasesEvenWhenTheOperationThrows()
    {
        var tracker = new CriticalOperationTracker();
        try
        {
            using var lease = tracker.Begin(CriticalOperationKind.Restore, "Test Server");
            throw new InvalidOperationException("synthetic mid-operation failure");
        }
        catch (InvalidOperationException) { }

        True(!tracker.IsBusy, "a 'using' lease must release even when the operation throws");
        return Task.CompletedTask;
    }

    public static Task TestTryBeginShutdownFailsWhileAnOperationIsActive()
    {
        var tracker = new CriticalOperationTracker();
        using var lease = tracker.Begin(CriticalOperationKind.PackageImport, "incoming.palserver");

        var began = tracker.TryBeginShutdown(out var reason);
        True(!began, "shutdown must not begin while a critical operation is active");
        True(reason is not null && reason.Contains("PackageImport"), "the block reason should name the active operation: " + reason);
        return Task.CompletedTask;
    }

    public static Task TestTryBeginShutdownSucceedsWhenIdle()
    {
        var tracker = new CriticalOperationTracker();
        var began = tracker.TryBeginShutdown(out var reason);
        True(began, "shutdown must begin when nothing is active");
        True(reason is null, "no block reason should be given on success");
        return Task.CompletedTask;
    }

    public static Task TestNoNewCriticalOperationCanStartOnceShutdownIsCommitted()
    {
        var tracker = new CriticalOperationTracker();
        True(tracker.TryBeginShutdown(out _), "shutdown gate should acquire cleanly on an idle tracker");

        Exception? caught = null;
        try { tracker.Begin(CriticalOperationKind.Backup); }
        catch (InvalidOperationException ex) { caught = ex; }

        True(caught is not null, "a critical operation must be rejected once the shutdown gate is held - this closes the race where an operation could start between the apply gate check and the actual restart");
        return Task.CompletedTask;
    }

    public static Task TestCancelShutdownAllowsOperationsToResume()
    {
        var tracker = new CriticalOperationTracker();
        True(tracker.TryBeginShutdown(out _), "shutdown gate should acquire cleanly");
        tracker.CancelShutdown();

        using var lease = tracker.Begin(CriticalOperationKind.ServerStart, "Test Server");
        True(tracker.IsBusy, "operations must be allowed again after a canceled shutdown");
        return Task.CompletedTask;
    }

    public static Task TestSecondShutdownAttemptIsRejectedAsAlreadyInProgress()
    {
        var tracker = new CriticalOperationTracker();
        True(tracker.TryBeginShutdown(out _), "first shutdown attempt should succeed");

        var secondBegan = tracker.TryBeginShutdown(out var reason);
        True(!secondBegan, "a second concurrent shutdown/apply attempt must be rejected");
        True(reason is not null && reason.Contains("already in progress"), "the reason should explain an apply is already underway: " + reason);
        return Task.CompletedTask;
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected '{expected}', got '{actual}'.");
    }
}
