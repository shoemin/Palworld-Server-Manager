using PalworldServerManager.Host;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static class HostServiceWorkerTests
{
    public static async Task Lifecycle()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = false; var failures = 0;
        using (var worker = new HostServiceWorker(async stop =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, stop); }
            finally { released = true; }
        }, () => Interlocked.Increment(ref failures), default))
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!released, "SCM callback waited for asynchronous lifetime or worker ended early.");
        }
        Check(released && failures == 0, "Normal worker stop did not finish resource cleanup.");
        foreach (var failureKind in new[] { "io", "out-of-memory", "unexpected-return" })
        {
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); released = false;
            using var worker = new HostServiceWorker(_ =>
            {
                try
                {
                    if (failureKind == "io") throw new IOException("synthetic private diagnostic");
                    if (failureKind == "out-of-memory") throw new OutOfMemoryException("synthetic worker failure");
                    return Task.CompletedTask;
                }
                finally { released = true; }
            }, () => { if (released) failed.TrySetResult(); }, default);
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using (var worker = new HostServiceWorker(stop => { stop.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            () => Interlocked.Increment(ref failures), canceled.Token)) { }
        Check(failures == 0, "Canceled startup was reported as an unexpected failure.");
    }
}
