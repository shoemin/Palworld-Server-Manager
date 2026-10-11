using System.Diagnostics;
using PalworldServerManager.Platform.Contracts;
using PalworldServerManager.Platform.Windows;

namespace PalworldServerManager.SelfTest;

internal static class WindowsProcessObservationTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    private static void Check(bool condition) { if (!condition) throw new Exception("Process observation assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected process observation refusal."); }
    private sealed class Api : IWindowsProcessObservationApi
    {
        internal Func<IReadOnlyList<int>?> Enumerate = () => [12];
        internal Func<int, NativeProcessObservation> Read = id => new(ProcessObservationState.Observed, new(id, Created, @"C:\fixture\PalServer.exe"));
        public IReadOnlyList<int>? CandidateIds() => Enumerate();
        public NativeProcessObservation Observe(int processId) => Read(processId);
    }

    public static Task FailedInspectionNeverMeansEmptySuccess()
    {
        var api = new Api { Enumerate = () => null }; var inspector = new WindowsProcessInspector(api);
        Check(!inspector.Capture().IsComplete && inspector.Capture().Processes.Count == 0);
        api.Enumerate = () => [12, 13];
        api.Read = id => id == 12 ? new(ProcessObservationState.Unknown) : new(ProcessObservationState.Observed, new(id, Created, @"C:\other\PalServer.exe"));
        var partial = inspector.Capture(); Check(!partial.IsComplete && partial.Processes.Single().ProcessId == 13);
        api.Enumerate = () => [12];
        foreach (var bad in new[] { new NativeProcessObservation((ProcessObservationState)99), new(ProcessObservationState.Observed),
            new(ProcessObservationState.Observed, new(13, Created, @"C:\fixture\PalServer.exe")),
            new(ProcessObservationState.Gone, new(12, Created, @"C:\fixture\PalServer.exe")),
            new(ProcessObservationState.Observed, new(12, Created, "relative/PalServer.exe")) })
        { api.Read = _ => bad; Check(!inspector.Capture().IsComplete && inspector.Capture().Processes.Count == 0); }
        api.Enumerate = () => [0, -1, 12, 12]; api.Read = id => new(ProcessObservationState.Observed, new(id, Created, @"C:\fixture\PalServer.exe"));
        var duplicate = inspector.Capture(); Check(!duplicate.IsComplete && duplicate.Processes.Count == 1);
        return Task.CompletedTask;
    }

    public static Task ReusedPidAndExitedProcessDoNotBecomeIdentityMatches()
    {
        var api = new Api(); var inspector = new WindowsProcessInspector(api);
        api.Read = _ => new(ProcessObservationState.Observed, new(12, Created, @"C:\fixture\unrelated.exe"));
        Check(inspector.Capture().IsComplete && inspector.Capture().Processes.Count == 0);
        api.Read = _ => new(ProcessObservationState.Gone);
        Check(inspector.Capture().IsComplete && inspector.Capture().Processes.Count == 0);
        api.Read = _ => new(ProcessObservationState.Observed, new(12, Created, @"C:\fixture\pAlSeRvEr-Win64-Shipping-Cmd.exe"));
        Check(inspector.Capture().Processes.Single().CreatedUtc == Created);
        api.Read = _ => new(ProcessObservationState.Observed, new(12, Created.AddSeconds(1), @"C:\fixture\PalServer.exe"));
        Check(inspector.Capture().Processes.Single().CreatedUtc != Created); // same PID is not the old identity.
        return Task.CompletedTask;
    }

    public static Task CancellationAndImmutableObservations()
    {
        using var cancel = new CancellationTokenSource(); var api = new Api(); var inspector = new WindowsProcessInspector(api);
        api.Read = id => { cancel.Cancel(); return new(ProcessObservationState.Observed, new(id, Created, @"C:\fixture\PalServer.exe")); };
        Reject<OperationCanceledException>(() => inspector.Capture(cancel.Token));
        api.Enumerate = () => throw new Exception("Canceled enumeration ran.");
        Reject<OperationCanceledException>(() => inspector.Capture(cancel.Token));
        using var duringEnumeration = new CancellationTokenSource();
        api.Enumerate = () => { duringEnumeration.Cancel(); return null; };
        Reject<OperationCanceledException>(() => inspector.Capture(duringEnumeration.Token));
        var observation = new ObservedServerProcess(12, Created, @"C:\private-fixture\PalServer.exe");
        var source = new[] { observation }; var snapshot = new ServerProcessInspection(true, source); source[0] = null!;
        Check(ReferenceEquals(snapshot.Processes.Single(), observation));
        Reject<NotSupportedException>(() => ((IList<ObservedServerProcess>)snapshot.Processes).Clear());
        Check(!observation.ToString()!.Contains("private-fixture"));
        Reject<ArgumentException>(() => new ServerProcessInspection(true, [observation, observation]));
        Reject<ArgumentException>(() => new ServerProcessInspection(true, [null!]));
        Reject<ArgumentException>(() => new ObservedServerProcess(0, Created, "x"));
        Reject<ArgumentException>(() => new ObservedServerProcess(12, Created.ToOffset(TimeSpan.FromHours(1)), "x"));
        Reject<ArgumentException>(() => new ObservedServerProcess(12, Created, "x\0y"));
        return Task.CompletedTask;
    }

    public static async Task ActualNativeProcessesPathsTimesAndExit()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PSMProcessObservation" + Guid.NewGuid().ToString("N")));
        Process? alpha = null, other = null;
        try
        {
            var a = Path.Combine(root, "alpha"); var b = Path.Combine(root, "alpha-other");
            alpha = SyntheticPalServerHarness.Start(a, 60, 37); other = SyntheticPalServerHarness.Start(b, 60, 0);
            var inspector = new WindowsProcessInspector(); var deadline = Stopwatch.StartNew(); ServerProcessInspection snapshot;
            do
            {
                snapshot = inspector.Capture();
                if (snapshot.Processes.Any(p => p.ProcessId == alpha.Id) && snapshot.Processes.Any(p => p.ProcessId == other.Id)) break;
                await Task.Delay(50);
            } while (deadline.Elapsed < TimeSpan.FromSeconds(10));
            var first = snapshot.Processes.Single(p => p.ProcessId == alpha.Id); var second = snapshot.Processes.Single(p => p.ProcessId == other.Id);
            Check(first.ImagePath.Equals(Path.Combine(a, "PalServer.exe"), StringComparison.OrdinalIgnoreCase));
            Check(second.ImagePath.Equals(Path.Combine(b, "PalServer.exe"), StringComparison.OrdinalIgnoreCase));
            Check(first.CreatedUtc.UtcDateTime == alpha.StartTime.ToUniversalTime() && second.CreatedUtc.UtcDateTime == other.StartTime.ToUniversalTime());
            // Never terminate by a PID taken from the snapshot: these handles are our own synthetic children.
            alpha.Kill(entireProcessTree: true); await alpha.WaitForExitAsync();
            Check(!inspector.Capture().Processes.Any(p => p.ProcessId == first.ProcessId && p.CreatedUtc == first.CreatedUtc));
            Check(inspector.Capture().Processes.Any(p => p.ProcessId == other.Id));
        }
        finally
        {
            var cleanupFailed = false;
            foreach (var child in new[] { alpha, other })
            {
                if (child is null) continue;
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); if (!child.WaitForExit(10000)) cleanupFailed = true; }
                catch { cleanupFailed = true; }
                finally { child.Dispose(); }
            }
            if (cleanupFailed) throw new Exception("Synthetic process cleanup failed; fixture directory retained.");
            var allowed = Path.GetFullPath(Path.GetTempPath()) + "PSMProcessObservation";
            if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new Exception("Synthetic cleanup refused.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    public static Task ActualNativeRepeatedCaptureDisposesHandles()
    {
        var inspector = new WindowsProcessInspector();
        for (var i = 0; i < 10; i++) inspector.Capture();
        using var self = Process.GetCurrentProcess(); var before = self.HandleCount;
        for (var i = 0; i < 100; i++) inspector.Capture();
        self.Refresh(); Check(self.HandleCount <= before + 16);
        return Task.CompletedTask;
    }
}
