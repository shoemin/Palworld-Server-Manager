using System.Diagnostics;
using PalworldServerManager.Host.Persistence;

namespace PalworldServerManager.SelfTest;

// #40's machine-wide exclusivity-lock tests (SS2, SS5a, HOST-001, PERSIST-001).
//
// These spawn REAL second OS processes. In-process assertions alone cannot prove machine-wide
// exclusion, because a named mutex is re-entrant for the thread that already owns it - only a
// genuinely separate process demonstrates that a second writer is actually refused.
//
// Each test uses a unique mutex name so concurrent/repeated runs never collide with each other
// or with a real Host installation on the developer's machine.
public static class HostExclusivityLockTests
{
    private static string UniqueMutexName() => $@"Global\PSM-SelfTest-{Guid.NewGuid():N}";

    private static void True(bool condition, string what)
    {
        if (!condition)
        {
            throw new Exception($"Expected condition to hold: {what}");
        }
    }

    private static void Equal(string expected, string actual, string what)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new Exception($"{what}: expected '{expected}', got '{actual}'");
        }
    }

    private static ProcessStartInfo SelfProcess(params string[] arguments)
    {
        var exePath = Environment.ProcessPath!;
        var startInfo = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // When running via `dotnet run`/`dotnet test`, ProcessPath is the dotnet host itself, so
        // the managed entry assembly has to be passed explicitly.
        if (Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string RunSelf(params string[] arguments)
    {
        using var process = Process.Start(SelfProcess(arguments))
            ?? throw new Exception("Failed to start the self-test harness as a child process.");
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(30_000);
        return output;
    }

    // A + B + C + D: acquire, deny a second process, release, then a second process can acquire.
    public static Task TestCrossProcessExclusionAndReleaseSequence()
    {
        var mutexName = UniqueMutexName();

        using (var held = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(5), mutexName))
        {
            True(held is not null, "A. this process acquires the machine-wide lock");
            // B. a genuinely separate process is refused while A holds it.
            Equal("DENIED", RunSelf("--lock-try", mutexName), "B. second process denied while the lock is held");
        }
        // C. released deterministically by Dispose (owner thread signalled and joined).

        // D. a second process can acquire afterward.
        Equal("ACQUIRED", RunSelf("--lock-try", mutexName), "D. second process acquires after release");
        return Task.CompletedTask;
    }

    // E + F: a holder that terminates WITHOUT releasing must not wedge the lock permanently.
    //
    // H: this deliberately does not assert AbandonedMutexException. Preflight measured a case
    // where Windows did not raise it at all, so requiring it would make the test depend on
    // behavior correctness must not depend on. The requirement is simply that the next process
    // can acquire.
    public static Task TestAbandonedLockIsReacquirableWithoutRequiringAbandonedMutexException()
    {
        var mutexName = UniqueMutexName();

        // E. a child process acquires and exits without releasing.
        Equal("ACQUIRED", RunSelf("--lock-abandon", mutexName), "E. holder acquires then terminates without releasing");

        // F. the next process simply acquires it.
        using var recovered = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(5), mutexName);
        True(recovered is not null, "F. lock is acquirable after an abandoned holder");
        return Task.CompletedTask;
    }

    // G: the whole reason for the dedicated owning thread. A caller may await arbitrary work -
    // hopping threads freely - while the lease is held, and release still succeeds. Releasing a
    // thread-affine Mutex directly on an async continuation throws ApplicationException.
    public static async Task TestLeaseSurvivesAsyncThreadHops()
    {
        var mutexName = UniqueMutexName();

        var acquireThread = Environment.CurrentManagedThreadId;
        using (var lease = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(5), mutexName))
        {
            True(lease is not null, "lease acquired");

            await Task.Delay(30).ConfigureAwait(false);
            await Task.Yield();
            await Task.Delay(30).ConfigureAwait(false);

            // Still exclusive after hopping threads - the dedicated owner thread still holds it.
            Equal("DENIED", RunSelf("--lock-try", mutexName), "G. lock still held after the caller changed threads");

            // Dispose happens here, potentially on a different thread than acquisition. With a
            // naive Mutex this throws; with the dedicated owner thread it must succeed.
        }

        // Proves the release actually happened rather than being swallowed.
        Equal("ACQUIRED", RunSelf("--lock-try", mutexName), "G. lock released correctly despite thread hops");

        // Recorded for diagnostic value; the test does not require a hop to have occurred.
        _ = acquireThread;
    }

    // SS5a: Host.Cli must refuse immediately and clearly while Host holds the lock, never
    // falling back to any online mode (it has none).
    public static Task TestSecondWriterIsRefusedImmediately()
    {
        var mutexName = UniqueMutexName();

        using var hostHolds = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(5), mutexName);
        True(hostHolds is not null, "simulated Host holds the exclusivity lock");

        var started = Stopwatch.StartNew();
        var result = HostExclusivityLock.TryAcquire(TimeSpan.FromMilliseconds(250), mutexName);
        started.Stop();

        True(result is null, "a second writer is refused, never granted concurrent access");
        True(started.Elapsed < TimeSpan.FromSeconds(5), "refusal is prompt, bounded by the caller's timeout");
        return Task.CompletedTask;
    }

    // Dispose must be safe to call more than once and must not wedge the owner thread.
    public static Task TestDisposeIsIdempotent()
    {
        var mutexName = UniqueMutexName();

        var lease = HostExclusivityLock.TryAcquire(TimeSpan.FromSeconds(5), mutexName);
        True(lease is not null, "lease acquired");
        lease!.Dispose();
        lease.Dispose();

        Equal("ACQUIRED", RunSelf("--lock-try", mutexName), "lock is free after repeated Dispose");
        return Task.CompletedTask;
    }
}
